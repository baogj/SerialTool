using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MoonSharp.Interpreter;

namespace SerialTool.Features;

public enum LuaRunStatus { Ok, Cancelled, Error }

public sealed class LuaRunResult
{
    public LuaRunStatus Status { get; init; }
    public string Message { get; init; } = "";
    /// <summary>出错行号（未知为 0）。</summary>
    public int Line { get; init; }

    public override string ToString() => Status switch
    {
        LuaRunStatus.Ok => "脚本执行完毕",
        LuaRunStatus.Cancelled => "脚本已停止",
        _ => Line > 0 ? $"第 {Line} 行出错：{Message}" : "脚本出错：" + Message,
    };
}

internal sealed class LuaCancelledException : Exception
{
    public LuaCancelledException() : base("脚本已停止") { }
}

/// <summary>
/// Lua 脚本宿主（MoonSharp）：在后台线程执行脚本，提供 send / sendhex / read / waitfor / sleep / log / crc16 等 API，
/// 以及 on_receive(data) 回调、status 状态变量与 session 表。字节串约定：每个字符代表一个字节（0~255）。
/// </summary>
public sealed class LuaScriptHost
{
    private readonly IByteChannel _channel;
    private readonly ConcurrentQueue<byte[]> _pending = new();
    private RxCollector? _rx;
    private CancellationTokenSource? _cts;
    private Thread? _thread;
    private Script? _script;
    private bool _inCallback;
    private Stopwatch _clock = new();
    private string _lastStatus = "";

    public LuaScriptHost(IByteChannel channel, Encoding? encoding = null)
    {
        _channel = channel;
        Encoding = encoding ?? new UTF8Encoding(false);
    }

    public Encoding Encoding { get; set; }
    /// <summary>每执行多少条指令检查一次停止请求。</summary>
    public long AutoYieldCounter { get; set; } = 1000;
    /// <summary>脚本主体结束后，若定义了 on_receive，是否继续运行以处理接收回调（直到停止）。</summary>
    public bool KeepAliveForCallbacks { get; set; } = true;

    public bool IsRunning => _thread != null;
    public string Status => _lastStatus;

    /// <summary>脚本输出（log / print / 错误）。</summary>
    public event Action<string>? Output;
    public event Action<string>? StatusChanged;
    public event Action<bool>? RunningChanged;
    public event Action<LuaRunResult>? Completed;

    /// <summary>在后台线程运行脚本；完成后返回结果。</summary>
    public Task<LuaRunResult> RunAsync(string code)
    {
        if (_thread != null) throw new InvalidOperationException("脚本正在运行");
        var tcs = new TaskCompletionSource<LuaRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _thread = new Thread(() =>
        {
            LuaRunResult r;
            try { r = Execute(code, ct); }
            catch (Exception ex) { r = new LuaRunResult { Status = LuaRunStatus.Error, Message = ex.Message }; }
            _thread = null;
            RunningChanged?.Invoke(false);
            Completed?.Invoke(r);
            tcs.TrySetResult(r);
        })
        { IsBackground = true, Name = "Lua 脚本" };
        RunningChanged?.Invoke(true);
        _thread.Start();
        return tcs.Task;
    }

    public void Stop() => _cts?.Cancel();

    /// <summary>同步执行（调用线程即脚本线程）。</summary>
    public LuaRunResult Execute(string code, CancellationToken ct)
    {
        _rx = new RxCollector();
        while (_pending.TryDequeue(out _)) { }
        _channel.Received += OnReceived;
        _clock = Stopwatch.StartNew();
        _lastStatus = "";
        try
        {
            var script = new Script(CoreModules.Preset_SoftSandbox);
            _script = script;
            script.Options.DebugPrint = s => Output?.Invoke(s);
            Register(script, ct);
            DynValue fn;
            try { fn = script.LoadString(code, null, "script"); }
            catch (InterpreterException ex) { return MakeError(ex); }

            RunCancellable(fn, ct);
            PollStatus();

            var onRecv = script.Globals.Get("on_receive");
            if (onRecv.Type == DataType.Function && KeepAliveForCallbacks)
            {
                Output?.Invoke("主体执行完毕，正在等待接收数据（on_receive）……");
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    DispatchPending(ct);
                    if (_pending.IsEmpty) _rx.WaitForData(_rx.Version, 100, ct);
                }
            }
            return new LuaRunResult { Status = LuaRunStatus.Ok };
        }
        catch (Exception ex) when (ct.IsCancellationRequested || ex is LuaCancelledException || ex is OperationCanceledException || ex.InnerException is LuaCancelledException or OperationCanceledException)
        {
            return new LuaRunResult { Status = LuaRunStatus.Cancelled, Message = "脚本已停止" };
        }
        catch (InterpreterException ex)
        {
            return MakeError(ex);
        }
        catch (Exception ex)
        {
            return new LuaRunResult { Status = LuaRunStatus.Error, Message = ex.Message };
        }
        finally
        {
            _channel.Received -= OnReceived;
            _script = null;
            PollStatusSafe();
        }
    }

    private void PollStatusSafe()
    {
        try { PollStatus(); } catch { }
    }

    private static readonly Regex LineRx = new(@"\((\d+),", RegexOptions.Compiled);

    public static int ParseLine(string? decorated)
    {
        if (string.IsNullOrEmpty(decorated)) return 0;
        var m = LineRx.Match(decorated);
        return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }

    private static LuaRunResult MakeError(InterpreterException ex)
    {
        string dec = ex.DecoratedMessage ?? ex.Message;
        int line = ParseLine(dec);
        string msg = ex.Message;
        if (ex is SyntaxErrorException) msg = "语法错误：" + msg;
        return new LuaRunResult { Status = LuaRunStatus.Error, Message = msg, Line = line };
    }

    private void OnReceived(byte[] data)
    {
        _rx?.Append(data);
        if (_script != null && _pending.Count < 10000) _pending.Enqueue(data);
    }

    // ------------------------------------------------------------------
    // 执行辅助
    // ------------------------------------------------------------------

    /// <summary>以协程方式执行函数，以便打断死循环。</summary>
    private DynValue RunCancellable(DynValue fn, CancellationToken ct, params DynValue[] args)
    {
        var script = _script!;
        var co = script.CreateCoroutine(fn);
        co.Coroutine.AutoYieldCounter = AutoYieldCounter;
        var r = co.Coroutine.Resume(args);
        while (r.Type == DataType.YieldRequest)
        {
            if (ct.IsCancellationRequested) throw new LuaCancelledException();
            PollStatus();
            r = co.Coroutine.Resume();
        }
        return r;
    }

    private void PollStatus()
    {
        var s = _script;
        if (s == null) return;
        var v = s.Globals.Get("status");
        string text = v.IsNil() ? "" : (v.CastToString() ?? v.ToPrintString());
        if (text != _lastStatus)
        {
            _lastStatus = text;
            StatusChanged?.Invoke(text);
        }
    }

    /// <summary>把排队的接收数据交给 on_receive。</summary>
    private void DispatchPending(CancellationToken ct)
    {
        if (_inCallback || _script == null) return;
        var fn = _script.Globals.Get("on_receive");
        if (fn.Type != DataType.Function)
        {
            while (_pending.TryDequeue(out _)) { }
            return;
        }
        _inCallback = true;
        try
        {
            while (_pending.TryDequeue(out var d))
            {
                ct.ThrowIfCancellationRequested();
                RunCancellable(fn, ct, DynValue.NewString(ToByteString(d)));
                PollStatus();
            }
        }
        finally
        {
            _inCallback = false;
        }
    }

    private void Check(CancellationToken ct)
    {
        if (ct.IsCancellationRequested) throw new LuaCancelledException();
    }

    /// <summary>可取消地等待，同时派发 on_receive。</summary>
    private void Pause(int ms, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            Check(ct);
            DispatchPending(ct);
            int left = ms - (int)sw.ElapsedMilliseconds;
            if (left <= 0) return;
            ct.WaitHandle.WaitOne(Math.Min(left, 20));
        }
    }

    // ------------------------------------------------------------------
    // 字节串
    // ------------------------------------------------------------------

    public static string ToByteString(byte[] d)
    {
        var c = new char[d.Length];
        for (int i = 0; i < d.Length; i++) c[i] = (char)d[i];
        return new string(c);
    }

    /// <summary>Lua 字符串 → 字节：全部字符 ≤ 0xFF 时按字节串处理，否则按会话编码编码。</summary>
    public byte[] FromLuaString(string s)
    {
        if (s.All(ch => ch <= 0xFF))
        {
            var b = new byte[s.Length];
            for (int i = 0; i < s.Length; i++) b[i] = (byte)s[i];
            return b;
        }
        return Encoding.GetBytes(s);
    }

    private byte[] ArgBytes(CallbackArguments args, int i, string fname)
    {
        var v = args[i];
        if (v.Type == DataType.String) return FromLuaString(v.String);
        if (v.Type == DataType.Number) return new[] { (byte)(int)v.Number };
        if (v.Type == DataType.Table)
        {
            var list = new List<byte>();
            foreach (var x in v.Table.Values)
            {
                if (x.Type != DataType.Number) throw new ScriptRuntimeException($"{fname}：表中只能是数字");
                list.Add((byte)((int)x.Number & 0xFF));
            }
            return list.ToArray();
        }
        throw new ScriptRuntimeException($"{fname}：第 {i + 1} 个参数应为字符串或字节表");
    }

    private static int ArgInt(CallbackArguments args, int i, int def)
    {
        var v = args[i];
        if (v.IsNil()) return def;
        var n = v.CastToNumber();
        if (n == null) throw new ScriptRuntimeException($"第 {i + 1} 个参数应为数字");
        return (int)n.Value;
    }

    private static bool TryKind(string s, out ChecksumKind k) =>
        Checksum.TryParseShortName(s, out k) || Enum.TryParse(s.Trim(), true, out k);

    private static ChecksumKind ArgChecksum(CallbackArguments args, int i)
    {
        var v = args[i];
        if (v.IsNil()) return ChecksumKind.None;
        if (!TryKind(v.CastToString() ?? "", out var k)) throw new ScriptRuntimeException("未知校验类型：" + v.CastToString() + "（可用 modbus / xmodem / ccitt / sum8 / xor8）");
        return k;
    }

    // ------------------------------------------------------------------
    // API
    // ------------------------------------------------------------------

    private void Register(Script script, CancellationToken ct)
    {
        var g = script.Globals;
        void Fn(string name, Func<CallbackArguments, DynValue> f) =>
            g[name] = DynValue.NewCallback((ctx, args) => { Check(ct); return f(args); }, name);

        Fn("send", a =>
        {
            var data = ArgBytes(a, 0, "send");
            data = Checksum.Append(ArgChecksum(a, 1), data);
            DispatchPending(ct);
            return DynValue.NewBoolean(_channel.Send(data));
        });
        Fn("sendhex", a =>
        {
            var s = a[0].CastToString() ?? "";
            if (!HexUtil.TryParse(s, out var data, out var err)) throw new ScriptRuntimeException("sendhex：" + err);
            data = Checksum.Append(ArgChecksum(a, 1), data);
            return DynValue.NewBoolean(_channel.Send(data));
        });
        Fn("read", a =>
        {
            int timeout = ArgInt(a, 0, 1000), min = Math.Max(1, ArgInt(a, 1, 1));
            var sw = Stopwatch.StartNew();
            while (true)
            {
                Check(ct);
                DispatchPending(ct);
                if (_rx!.Count >= min) return DynValue.NewString(ToByteString(_rx.Take()));
                int left = timeout - (int)sw.ElapsedMilliseconds;
                if (left <= 0) return DynValue.Nil;
                _rx.WaitForData(_rx.Version, Math.Min(left, 20), ct);
            }
        });
        Fn("waitfor", a =>
        {
            var pat = a[0].CastToString() ?? "";
            int timeout = ArgInt(a, 1, 1000);
            var pb = FromLuaString(pat);
            var sw = Stopwatch.StartNew();
            while (true)
            {
                Check(ct);
                DispatchPending(ct);
                var buf = _rx!.Snapshot();
                int idx = pb.Length == 0 ? 0 : IndexOf(buf, pb);
                if (idx >= 0)
                {
                    int end = idx + pb.Length;
                    _rx.Consume(end);
                    return DynValue.NewString(Encoding.GetString(buf, 0, end));
                }
                int left = timeout - (int)sw.ElapsedMilliseconds;
                if (left <= 0) return DynValue.Nil;
                _rx.WaitForData(_rx.Version, Math.Min(left, 20), ct);
            }
        });
        Fn("waitregex", a =>
        {
            var pat = a[0].CastToString() ?? "";
            int timeout = ArgInt(a, 1, 1000);
            Regex rx;
            try { rx = new Regex(pat, RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)); }
            catch (ArgumentException ex) { throw new ScriptRuntimeException("waitregex：正则无效 " + ex.Message); }
            var sw = Stopwatch.StartNew();
            while (true)
            {
                Check(ct);
                DispatchPending(ct);
                var buf = _rx!.Snapshot();
                var text = Encoding.GetString(buf);
                var m = rx.Match(text);
                if (m.Success)
                {
                    _rx.Consume(Encoding.GetByteCount(text.Substring(0, m.Index + m.Length)));
                    var vals = new List<DynValue> { DynValue.NewString(m.Value) };
                    for (int gi = 1; gi < m.Groups.Count; gi++) vals.Add(DynValue.NewString(m.Groups[gi].Value));
                    return DynValue.NewTuple(vals.ToArray());
                }
                int left = timeout - (int)sw.ElapsedMilliseconds;
                if (left <= 0) return DynValue.Nil;
                _rx.WaitForData(_rx.Version, Math.Min(left, 20), ct);
            }
        });
        Fn("sleep", a =>
        {
            Pause(Math.Max(0, ArgInt(a, 0, 0)), ct);
            return DynValue.Nil;
        });
        DynValue Log(CallbackArguments a)
        {
            var parts = new List<string>();
            for (int i = 0; i < a.Count; i++) parts.Add(a[i].Type == DataType.String ? a[i].String : a[i].ToPrintString());
            Output?.Invoke(string.Join("\t", parts));
            return DynValue.Nil;
        }
        Fn("log", Log);
        Fn("print", Log);
        Fn("crc16", a => DynValue.NewNumber(Checksum.ModbusCrc16(ArgBytes(a, 0, "crc16"))));
        Fn("crc16_ccitt", a => DynValue.NewNumber(Checksum.Crc16Ccitt(ArgBytes(a, 0, "crc16_ccitt"), (ushort)ArgInt(a, 1, 0xFFFF))));
        Fn("sum8", a => DynValue.NewNumber(ArgBytes(a, 0, "sum8").Aggregate(0, (s, b) => (s + b) & 0xFF)));
        Fn("xor8", a => DynValue.NewNumber(ArgBytes(a, 0, "xor8").Aggregate(0, (s, b) => s ^ b)));
        Fn("checksum", a =>
        {
            if (!TryKind(a[0].CastToString() ?? "", out var k)) throw new ScriptRuntimeException("checksum：未知校验类型 " + a[0].CastToString());
            return DynValue.NewString(ToByteString(Checksum.Compute(k, ArgBytes(a, 1, "checksum"))));
        });
        Fn("append_checksum", a =>
        {
            if (!TryKind(a[0].CastToString() ?? "", out var k)) throw new ScriptRuntimeException("append_checksum：未知校验类型 " + a[0].CastToString());
            return DynValue.NewString(ToByteString(Checksum.Append(k, ArgBytes(a, 1, "append_checksum"))));
        });
        Fn("hex", a =>
        {
            var v = a[0];
            if (v.Type == DataType.Number)
            {
                int w = ArgInt(a, 1, 0);
                long n = (long)v.Number;
                return DynValue.NewString(w > 0 ? n.ToString("X" + w, CultureInfo.InvariantCulture) : n.ToString("X", CultureInfo.InvariantCulture));
            }
            return DynValue.NewString(Checksum.ToHex(ArgBytes(a, 0, "hex")));
        });
        Fn("unhex", a =>
        {
            if (!HexUtil.TryParse(a[0].CastToString() ?? "", out var d, out var err)) throw new ScriptRuntimeException("unhex：" + err);
            return DynValue.NewString(ToByteString(d));
        });
        Fn("text", a => DynValue.NewString(Encoding.GetString(ArgBytes(a, 0, "text"))));
        Fn("bytes", a =>
        {
            var t = new Table(script);
            foreach (var b in ArgBytes(a, 0, "bytes")) t.Append(DynValue.NewNumber(b));
            return DynValue.NewTable(t);
        });
        Fn("clear", a =>
        {
            _rx!.Clear();
            return DynValue.Nil;
        });
        Fn("now", a => DynValue.NewNumber(_clock.Elapsed.TotalMilliseconds));
        Fn("set_status", a =>
        {
            g["status"] = a[0];
            PollStatus();
            return DynValue.Nil;
        });
        g["status"] = DynValue.NewString("");

        var session = new Table(script);
        session["name"] = _channel.Name;
        session["isopen"] = DynValue.NewCallback((c, a) => DynValue.NewBoolean(_channel.IsOpen), "isopen");
        session["send"] = g.Get("send");
        session["sendhex"] = g.Get("sendhex");
        session["encoding"] = Encoding.WebName;
        session["pending"] = DynValue.NewCallback((c, a) => DynValue.NewNumber(_rx?.Count ?? 0), "pending");
        g["session"] = DynValue.NewTable(session);
    }

    private static int IndexOf(byte[] hay, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= hay.Length; i++)
        {
            int k = 0;
            while (k < needle.Length && hay[i + k] == needle[k]) k++;
            if (k == needle.Length) return i;
        }
        return -1;
    }
}

/// <summary>Lua 示例脚本。</summary>
public static class LuaSamples
{
    public const string AtTest = @"-- AT 指令测试：逐条发送并等待 OK
local cmds = { ""AT"", ""ATE0"", ""AT+GMR"", ""AT+CSQ"" }
local ok = 0
for i, cmd in ipairs(cmds) do
  clear()
  send(cmd .. ""\r\n"")
  local resp = waitfor(""OK"", 1000)
  if resp then
    ok = ok + 1
    log(""✓ "" .. cmd)
  else
    log(""✗ "" .. cmd .. ""：超时"")
  end
  status = string.format(""%d/%d 通过"", ok, i)
  sleep(100)
end
log(""完成："" .. status)
";

    public const string ModbusPoll = @"-- Modbus RTU 轮询：每秒读站号 1 的保持寄存器 0~1
local function read_regs(slave, addr, count)
  local req = string.char(slave, 0x03, math.floor(addr / 256), addr % 256, math.floor(count / 256), count % 256)
  local crc = crc16(req)
  clear()
  send(req .. string.char(crc % 256, math.floor(crc / 256)))
  local resp = read(500, 5 + count * 2)
  if not resp then return nil, ""超时"" end
  local body = resp:sub(1, #resp - 2)
  local rc = crc16(body)
  if resp:byte(#resp - 1) ~= rc % 256 or resp:byte(#resp) ~= math.floor(rc / 256) then return nil, ""CRC 错误"" end
  if resp:byte(2) >= 0x80 then return nil, ""异常码 "" .. resp:byte(3) end
  local vals = {}
  for i = 0, count - 1 do
    vals[#vals + 1] = resp:byte(4 + i * 2) * 256 + resp:byte(5 + i * 2)
  end
  return vals
end

for n = 1, 10 do
  local v, err = read_regs(1, 0, 2)
  if v then
    log(string.format(""第 %d 次：R0=%d R1=%d"", n, v[1], v[2]))
    status = ""R0="" .. v[1]
  else
    log(""第 "" .. n .. "" 次失败："" .. err)
  end
  sleep(1000)
end
";

    public const string Echo = @"-- 回环应答：收到什么就加前缀回发什么（停止前一直运行）
local count = 0
function on_receive(data)
  count = count + 1
  status = ""已收到 "" .. count .. "" 包""
  log(""收到："" .. hex(data))
  send(""ECHO:"" .. data)
end
log(""等待数据……会话："" .. session.name)
";

    public static readonly (string Name, string Code)[] All =
    {
        ("AT 指令测试", AtTest),
        ("Modbus 轮询", ModbusPoll),
        ("回环应答 on_receive", Echo),
    };
}
