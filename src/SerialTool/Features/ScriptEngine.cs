using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SerialTool.Features;

/// <summary>
/// 轻量脚本语言（无第三方依赖，适合单文件发布）：
///   SEND text          文本发送（支持 \r \n \t \0 \\ \xHH），追加校验 + 行尾
///   SENDHEX aa bb cc   HEX 发送，追加校验（不加行尾）
///   SENDHEXRAW aa bb   HEX 原样发送（不加校验）
///   CHECKSUM MODBUS    本脚本内的校验覆盖：NONE / MODBUS / XMODEM / CCITT / SUM / XOR / DEFAULT（跟随界面）
///   SLEEP ms
///   WAITFOR pattern timeout_ms   等待 RX 文本包含 pattern（字面量）
///   WAITREGEX pattern timeout_ms 正则等待
///   LOG message
///   # 注释
/// </summary>
public sealed class ScriptEngine
{
    public interface IHost
    {
        bool IsPortOpen { get; }
        string? PortName { get; }
        /// <param name="checksum">null = 跟随界面“校验”设置</param>
        bool SendText(string text, bool escape, ChecksumKind? checksum, out string? error);
        /// <param name="raw">true = 不追加校验</param>
        bool SendHex(string hex, ChecksumKind? checksum, bool raw, out string? error);
        void Log(string message);
        Encoding Encoding { get; }
    }

    private readonly IHost _host;
    private readonly ConcurrentQueue<string> _rxLines = new();
    private readonly object _rxLock = new();
    private string _rxAccum = "";
    private CancellationTokenSource? _cts;
    private Task? _running;

    public bool IsRunning => _running != null && !_running.IsCompleted;
    public event Action<string>? Output;
    public event Action<bool>? RunningChanged;

    public ScriptEngine(IHost host) => _host = host;

    public void FeedRx(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        lock (_rxLock)
        {
            _rxAccum += text;
            if (_rxAccum.Length > 512_000)
                _rxAccum = _rxAccum.Substring(_rxAccum.Length - 256_000);
        }
        foreach (var line in text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None))
        {
            if (line.Length > 0) _rxLines.Enqueue(line);
        }
    }

    public void FeedRxBytes(byte[] data)
    {
        try { FeedRx(_host.Encoding.GetString(data)); }
        catch { /* ignore decode errors */ }
    }

    public void ClearRxBuffer()
    {
        lock (_rxLock) _rxAccum = "";
        while (_rxLines.TryDequeue(out _)) { }
    }

    public async Task RunAsync(string script)
    {
        if (IsRunning) throw new InvalidOperationException("脚本已在运行");
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        ClearRxBuffer();
        _running = Task.Run(() => Execute(script, token), token);
        RunningChanged?.Invoke(true);
        try
        {
            await _running.ConfigureAwait(false);
        }
        finally
        {
            RunningChanged?.Invoke(false);
            _running = null;
        }
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
    }

    private void Emit(string msg) => Output?.Invoke(msg);

    private void Execute(string script, CancellationToken token)
    {
        var lines = script.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int i = 0;
        ChecksumKind? checksum = null; // 脚本内覆盖
        try
        {
            Emit($"--- 脚本开始（{lines.Length} 行）---");
            foreach (string raw in lines)
            {
                token.ThrowIfCancellationRequested();
                i++;
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//")) continue;

                int sp = line.IndexOf(' ');
                string cmd = (sp < 0 ? line : line.Substring(0, sp)).ToUpperInvariant();
                string arg = sp < 0 ? "" : line.Substring(sp + 1).Trim();

                switch (cmd)
                {
                    case "LOG":
                        Emit("[LOG] " + arg);
                        _host.Log(arg);
                        break;
                    case "SLEEP":
                        if (!int.TryParse(arg, out int ms) || ms < 0)
                            throw new Exception($"SLEEP 参数无效：{arg}");
                        Emit($"SLEEP {ms} ms");
                        Sleep(ms, token);
                        break;
                    case "SEND":
                        if (!_host.IsPortOpen) throw new Exception("串口未打开");
                        Emit("SEND " + Visible(arg));
                        if (!_host.SendText(arg, true, checksum, out string? terr))
                            throw new Exception(terr ?? "发送失败");
                        break;
                    case "SENDHEX":
                    case "SENDHEXRAW":
                        {
                            if (!_host.IsPortOpen) throw new Exception("串口未打开");
                            bool rawHex = cmd == "SENDHEXRAW";
                            Emit(cmd + " " + arg);
                            if (!_host.SendHex(arg, checksum, rawHex, out string? err))
                                throw new Exception(err ?? "HEX 发送失败");
                            break;
                        }
                    case "CHECKSUM":
                        if (arg.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase) || arg.Length == 0)
                        {
                            checksum = null;
                            Emit("CHECKSUM → 跟随界面设置");
                        }
                        else if (Checksum.TryParseShortName(arg, out var ck))
                        {
                            checksum = ck;
                            Emit("CHECKSUM → " + Checksum.DisplayName(ck));
                        }
                        else
                        {
                            throw new Exception($"未知校验：{arg}（可用 NONE/MODBUS/XMODEM/CCITT/SUM/XOR/DEFAULT）");
                        }
                        break;
                    case "WAITFOR":
                        {
                            ParseWait(arg, out string pattern, out int timeout);
                            Emit($"WAITFOR \"{pattern}\" ({timeout} ms)");
                            WaitFor(pattern, timeout, false, token);
                            Emit("  → 匹配成功");
                            break;
                        }
                    case "WAITREGEX":
                        {
                            ParseWait(arg, out string pattern, out int timeout);
                            Emit($"WAITREGEX /{pattern}/ ({timeout} ms)");
                            WaitFor(pattern, timeout, true, token);
                            Emit("  → 匹配成功");
                            break;
                        }
                    case "CLEARRX":
                        ClearRxBuffer();
                        Emit("CLEARRX");
                        break;
                    case "STATUS":
                        Emit($"STATUS port={_host.PortName ?? "(未打开)"} open={_host.IsPortOpen}");
                        break;
                    default:
                        throw new Exception($"未知命令：{cmd}");
                }
            }
            Emit("--- 脚本完成 ---");
        }
        catch (OperationCanceledException)
        {
            Emit($"--- 脚本已停止（行 {i}）---");
        }
        catch (Exception ex)
        {
            Emit($"--- 脚本错误（行 {i}）：{ex.Message} ---");
        }
    }

    private static string Visible(string s) =>
        s.Replace("\r", "\\r").Replace("\n", "\\n");

    private static void ParseWait(string arg, out string pattern, out int timeout)
    {
        // 最后一段为 timeout，前面为 pattern
        arg = arg.Trim();
        int lastSp = arg.LastIndexOf(' ');
        if (lastSp <= 0 || !int.TryParse(arg.Substring(lastSp + 1), out timeout) || timeout < 1)
            throw new Exception("WAITFOR/WAITREGEX 用法：WAITFOR pattern timeout_ms");
        pattern = arg.Substring(0, lastSp).Trim().Trim('"');
        if (pattern.Length == 0) throw new Exception("等待模式为空");
    }

    private void WaitFor(string pattern, int timeoutMs, bool regex, CancellationToken token)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Regex? re = regex ? new Regex(pattern, RegexOptions.CultureInvariant) : null;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            token.ThrowIfCancellationRequested();
            string snap;
            lock (_rxLock) snap = _rxAccum;
            if (regex)
            {
                if (re!.IsMatch(snap)) return;
            }
            else if (snap.Contains(pattern, StringComparison.Ordinal))
            {
                return;
            }
            Thread.Sleep(20);
        }
        throw new Exception($"等待超时（{timeoutMs} ms）：未出现 \"{pattern}\"");
    }

    private static void Sleep(int ms, CancellationToken token)
    {
        int left = ms;
        while (left > 0)
        {
            token.ThrowIfCancellationRequested();
            int step = Math.Min(50, left);
            Thread.Sleep(step);
            left -= step;
        }
    }

    public static string SampleAtPoll =>
@"# AT 轮询示例：发送 AT，等待 OK
STATUS
CLEARRX
SEND AT\r\n
WAITFOR OK 2000
SLEEP 300
SEND AT+GMR\r\n
WAITFOR OK 3000
LOG AT 轮询完成
";

    public static string SampleModbusRead =>
@"# Modbus RTU 读保持寄存器：从站1，起始地址0，数量10
# CHECKSUM MODBUS 让 SENDHEX 自动追加 CRC（01 03 00 00 00 0A → C5 CD）
STATUS
CHECKSUM MODBUS
CLEARRX
SENDHEX 01 03 00 00 00 0A
SLEEP 500
# 不加校验的原样发送：
# SENDHEXRAW 01 03 00 00 00 01 84 0A
LOG 已发送读保持寄存器请求（在「协议：Modbus RTU」下查看帧解析）
";
}
