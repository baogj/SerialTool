using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SerialTool.Features;

public enum ExpectMode
{
    /// <summary>不等待应答，发送即通过</summary>
    None,
    /// <summary>应答文本包含</summary>
    Contains,
    /// <summary>应答文本匹配正则</summary>
    Regex,
    /// <summary>应答字节完全等于（HEX，?? 表示任意字节）</summary>
    ExactHex,
    /// <summary>合法 Modbus RTU 应答（CRC 正确、地址与功能码匹配、非异常）</summary>
    ModbusValid,
}

public enum TestVerdict { Pass, Fail, Error, Skipped }

/// <summary>测试步骤：发送 → 等待期望应答。</summary>
public sealed class TestStep
{
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "";
    /// <summary>发送内容；留空表示不发送、只等待应答。</summary>
    public string Send { get; set; } = "";
    public bool SendHex { get; set; }
    /// <summary>文本发送时解析 \r \n \t \xHH 转义。</summary>
    public bool Escape { get; set; } = true;
    /// <summary>发送前自动追加的校验（ChecksumKind 名称）。</summary>
    public string Checksum { get; set; } = "None";
    public ExpectMode Expect { get; set; } = ExpectMode.Contains;
    public string ExpectValue { get; set; } = "";
    public int TimeoutMs { get; set; } = 1000;
    /// <summary>本步骤结束后再等待的时间。</summary>
    public int DelayMs { get; set; }
    /// <summary>失败后的重试次数（0 = 不重试）。</summary>
    public int Retries { get; set; }
    /// <summary>失败后是否继续后续步骤；false 时整个测试中止。</summary>
    public bool ContinueOnFail { get; set; } = true;

    public TestStep Clone() => (TestStep)MemberwiseClone();
}

public sealed class TestSuite
{
    public string Name { get; set; } = "新测试";
    public string Description { get; set; } = "";
    /// <summary>文本发送 / 匹配使用的编码。</summary>
    public string Encoding { get; set; } = "UTF-8";
    public List<TestStep> Steps { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static TestSuite FromJson(string json)
    {
        var s = JsonSerializer.Deserialize<TestSuite>(json, Options) ?? throw new InvalidDataException("测试用例文件为空");
        s.Name ??= "";
        s.Description ??= "";
        s.Encoding ??= "UTF-8";
        s.Steps ??= new List<TestStep>();
        s.Steps.RemoveAll(x => x == null);
        foreach (var st in s.Steps)
        {
            st.Name ??= "";
            st.Send ??= "";
            st.Checksum ??= "None";
            st.ExpectValue ??= "";
            if (st.TimeoutMs <= 0) st.TimeoutMs = 1000;
            if (st.Retries < 0) st.Retries = 0;
            if (st.DelayMs < 0) st.DelayMs = 0;
        }
        return s;
    }

    public static TestSuite Load(string path) => FromJson(File.ReadAllText(path, System.Text.Encoding.UTF8));

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, ToJson(), new UTF8Encoding(false));
        File.Move(tmp, path, true);
    }

    /// <summary>示例：AT 模块 + Modbus 从站的冒烟测试。</summary>
    public static TestSuite Sample() => new()
    {
        Name = "示例：AT 模块 + Modbus 从站冒烟测试",
        Description = "前 3 步测试 AT 指令模块，后 2 步测试站号 1 的 Modbus RTU 从站（可用“扩展工具 → Modbus 从站”在另一个会话里模拟）。",
        Steps =
        {
            new TestStep { Name = "AT 握手", Send = "AT\\r\\n", Expect = ExpectMode.Contains, ExpectValue = "OK", TimeoutMs = 1000, Retries = 2 },
            new TestStep { Name = "查询版本", Send = "AT+GMR\\r\\n", Expect = ExpectMode.Regex, ExpectValue = @"\d+\.\d+", TimeoutMs = 2000 },
            new TestStep { Name = "信号质量", Send = "AT+CSQ\\r\\n", Expect = ExpectMode.Regex, ExpectValue = @"\+CSQ:\s*\d+", TimeoutMs = 1000, DelayMs = 100 },
            new TestStep { Name = "读保持寄存器 0~1", Send = "01 03 00 00 00 02", SendHex = true, Checksum = "ModbusCrc16", Expect = ExpectMode.ModbusValid, TimeoutMs = 500, Retries = 1 },
            new TestStep { Name = "写寄存器 1 = 0x1234（回显）", Send = "01 06 00 01 12 34", SendHex = true, Checksum = "ModbusCrc16",
                Expect = ExpectMode.ExactHex, ExpectValue = "01 06 00 01 12 34 ?? ??", TimeoutMs = 500 },
        },
    };
}

public sealed class TestStepResult
{
    public int Loop { get; init; }
    public int StepIndex { get; init; }
    public string StepName { get; init; } = "";
    public DateTime Start { get; init; }
    public double DurationMs { get; init; }
    public string SentHex { get; init; } = "";
    public string SentText { get; init; } = "";
    public string ReceivedHex { get; init; } = "";
    public string ReceivedText { get; init; } = "";
    public TestVerdict Verdict { get; init; }
    public string Message { get; init; } = "";
    public int Attempts { get; init; }

    public string VerdictText => Verdict switch
    {
        TestVerdict.Pass => "通过",
        TestVerdict.Fail => "失败",
        TestVerdict.Error => "错误",
        _ => "跳过",
    };
}

public sealed class TestReport
{
    public string SuiteName { get; init; } = "";
    public string SessionName { get; init; } = "";
    public DateTime Start { get; init; }
    public DateTime End { get; set; }
    public int Loops { get; init; }
    public bool Aborted { get; set; }
    public bool Cancelled { get; set; }
    public List<TestStepResult> Results { get; } = new();

    public int Passed => Results.Count(r => r.Verdict == TestVerdict.Pass);
    public int Failed => Results.Count(r => r.Verdict is TestVerdict.Fail or TestVerdict.Error);
    public int Skipped => Results.Count(r => r.Verdict == TestVerdict.Skipped);
    public int Executed => Passed + Failed;
    public double PassRate => Executed == 0 ? 0 : Passed * 100.0 / Executed;

    public string Summary =>
        $"共执行 {Executed} 步：通过 {Passed}，失败 {Failed}" + (Skipped > 0 ? $"，跳过 {Skipped}" : "") +
        $"，通过率 {PassRate.ToString("0.0", CultureInfo.InvariantCulture)}%" +
        (Cancelled ? "（已手动停止）" : Aborted ? "（失败后中止）" : "");

    private static string T(DateTime t) => t.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    private static string Csv(string s) =>
        s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    public string ToCsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine("循环,步骤,名称,开始时间,耗时(ms),发送HEX,发送文本,接收HEX,接收文本,结果,说明,尝试次数");
        foreach (var r in Results)
        {
            sb.AppendLine(string.Join(",", new[]
            {
                r.Loop.ToString(CultureInfo.InvariantCulture), (r.StepIndex + 1).ToString(CultureInfo.InvariantCulture), Csv(r.StepName),
                T(r.Start), r.DurationMs.ToString("0.0", CultureInfo.InvariantCulture), Csv(r.SentHex), Csv(r.SentText),
                Csv(r.ReceivedHex), Csv(r.ReceivedText), r.VerdictText, Csv(r.Message), r.Attempts.ToString(CultureInfo.InvariantCulture),
            }));
        }
        sb.AppendLine();
        sb.AppendLine($"测试,{Csv(SuiteName)}");
        sb.AppendLine($"会话,{Csv(SessionName)}");
        sb.AppendLine($"开始,{T(Start)}");
        sb.AppendLine($"结束,{T(End)}");
        sb.AppendLine($"循环次数,{Loops}");
        sb.AppendLine($"通过,{Passed}");
        sb.AppendLine($"失败,{Failed}");
        sb.AppendLine($"通过率,{PassRate.ToString("0.0", CultureInfo.InvariantCulture)}%");
        return sb.ToString();
    }

    public string ToHtml()
    {
        static string H(string s) => WebUtility.HtmlEncode(s);
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><title>测试报告 - ").Append(H(SuiteName)).Append("</title>");
        sb.Append("<style>body{font-family:'Microsoft YaHei UI',sans-serif;margin:24px;color:#1f2937}h1{font-size:20px}");
        sb.Append("table{border-collapse:collapse;width:100%;font-size:12px}th,td{border:1px solid #e5e7eb;padding:4px 6px;vertical-align:top}");
        sb.Append("th{background:#f3f4f6;text-align:left}td.mono{font-family:Consolas,monospace;word-break:break-all}");
        sb.Append("tr.pass td.v{background:#dcfce7;color:#166534;font-weight:bold}tr.fail td.v{background:#fee2e2;color:#991b1b;font-weight:bold}");
        sb.Append("tr.skip td.v{color:#6b7280}.sum{display:flex;gap:24px;margin:12px 0 18px}.sum div{padding:8px 14px;border-radius:6px;background:#f3f4f6}");
        sb.Append(".rate{font-size:22px;font-weight:bold;color:").Append(Failed == 0 && Executed > 0 ? "#16a34a" : "#dc2626").Append("}</style></head><body>");
        sb.Append("<h1>测试报告：").Append(H(SuiteName)).Append("</h1>");
        sb.Append("<p>会话：").Append(H(SessionName)).Append("　开始：").Append(T(Start)).Append("　结束：").Append(T(End))
          .Append("　循环：").Append(Loops).Append("</p>");
        sb.Append("<div class=\"sum\"><div>通过率<br><span class=\"rate\">").Append(PassRate.ToString("0.0", CultureInfo.InvariantCulture)).Append("%</span></div>");
        sb.Append("<div>执行<br><b>").Append(Executed).Append("</b></div><div>通过<br><b>").Append(Passed).Append("</b></div><div>失败<br><b>")
          .Append(Failed).Append("</b></div><div>").Append(H(Summary)).Append("</div></div>");
        sb.Append("<table><tr><th>循环</th><th>#</th><th>名称</th><th>开始时间</th><th>耗时(ms)</th><th>发送</th><th>接收</th><th>结果</th><th>说明</th><th>尝试</th></tr>");
        foreach (var r in Results)
        {
            string cls = r.Verdict == TestVerdict.Pass ? "pass" : r.Verdict == TestVerdict.Skipped ? "skip" : "fail";
            sb.Append("<tr class=\"").Append(cls).Append("\"><td>").Append(r.Loop).Append("</td><td>").Append(r.StepIndex + 1).Append("</td><td>")
              .Append(H(r.StepName)).Append("</td><td>").Append(T(r.Start)).Append("</td><td>").Append(r.DurationMs.ToString("0.0", CultureInfo.InvariantCulture))
              .Append("</td><td class=\"mono\">").Append(H(r.SentHex)).Append("</td><td class=\"mono\">").Append(H(r.ReceivedHex))
              .Append(r.ReceivedText.Length > 0 ? "<br>" + H(r.ReceivedText) : "")
              .Append("</td><td class=\"v\">").Append(r.VerdictText).Append("</td><td>").Append(H(r.Message)).Append("</td><td>").Append(r.Attempts).Append("</td></tr>");
        }
        sb.Append("</table><p style=\"color:#6b7280;font-size:11px\">SerialTool 生成</p></body></html>");
        return sb.ToString();
    }
}

/// <summary>在一个会话（IByteChannel）上执行测试用例。</summary>
public sealed class TestRunner
{
    private readonly IByteChannel _channel;

    public TestRunner(IByteChannel channel)
    {
        _channel = channel;
    }

    /// <summary>重试之间的间隔。</summary>
    public int RetryGapMs { get; set; } = 50;

    public event Action<int, int>? StepStarted;          // (loop, stepIndex)
    public event Action<TestStepResult>? StepFinished;

    public Task<TestReport> RunAsync(TestSuite suite, int loops, CancellationToken ct) =>
        Task.Run(() => Run(suite, loops, ct), CancellationToken.None);

    public TestReport Run(TestSuite suite, int loops, CancellationToken ct)
    {
        loops = Math.Max(1, loops);
        var report = new TestReport { SuiteName = suite.Name, SessionName = _channel.Name, Start = DateTime.Now, Loops = loops };
        var enc = EncodingUtil.Get(suite.Encoding);
        using var rx = new RxCollector(_channel);
        try
        {
            for (int loop = 1; loop <= loops && !report.Aborted; loop++)
            {
                for (int i = 0; i < suite.Steps.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var step = suite.Steps[i];
                    StepStarted?.Invoke(loop, i);
                    var r = RunStep(step, loop, i, enc, rx, ct);
                    report.Results.Add(r);
                    StepFinished?.Invoke(r);
                    if (r.Verdict is TestVerdict.Fail or TestVerdict.Error && !step.ContinueOnFail)
                    {
                        report.Aborted = true;
                        break;
                    }
                    if (step.Enabled && step.DelayMs > 0) Sleep(step.DelayMs, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            report.Cancelled = true;
        }
        report.End = DateTime.Now;
        return report;
    }

    private static void Sleep(int ms, CancellationToken ct)
    {
        if (ct.WaitHandle.WaitOne(ms)) ct.ThrowIfCancellationRequested();
    }

    private TestStepResult RunStep(TestStep step, int loop, int index, Encoding enc, RxCollector rx, CancellationToken ct)
    {
        var start = DateTime.Now;
        if (!step.Enabled)
            return new TestStepResult { Loop = loop, StepIndex = index, StepName = step.Name, Start = start, Verdict = TestVerdict.Skipped, Message = "未启用" };

        byte[] data = Array.Empty<byte>();
        if (!string.IsNullOrEmpty(step.Send) &&
            !SessionCore.BuildPayload(step.Send, step.SendHex, step.Escape, SessionCore.ParseChecksum(step.Checksum), null, enc,
                out data, out _, out _, out string? buildErr))
        {
            return new TestStepResult { Loop = loop, StepIndex = index, StepName = step.Name, Start = start, Verdict = TestVerdict.Error, Message = "发送内容无效：" + buildErr };
        }
        Regex? regex = null;
        if (step.Expect == ExpectMode.Regex)
        {
            try { regex = new Regex(step.ExpectValue, RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)); }
            catch (ArgumentException ex)
            {
                return new TestStepResult { Loop = loop, StepIndex = index, StepName = step.Name, Start = start, Verdict = TestVerdict.Error, Message = "正则无效：" + ex.Message };
            }
        }
        byte?[]? exact = null;
        if (step.Expect == ExpectMode.ExactHex && !TryParseWildHex(step.ExpectValue, out exact))
            return new TestStepResult { Loop = loop, StepIndex = index, StepName = step.Name, Start = start, Verdict = TestVerdict.Error, Message = "期望 HEX 无效" };

        int attempts = 0;
        TestVerdict verdict = TestVerdict.Fail;
        string msg = "";
        byte[] got = Array.Empty<byte>();
        var sw = Stopwatch.StartNew();
        var attemptStart = start;
        for (int attempt = 0; attempt <= Math.Max(0, step.Retries); attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (attempt > 0 && RetryGapMs > 0) Sleep(RetryGapMs, ct);
            attempts++;
            rx.Clear();
            long ver = rx.Version;
            sw.Restart();
            attemptStart = DateTime.Now;
            if (data.Length > 0 && !_channel.Send(data))
            {
                verdict = TestVerdict.Error;
                msg = _channel.IsOpen ? "发送失败（队列已满）" : "会话未连接";
                got = Array.Empty<byte>();
                if (!_channel.IsOpen) break;
                continue;
            }
            if (step.Expect == ExpectMode.None)
            {
                verdict = TestVerdict.Pass;
                msg = data.Length > 0 ? "已发送" : "";
                break;
            }
            while (true)
            {
                got = rx.Snapshot();
                var (state, m) = Evaluate(step, regex, exact, data, got, enc, false);
                if (state != EvalState.Pending)
                {
                    verdict = state == EvalState.Pass ? TestVerdict.Pass : TestVerdict.Fail;
                    msg = m;
                    break;
                }
                int left = step.TimeoutMs - (int)sw.ElapsedMilliseconds;
                if (left <= 0)
                {
                    verdict = TestVerdict.Fail;
                    msg = got.Length == 0 ? $"超时：{step.TimeoutMs} ms 内没有收到数据" : $"超时：{step.TimeoutMs} ms 内未收到期望应答" + (m.Length > 0 ? "（" + m + "）" : "");
                    break;
                }
                ver = rx.Version;
                // 再检查一次，避免在取快照与记录版本之间到达的数据被漏掉
                if (rx.Count != got.Length) continue;
                rx.WaitForData(ver, left, ct);
            }
            if (verdict == TestVerdict.Pass) break;
        }
        if (verdict == TestVerdict.Pass && attempts > 1) msg = (msg.Length > 0 ? msg + "；" : "") + $"第 {attempts} 次尝试通过";

        return new TestStepResult
        {
            Loop = loop, StepIndex = index, StepName = step.Name, Start = attemptStart, DurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 1),
            SentHex = Checksum.ToHex(data), SentText = step.SendHex ? "" : SafeText(enc, data),
            ReceivedHex = Checksum.ToHex(got.Length > 4096 ? got.Take(4096).ToArray() : got),
            ReceivedText = SafeText(enc, got.Length > 4096 ? got.Take(4096).ToArray() : got),
            Verdict = verdict, Message = msg, Attempts = attempts,
        };
    }

    private static string SafeText(Encoding enc, byte[] d)
    {
        if (d.Length == 0) return "";
        var s = enc.GetString(d);
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c == '\r') sb.Append("\\r");
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\t') sb.Append("\\t");
            else if (char.IsControl(c)) sb.Append('·');
            else sb.Append(c);
        }
        return sb.ToString();
    }

    public enum EvalState { Pending, Pass, Fail }

    /// <summary>判定当前收到的数据。Pending = 还需要更多数据。</summary>
    public static (EvalState, string) Evaluate(TestStep step, Regex? regex, byte?[]? exact, byte[] sent, byte[] got, Encoding enc, bool final)
    {
        switch (step.Expect)
        {
            case ExpectMode.None:
                return (EvalState.Pass, "");
            case ExpectMode.Contains:
                return enc.GetString(got).Contains(step.ExpectValue, StringComparison.Ordinal) ? (EvalState.Pass, "包含“" + step.ExpectValue + "”") : (EvalState.Pending, "");
            case ExpectMode.Regex:
            {
                var m = regex!.Match(enc.GetString(got));
                return m.Success ? (EvalState.Pass, "匹配：" + m.Value) : (EvalState.Pending, "");
            }
            case ExpectMode.ExactHex:
            {
                var e = exact!;
                int n = Math.Min(e.Length, got.Length);
                for (int i = 0; i < n; i++)
                    if (e[i].HasValue && e[i]!.Value != got[i])
                        return (EvalState.Fail, $"第 {i + 1} 字节不一致：收到 {got[i]:X2}，期望 {e[i]!.Value:X2}");
                if (got.Length < e.Length) return (EvalState.Pending, got.Length > 0 ? $"只收到 {got.Length}/{e.Length} 字节" : "");
                if (got.Length > e.Length) return (EvalState.Fail, $"多出 {got.Length - e.Length} 字节");
                return (EvalState.Pass, "完全一致");
            }
            case ExpectMode.ModbusValid:
                return EvaluateModbus(sent, got);
            default:
                return (EvalState.Fail, "未知判定方式");
        }
    }

    private static (EvalState, string) EvaluateModbus(byte[] sent, byte[] got)
    {
        if (sent.Length < 4) return (EvalState.Fail, "发送内容不是 Modbus RTU 请求");
        if (got.Length < 5) return (EvalState.Pending, got.Length > 0 ? $"只收到 {got.Length} 字节" : "");
        byte unit = sent[0], fc = sent[1];
        if (got[0] != unit) return (EvalState.Fail, $"站号不符：收到 {got[0]}，期望 {unit}");
        if (got[1] == (fc | 0x80))
        {
            if (!ModbusGatewayCodec.CrcOk(got, 5)) return (EvalState.Fail, "异常应答 CRC 错误");
            return (EvalState.Fail, $"Modbus 异常应答：{got[2]:X2} {ModbusMaster.ExceptionName(got[2])}");
        }
        if (got[1] != fc) return (EvalState.Fail, $"功能码不符：收到 {got[1]:X2}，期望 {fc:X2}");
        int len = ModbusGatewayCodec.RtuResponseLength(got);
        if (len == 0) return (EvalState.Pending, "");
        if (len < 0)
        {
            // 未知功能码：任意长度 CRC 正确即可
            for (int n = 4; n <= got.Length; n++)
                if (ModbusGatewayCodec.CrcOk(got, n)) return n == got.Length ? (EvalState.Pass, "CRC 正确") : (EvalState.Fail, $"多出 {got.Length - n} 字节");
            return (EvalState.Pending, "");
        }
        if (got.Length < len) return (EvalState.Pending, $"只收到 {got.Length}/{len} 字节");
        if (!ModbusGatewayCodec.CrcOk(got, len)) return (EvalState.Fail, "CRC 错误");
        if (got.Length > len) return (EvalState.Fail, $"多出 {got.Length - len} 字节");
        return (EvalState.Pass, "合法 Modbus 应答");
    }

    /// <summary>解析带通配符的 HEX：?? 或 XX 表示任意字节。</summary>
    public static bool TryParseWildHex(string text, out byte?[] bytes)
    {
        var list = new List<byte?>();
        bytes = Array.Empty<byte?>();
        var s = (text ?? "").Replace(",", " ").Replace("0x", " ").Replace("0X", " ");
        foreach (var tok0 in s.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var tok = tok0;
            if (tok.Length % 2 != 0) return false;
            for (int i = 0; i < tok.Length; i += 2)
            {
                var p = tok.Substring(i, 2);
                if (p == "??" || p.Equals("XX", StringComparison.OrdinalIgnoreCase)) list.Add(null);
                else if (byte.TryParse(p, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b)) list.Add(b);
                else return false;
            }
        }
        bytes = list.ToArray();
        return bytes.Length > 0;
    }
}
