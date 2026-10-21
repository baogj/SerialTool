using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace SerialTool.Features;

/// <summary>Modbus TCP（MBAP）与 RTU 帧之间的转换工具。</summary>
public static class ModbusGatewayCodec
{
    public const byte ExGatewayPathUnavailable = 0x0A;
    public const byte ExGatewayTargetNoResponse = 0x0B;

    /// <summary>缓冲区开头是否已有一个完整的 MBAP 帧。返回 1 = 完整（frameLength 有效），0 = 需要更多数据，-1 = 头部非法（应丢弃一个字节）。</summary>
    public static int TryParseMbap(IReadOnlyList<byte> buf, out int frameLength)
    {
        frameLength = 0;
        if (buf.Count < 7) return 0;
        int len = (buf[4] << 8) | buf[5];
        if (buf[2] != 0 || buf[3] != 0 || len < 2 || len > 254) return -1;
        if (buf.Count < 6 + len) return 0;
        frameLength = 6 + len;
        return 1;
    }

    public static ushort TransactionId(IReadOnlyList<byte> adu) => (ushort)((adu[0] << 8) | adu[1]);

    /// <summary>MBAP 帧 → RTU 帧（站号 + PDU + CRC）。unitOverride 非 0 时替换站号。</summary>
    public static byte[] MbapToRtu(byte[] adu, byte unitOverride = 0)
    {
        if (adu.Length < 8) throw new ArgumentException("MBAP 帧太短");
        int pduLen = adu.Length - 7;
        var rtu = new byte[1 + pduLen + 2];
        rtu[0] = unitOverride != 0 ? unitOverride : adu[6];
        Buffer.BlockCopy(adu, 7, rtu, 1, pduLen);
        ushort crc = Checksum.ModbusCrc16(rtu.AsSpan(0, 1 + pduLen));
        rtu[^2] = (byte)crc;
        rtu[^1] = (byte)(crc >> 8);
        return rtu;
    }

    /// <summary>组 MBAP 帧。</summary>
    public static byte[] BuildMbap(ushort tid, byte unit, ReadOnlySpan<byte> pdu)
    {
        var r = new byte[7 + pdu.Length];
        r[0] = (byte)(tid >> 8);
        r[1] = (byte)tid;
        int len = pdu.Length + 1;
        r[4] = (byte)(len >> 8);
        r[5] = (byte)len;
        r[6] = unit;
        pdu.CopyTo(r.AsSpan(7));
        return r;
    }

    /// <summary>组 RTU 帧（自动追加 CRC）。</summary>
    public static byte[] BuildRtu(byte unit, ReadOnlySpan<byte> pdu)
    {
        var r = new byte[pdu.Length + 3];
        r[0] = unit;
        pdu.CopyTo(r.AsSpan(1));
        ushort crc = Checksum.ModbusCrc16(r.AsSpan(0, pdu.Length + 1));
        r[^2] = (byte)crc;
        r[^1] = (byte)(crc >> 8);
        return r;
    }

    /// <summary>RTU 帧 → 站号 + PDU（校验 CRC）。</summary>
    public static bool TryRtuToPdu(byte[] rtu, out byte unit, out byte[] pdu)
    {
        unit = 0;
        pdu = Array.Empty<byte>();
        if (rtu.Length < 4 || !CrcOk(rtu, rtu.Length)) return false;
        unit = rtu[0];
        pdu = rtu.AsSpan(1, rtu.Length - 3).ToArray();
        return true;
    }

    public static byte[] ExceptionPdu(byte function, byte code) => new[] { (byte)(function | 0x80), code };

    public static bool CrcOk(IReadOnlyList<byte> b, int len)
    {
        if (len < 3 || b.Count < len) return false;
        ushort crc = 0xFFFF;
        for (int i = 0; i < len - 2; i++)
        {
            crc ^= b[i];
            for (int k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
        }
        return b[len - 2] == (byte)crc && b[len - 1] == (byte)(crc >> 8);
    }

    /// <summary>根据功能码推算 RTU 应答帧长度：&gt;0 = 长度，0 = 数据不足，-1 = 未知功能码。</summary>
    public static int RtuResponseLength(IReadOnlyList<byte> b)
    {
        if (b.Count < 2) return 0;
        byte fc = b[1];
        if ((fc & 0x80) != 0) return 5;
        switch (fc)
        {
            case 1: case 2: case 3: case 4: case 0x17:
                return b.Count < 3 ? 0 : 5 + b[2];
            case 5: case 6: case 0x0F: case 0x10:
                return 8;
            default:
                return -1;
        }
    }

    /// <summary>根据功能码推算 RTU 请求帧长度：&gt;0 = 长度，0 = 数据不足，-1 = 未知功能码。</summary>
    public static int RtuRequestLength(IReadOnlyList<byte> b)
    {
        if (b.Count < 2) return 0;
        switch (b[1])
        {
            case 1: case 2: case 3: case 4: case 5: case 6:
                return 8;
            case 0x0F: case 0x10:
                return b.Count < 7 ? 0 : 9 + b[6];
            case 0x17:
                return b.Count < 11 ? 0 : 13 + b[10];
            default:
                return -1;
        }
    }

    /// <summary>未知功能码时，查找使 CRC 正确的最短长度；找不到返回 -1。</summary>
    public static int FindCrcLength(IReadOnlyList<byte> b, int min = 4)
    {
        for (int n = min; n <= Math.Min(b.Count, 256); n++)
            if (CrcOk(b, n)) return n;
        return -1;
    }
}

public sealed class GatewayLogEntry
{
    public DateTime Time { get; init; }
    public string Direction { get; init; } = "";
    public ushort TransactionId { get; init; }
    public byte Unit { get; init; }
    public byte Function { get; init; }
    public string Request { get; init; } = "";
    public string Response { get; init; } = "";
    public double ElapsedMs { get; init; }
    public string Result { get; init; } = "";
    public bool IsError { get; init; }

    public override string ToString() =>
        $"{Time:HH:mm:ss.fff} [{Direction}] TID={TransactionId} 站{Unit} FC{Function:X2} {Result} {ElapsedMs:0} ms  请求 {Request}  应答 {Response}";
}

public sealed class GatewayStats
{
    private long _requests, _responses, _timeouts, _errors, _exceptions;
    public long Requests => Interlocked.Read(ref _requests);
    public long Responses => Interlocked.Read(ref _responses);
    public long Timeouts => Interlocked.Read(ref _timeouts);
    public long Errors => Interlocked.Read(ref _errors);
    public long Exceptions => Interlocked.Read(ref _exceptions);
    internal void Request() => Interlocked.Increment(ref _requests);
    internal void Response() => Interlocked.Increment(ref _responses);
    internal void Timeout() => Interlocked.Increment(ref _timeouts);
    internal void Error() => Interlocked.Increment(ref _errors);
    internal void Exception() => Interlocked.Increment(ref _exceptions);
    public void Reset()
    {
        Interlocked.Exchange(ref _requests, 0);
        Interlocked.Exchange(ref _responses, 0);
        Interlocked.Exchange(ref _timeouts, 0);
        Interlocked.Exchange(ref _errors, 0);
        Interlocked.Exchange(ref _exceptions, 0);
    }
    public override string ToString() => $"请求 {Requests}，应答 {Responses}，超时 {Timeouts}，异常应答 {Exceptions}，错误 {Errors}";
}

/// <summary>RTU 总线事务：发送一帧 RTU 请求并等待应答帧。</summary>
public interface IModbusRtuTransport
{
    /// <summary>返回应答帧；超时返回 null；广播（站号 0）返回空数组。</summary>
    Task<byte[]?> TransactAsync(byte[] rtuRequest, int timeoutMs, CancellationToken ct);
}

/// <summary>通过一个会话通道（串口 / TCP 等）访问 RTU 总线。</summary>
public sealed class ChannelRtuTransport : IModbusRtuTransport, IDisposable
{
    private readonly IByteChannel _channel;
    private readonly RxCollector _rx;

    public ChannelRtuTransport(IByteChannel channel)
    {
        _channel = channel;
        _rx = new RxCollector(channel);
    }

    public Task<byte[]?> TransactAsync(byte[] req, int timeoutMs, CancellationToken ct) => Task.Run(() => Transact(req, timeoutMs, ct), ct);

    private byte[]? Transact(byte[] req, int timeoutMs, CancellationToken ct)
    {
        _rx.Clear();
        if (!_channel.Send(req)) throw new IOException(_channel.IsOpen ? "发送失败" : "RTU 会话未连接");
        if (req[0] == 0) return Array.Empty<byte>();
        var sw = Stopwatch.StartNew();
        while (true)
        {
            long ver = _rx.Version;
            var buf = new List<byte>(_rx.Snapshot());
            // 丢弃开头不属于本次应答的字节
            int skipped = 0;
            while (buf.Count >= 2 && (buf[0] != req[0] || (buf[1] & 0x7F) != req[1]))
            {
                buf.RemoveAt(0);
                skipped++;
            }
            if (skipped > 0) _rx.Consume(skipped);
            int len = ModbusGatewayCodec.RtuResponseLength(buf);
            if (len < 0) len = ModbusGatewayCodec.FindCrcLength(buf);
            if (len > 0 && buf.Count >= len)
            {
                if (ModbusGatewayCodec.CrcOk(buf, len))
                {
                    _rx.Consume(len);
                    return buf.GetRange(0, len).ToArray();
                }
                _rx.Consume(1); // CRC 错：继续寻找
                continue;
            }
            int left = timeoutMs - (int)sw.ElapsedMilliseconds;
            if (left <= 0) return null;
            _rx.WaitForData(ver, left, ct);
        }
    }

    public void Dispose() => _rx.Dispose();
}

/// <summary>
/// Modbus TCP 服务器 → RTU 总线网关：接收 MBAP 请求，转成 RTU+CRC 依次发往串口，
/// 应答还原为 MBAP（保留原事务号）。请求串行执行；超时返回异常 0x0B。
/// </summary>
public sealed class ModbusTcpToRtuGateway : IAsyncDisposable
{
    private readonly IModbusRtuTransport _transport;
    private readonly SemaphoreSlim _busLock = new(1, 1);
    private readonly List<TcpClient> _clients = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;

    public ModbusTcpToRtuGateway(IModbusRtuTransport transport)
    {
        _transport = transport;
    }

    public int TimeoutMs { get; set; } = 1000;
    /// <summary>非 0 时转发到 RTU 时替换站号。</summary>
    public byte UnitOverride { get; set; }
    public int Port { get; private set; }
    public bool IsRunning => _listener != null;
    public GatewayStats Stats { get; } = new();
    public int ClientCount { get { lock (_clients) return _clients.Count; } }

    public event Action<GatewayLogEntry>? Log;
    public event Action<int>? ClientsChanged;

    public void Start(int port, IPAddress? bind = null)
    {
        if (_listener != null) throw new InvalidOperationException("网关已启动");
        var l = new TcpListener(bind ?? IPAddress.Any, port);
        l.Start();
        _listener = l;
        Port = ((IPEndPoint)l.LocalEndpoint).Port;
        _cts = new CancellationTokenSource();
        _acceptTask = AcceptLoop(l, _cts.Token);
    }

    private async Task AcceptLoop(TcpListener l, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await l.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch { break; }
            c.NoDelay = true;
            lock (_clients) _clients.Add(c);
            ClientsChanged?.Invoke(ClientCount);
            _ = ClientLoop(c, ct);
        }
    }

    private async Task ClientLoop(TcpClient c, CancellationToken ct)
    {
        var buf = new List<byte>();
        var tmp = new byte[1024];
        try
        {
            var ns = c.GetStream();
            while (!ct.IsCancellationRequested)
            {
                int n = await ns.ReadAsync(tmp.AsMemory(), ct).ConfigureAwait(false);
                if (n <= 0) break;
                buf.AddRange(tmp.AsSpan(0, n).ToArray());
                while (true)
                {
                    int st = ModbusGatewayCodec.TryParseMbap(buf, out int flen);
                    if (st == 0) break;
                    if (st < 0)
                    {
                        buf.RemoveAt(0);
                        continue;
                    }
                    var adu = buf.GetRange(0, flen).ToArray();
                    buf.RemoveRange(0, flen);
                    var resp = await ProcessAsync(adu, ct).ConfigureAwait(false);
                    if (resp != null) await ns.WriteAsync(resp.AsMemory(), ct).ConfigureAwait(false);
                }
                if (buf.Count > 4096) buf.Clear();
            }
        }
        catch { }
        finally
        {
            lock (_clients) _clients.Remove(c);
            try { c.Close(); } catch { }
            ClientsChanged?.Invoke(ClientCount);
        }
    }

    /// <summary>处理一个 MBAP 请求，返回 MBAP 应答（广播请求返回 null）。</summary>
    public async Task<byte[]?> ProcessAsync(byte[] adu, CancellationToken ct = default)
    {
        Stats.Request();
        ushort tid = ModbusGatewayCodec.TransactionId(adu);
        byte unit = adu[6];
        byte fc = adu.Length > 7 ? adu[7] : (byte)0;
        if (adu.Length < 8)
        {
            Stats.Error();
            return null;
        }
        var rtu = ModbusGatewayCodec.MbapToRtu(adu, UnitOverride);
        var sw = Stopwatch.StartNew();
        byte[]? rtuResp;
        string? err = null;
        await _busLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            rtuResp = await _transport.TransactAsync(rtu, TimeoutMs, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            rtuResp = null;
            err = ex.Message;
        }
        finally
        {
            _busLock.Release();
        }
        sw.Stop();

        byte[] mbapResp;
        string result;
        bool isErr = false;
        if (err != null)
        {
            Stats.Error();
            mbapResp = ModbusGatewayCodec.BuildMbap(tid, unit, ModbusGatewayCodec.ExceptionPdu(fc, ModbusGatewayCodec.ExGatewayPathUnavailable));
            result = "错误：" + err + " → 异常 0A";
            isErr = true;
        }
        else if (rtuResp == null)
        {
            Stats.Timeout();
            mbapResp = ModbusGatewayCodec.BuildMbap(tid, unit, ModbusGatewayCodec.ExceptionPdu(fc, ModbusGatewayCodec.ExGatewayTargetNoResponse));
            result = $"超时（{TimeoutMs} ms）→ 异常 0B";
            isErr = true;
        }
        else if (rtuResp.Length == 0)
        {
            Log?.Invoke(new GatewayLogEntry { Time = DateTime.Now, Direction = "TCP→RTU", TransactionId = tid, Unit = unit, Function = fc,
                Request = Checksum.ToHex(adu), Response = "", ElapsedMs = sw.Elapsed.TotalMilliseconds, Result = "广播，无应答" });
            return null;
        }
        else if (ModbusGatewayCodec.TryRtuToPdu(rtuResp, out _, out var pdu))
        {
            Stats.Response();
            if (pdu.Length > 0 && (pdu[0] & 0x80) != 0) Stats.Exception();
            mbapResp = ModbusGatewayCodec.BuildMbap(tid, unit, pdu);
            result = pdu.Length > 1 && (pdu[0] & 0x80) != 0 ? $"异常应答 {pdu[1]:X2}" : "成功";
        }
        else
        {
            Stats.Error();
            mbapResp = ModbusGatewayCodec.BuildMbap(tid, unit, ModbusGatewayCodec.ExceptionPdu(fc, ModbusGatewayCodec.ExGatewayTargetNoResponse));
            result = "应答 CRC 错误 → 异常 0B";
            isErr = true;
        }
        Log?.Invoke(new GatewayLogEntry
        {
            Time = DateTime.Now, Direction = "TCP→RTU", TransactionId = tid, Unit = unit, Function = fc,
            Request = Checksum.ToHex(adu), Response = Checksum.ToHex(mbapResp), ElapsedMs = sw.Elapsed.TotalMilliseconds, Result = result, IsError = isErr,
        });
        return mbapResp;
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        _listener = null;
        TcpClient[] cs;
        lock (_clients) cs = _clients.ToArray();
        foreach (var c in cs) try { c.Close(); } catch { }
        if (_acceptTask != null) try { await _acceptTask.ConfigureAwait(false); } catch { }
        _acceptTask = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}

/// <summary>
/// RTU 主站（串口）→ 远端 Modbus TCP 从站网关：从串口切出 RTU 请求，转为 MBAP（递增事务号）发往远端，
/// 按事务号匹配应答后加 CRC 写回串口。超时返回 RTU 异常 0x0B，连不上远端返回 0x0A。
/// </summary>
public sealed class ModbusRtuToTcpGateway : IAsyncDisposable
{
    private readonly IByteChannel _serial;
    private readonly object _bufLock = new();
    private readonly List<byte> _buf = new();
    private readonly SemaphoreSlim _signal = new(0);
    private DateTime _lastRx;
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private TcpClient? _tcp;
    private ushort _tid;

    public ModbusRtuToTcpGateway(IByteChannel serial, string remoteHost, int remotePort)
    {
        _serial = serial;
        RemoteHost = remoteHost;
        RemotePort = remotePort;
    }

    public string RemoteHost { get; }
    public int RemotePort { get; }
    public int TimeoutMs { get; set; } = 1000;
    public int ConnectTimeoutMs { get; set; } = 3000;
    public byte UnitOverride { get; set; }
    /// <summary>超过该静默间隔丢弃半帧。</summary>
    public int GapResetMs { get; set; } = 200;
    public bool IsRunning => _worker != null;
    public bool RemoteConnected => _tcp?.Connected == true;
    public GatewayStats Stats { get; } = new();
    public ushort LastTransactionId => _tid;

    public event Action<GatewayLogEntry>? Log;

    public void Start()
    {
        if (_worker != null) throw new InvalidOperationException("网关已启动");
        _cts = new CancellationTokenSource();
        _serial.Received += OnSerial;
        _worker = Task.Run(() => WorkerLoop(_cts.Token));
    }

    private void OnSerial(byte[] data)
    {
        lock (_bufLock)
        {
            var now = DateTime.UtcNow;
            if (_buf.Count > 0 && GapResetMs > 0 && (now - _lastRx).TotalMilliseconds > GapResetMs) _buf.Clear();
            _lastRx = now;
            _buf.AddRange(data);
            if (_buf.Count > 4096) _buf.RemoveRange(0, _buf.Count - 4096);
        }
        _signal.Release();
    }

    private byte[]? NextRequest()
    {
        lock (_bufLock)
        {
            while (_buf.Count >= 2)
            {
                int len = ModbusGatewayCodec.RtuRequestLength(_buf);
                if (len == 0) return null;
                if (len < 0)
                {
                    len = ModbusGatewayCodec.FindCrcLength(_buf);
                    if (len < 0)
                    {
                        if (_buf.Count >= 256) _buf.RemoveAt(0);
                        return null;
                    }
                }
                if (_buf.Count < len) return null;
                if (!ModbusGatewayCodec.CrcOk(_buf, len))
                {
                    _buf.RemoveAt(0);
                    Stats.Error();
                    continue;
                }
                var f = _buf.GetRange(0, len).ToArray();
                _buf.RemoveRange(0, len);
                return f;
            }
            return null;
        }
    }

    private async Task WorkerLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _signal.WaitAsync(ct).ConfigureAwait(false);
                byte[]? req;
                while ((req = NextRequest()) != null)
                {
                    var resp = await ProcessAsync(req, ct).ConfigureAwait(false);
                    if (resp != null) _serial.Send(resp);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task<NetworkStream> EnsureConnectedAsync(CancellationToken ct)
    {
        if (_tcp is { Connected: true }) return _tcp.GetStream();
        try { _tcp?.Close(); } catch { }
        _tcp = null;
        var c = new TcpClient { NoDelay = true };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ConnectTimeoutMs);
        try
        {
            await c.ConnectAsync(RemoteHost, RemotePort, cts.Token).ConfigureAwait(false);
        }
        catch
        {
            c.Dispose();
            throw;
        }
        _tcp = c;
        return c.GetStream();
    }

    /// <summary>处理一个 RTU 请求帧，返回写回串口的 RTU 应答（广播返回 null）。</summary>
    public async Task<byte[]?> ProcessAsync(byte[] rtuReq, CancellationToken ct = default)
    {
        Stats.Request();
        byte unit = rtuReq[0], fc = rtuReq[1];
        var pdu = rtuReq.AsSpan(1, rtuReq.Length - 3).ToArray();
        ushort tid = unchecked(++_tid);
        var adu = ModbusGatewayCodec.BuildMbap(tid, UnitOverride != 0 ? UnitOverride : unit, pdu);
        var sw = Stopwatch.StartNew();
        byte[]? result = null;
        string text;
        bool isErr = false;
        NetworkStream ns;
        try
        {
            ns = await EnsureConnectedAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Stats.Error();
            result = unit == 0 ? null : ModbusGatewayCodec.BuildRtu(unit, ModbusGatewayCodec.ExceptionPdu(fc, ModbusGatewayCodec.ExGatewayPathUnavailable));
            Emit(tid, unit, fc, rtuReq, result, sw, "连接远端失败：" + ex.Message + " → 异常 0A", true);
            return result;
        }

        try
        {
            await ns.WriteAsync(adu.AsMemory(), ct).ConfigureAwait(false);
            if (unit == 0)
            {
                Emit(tid, unit, fc, rtuReq, null, sw, "广播，无应答", false);
                return null;
            }
            var resp = await ReadMatchingAsync(ns, tid, TimeoutMs, ct).ConfigureAwait(false);
            if (resp == null)
            {
                Stats.Timeout();
                result = ModbusGatewayCodec.BuildRtu(unit, ModbusGatewayCodec.ExceptionPdu(fc, ModbusGatewayCodec.ExGatewayTargetNoResponse));
                text = $"超时（{TimeoutMs} ms）→ 异常 0B";
                isErr = true;
            }
            else
            {
                Stats.Response();
                var rpdu = resp.AsSpan(7).ToArray();
                if (rpdu.Length > 0 && (rpdu[0] & 0x80) != 0) Stats.Exception();
                result = ModbusGatewayCodec.BuildRtu(unit, rpdu);
                text = rpdu.Length > 1 && (rpdu[0] & 0x80) != 0 ? $"异常应答 {rpdu[1]:X2}" : "成功";
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Stats.Error();
            try { _tcp?.Close(); } catch { }
            _tcp = null;
            result = ModbusGatewayCodec.BuildRtu(unit, ModbusGatewayCodec.ExceptionPdu(fc, ModbusGatewayCodec.ExGatewayTargetNoResponse));
            text = "远端连接断开：" + ex.Message + " → 异常 0B";
            isErr = true;
        }
        Emit(tid, unit, fc, rtuReq, result, sw, text, isErr);
        return result;
    }

    private readonly List<byte> _tcpBuf = new();

    private async Task<byte[]?> ReadMatchingAsync(NetworkStream ns, ushort tid, int timeoutMs, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        var tmp = new byte[1024];
        while (true)
        {
            while (true)
            {
                int st = ModbusGatewayCodec.TryParseMbap(_tcpBuf, out int flen);
                if (st == 0) break;
                if (st < 0) { _tcpBuf.RemoveAt(0); continue; }
                var f = _tcpBuf.GetRange(0, flen).ToArray();
                _tcpBuf.RemoveRange(0, flen);
                if (ModbusGatewayCodec.TransactionId(f) == tid) return f;
                // 事务号不匹配（迟到的旧应答）：丢弃
            }
            int n;
            try { n = await ns.ReadAsync(tmp.AsMemory(), cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
            if (n <= 0) throw new IOException("远端关闭了连接");
            _tcpBuf.AddRange(tmp.AsSpan(0, n).ToArray());
        }
    }

    private void Emit(ushort tid, byte unit, byte fc, byte[] req, byte[]? resp, Stopwatch sw, string text, bool err) =>
        Log?.Invoke(new GatewayLogEntry
        {
            Time = DateTime.Now, Direction = "RTU→TCP", TransactionId = tid, Unit = unit, Function = fc,
            Request = Checksum.ToHex(req), Response = resp == null ? "" : Checksum.ToHex(resp),
            ElapsedMs = sw.Elapsed.TotalMilliseconds, Result = text, IsError = err,
        });

    public async Task StopAsync()
    {
        _serial.Received -= OnSerial;
        _cts?.Cancel();
        if (_worker != null) try { await _worker.ConfigureAwait(false); } catch { }
        _worker = null;
        try { _tcp?.Close(); } catch { }
        _tcp = null;
        _tcpBuf.Clear();
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
