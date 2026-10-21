using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SerialTool.Features;

/// <summary>文件传输使用的字节通道（串口 / 网络 / 内存管道）。</summary>
public interface IByteLink
{
    /// <summary>读一个字节；超时返回 -1。</summary>
    int ReadByte(int timeoutMs, CancellationToken ct);
    void Write(byte[] data);
    /// <summary>丢弃已收到但未读的数据。</summary>
    void Purge();
}

/// <summary>由外部喂数据的通道：主连接 Received 事件 → Feed；写出通过委托。</summary>
public sealed class FeedLink : IByteLink
{
    private readonly BlockingCollection<byte> _rx = new(new ConcurrentQueue<byte>());
    private readonly Action<byte[]> _write;

    public FeedLink(Action<byte[]> write)
    {
        _write = write;
    }

    public void Feed(byte[] data)
    {
        foreach (byte b in data) _rx.Add(b);
    }

    public int ReadByte(int timeoutMs, CancellationToken ct)
    {
        try
        {
            return _rx.TryTake(out byte b, timeoutMs, ct) ? b : -1;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    public void Write(byte[] data) => _write(data);

    public void Purge()
    {
        while (_rx.TryTake(out _)) { }
    }
}

/// <summary>内存中的一对互连通道（单元测试用）。</summary>
public static class MemoryLink
{
    public static (FeedLink A, FeedLink B) CreatePair(Func<byte[], byte[]>? aToB = null, Func<byte[], byte[]>? bToA = null)
    {
        FeedLink? a = null, b = null;
        a = new FeedLink(d => b!.Feed(aToB != null ? aToB(d) : d));
        b = new FeedLink(d => a!.Feed(bToA != null ? bToA(d) : d));
        return (a, b);
    }
}

public enum XModemVariant
{
    /// <summary>XModem 128 字节 + 8 位校验和</summary>
    XModemChecksum,
    /// <summary>XModem 128 字节 + CRC16</summary>
    XModemCrc,
    /// <summary>XModem-1K（1024 字节块 + CRC16）</summary>
    XModem1K,
    /// <summary>YModem 批量（块 0 携带文件名与大小，1K 块 + CRC16）</summary>
    YModem,
}

public sealed class TransferFile
{
    public string Name { get; init; } = "";
    public byte[] Data { get; init; } = Array.Empty<byte>();
}

public sealed class TransferProgress
{
    public string FileName { get; init; } = "";
    public long Done { get; init; }
    public long Total { get; init; }   // -1 = 未知
    public int Retries { get; init; }
    public string? Message { get; init; }
}

public sealed class TransferException : Exception
{
    public TransferException(string message) : base(message) { }
}

/// <summary>XModem / XModem-1K / YModem 发送与接收。</summary>
public sealed class XYModem
{
    public const byte SOH = 0x01, STX = 0x02, EOT = 0x04, ACK = 0x06, NAK = 0x15, CAN = 0x18, CRCC = 0x43, SUB = 0x1A;

    private readonly IByteLink _link;

    public XYModem(IByteLink link)
    {
        _link = link;
    }

    public int MaxRetries { get; set; } = 10;
    /// <summary>发送方等待接收方启动（C / NAK）的总时间。</summary>
    public int StartTimeoutMs { get; set; } = 60_000;
    /// <summary>每块等待 ACK 的超时。</summary>
    public int AckTimeoutMs { get; set; } = 10_000;
    /// <summary>接收方：块内字节间超时。</summary>
    public int ByteTimeoutMs { get; set; } = 1000;
    /// <summary>接收方：等待下一块的超时（超时则 NAK 重试）。</summary>
    public int BlockTimeoutMs { get; set; } = 10_000;
    /// <summary>接收方：发送 'C' / NAK 启动请求的间隔。</summary>
    public int StartIntervalMs { get; set; } = 3000;

    public event Action<TransferProgress>? Progress;
    public int TotalRetries { get; private set; }

    private void Report(string name, long done, long total, string? msg = null) =>
        Progress?.Invoke(new TransferProgress { FileName = name, Done = done, Total = total, Retries = TotalRetries, Message = msg });

    private void Cancel()
    {
        try { _link.Write(new byte[] { CAN, CAN, CAN, CAN, CAN, 0x08, 0x08, 0x08, 0x08, 0x08 }); } catch { /* ignore */ }
    }

    // ==================================================================
    // 发送
    // ==================================================================

    public Task SendAsync(IReadOnlyList<TransferFile> files, XModemVariant variant, CancellationToken ct) =>
        Task.Run(() => Send(files, variant, ct), CancellationToken.None);

    public void Send(IReadOnlyList<TransferFile> files, XModemVariant variant, CancellationToken ct)
    {
        try
        {
            if (variant != XModemVariant.YModem)
            {
                if (files.Count != 1) throw new TransferException("XModem 一次只能发送一个文件");
                bool crc = WaitStart(variant == XModemVariant.XModemChecksum ? null : true, ct);
                SendData(files[0], crc, variant == XModemVariant.XModem1K, 1, ct);
                SendEot(ct);
                Report(files[0].Name, files[0].Data.Length, files[0].Data.Length, "完成");
                return;
            }

            foreach (var f in files)
            {
                WaitStart(true, ct);
                SendBlock0(f, ct);
                WaitStart(true, ct);
                SendData(f, true, true, 1, ct);
                SendEot(ct);
                Report(f.Name, f.Data.Length, f.Data.Length, "完成");
            }
            // 结束批量：空块 0
            WaitStart(true, ct);
            SendBlockWithRetry(0, new byte[128], true, ct, "结束");
        }
        catch (OperationCanceledException)
        {
            Cancel();
            throw;
        }
        catch (TransferException)
        {
            Cancel();
            throw;
        }
    }

    /// <summary>等待接收方的启动字符。wantCrc：true=要求 'C'，null=接受 NAK（校验和）或 'C'。返回是否 CRC 模式。</summary>
    private bool WaitStart(bool? wantCrc, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(StartTimeoutMs);
        int cans = 0;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            int b = _link.ReadByte(500, ct);
            if (b == CRCC) return true;
            if (b == NAK && wantCrc != true) return false;
            if (b == CAN)
            {
                if (++cans >= 2) throw new TransferException("接收方取消了传输");
            }
            else if (b >= 0) cans = 0;
        }
        throw new TransferException("等待接收方启动超时（未收到 'C' 或 NAK）");
    }

    private void SendBlock0(TransferFile f, CancellationToken ct)
    {
        var block = new byte[128];
        string name = Path.GetFileName(f.Name);
        byte[] meta = Encoding.UTF8.GetBytes(name + "\0" + f.Data.Length.ToString(CultureInfo.InvariantCulture) + " ");
        if (meta.Length > 1024) throw new TransferException("文件名过长");
        if (meta.Length > 128) block = new byte[1024];
        Buffer.BlockCopy(meta, 0, block, 0, meta.Length);
        SendBlockWithRetry(0, block, true, ct, name);
    }

    private void SendData(TransferFile f, bool crc, bool k1, int firstBlock, CancellationToken ct)
    {
        int blk = firstBlock;
        int off = 0;
        var data = f.Data;
        Report(f.Name, 0, data.Length);
        while (off < data.Length)
        {
            int remaining = data.Length - off;
            int size = k1 && remaining > 128 ? 1024 : 128;
            var block = new byte[size];
            int n = Math.Min(size, remaining);
            Buffer.BlockCopy(data, off, block, 0, n);
            for (int i = n; i < size; i++) block[i] = SUB;
            SendBlockWithRetry((byte)blk, block, crc, ct, f.Name);
            off += n;
            blk = (blk + 1) & 0xFF;
            Report(f.Name, off, data.Length);
        }
    }

    public static byte[] BuildBlock(byte blk, byte[] data, bool crc)
    {
        var pkt = new byte[3 + data.Length + (crc ? 2 : 1)];
        pkt[0] = data.Length == 1024 ? STX : SOH;
        pkt[1] = blk;
        pkt[2] = (byte)(255 - blk);
        Buffer.BlockCopy(data, 0, pkt, 3, data.Length);
        if (crc)
        {
            ushort c = Checksum.Crc16Ccitt(data, 0);
            pkt[^2] = (byte)(c >> 8);
            pkt[^1] = (byte)c;
        }
        else
        {
            byte s = 0;
            foreach (byte b in data) s += b;
            pkt[^1] = s;
        }
        return pkt;
    }

    private void SendBlockWithRetry(byte blk, byte[] data, bool crc, CancellationToken ct, string name)
    {
        var pkt = BuildBlock(blk, data, crc);
        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (attempt > 0)
            {
                TotalRetries++;
                Report(name, -1, -1, $"块 {blk} 重发（第 {attempt} 次）");
            }
            _link.Write(pkt);
            int cans = 0;
            var deadline = DateTime.UtcNow.AddMilliseconds(AckTimeoutMs);
            while (true)
            {
                int left = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (left <= 0) break; // 超时 → 重发
                int b = _link.ReadByte(Math.Min(left, 500), ct);
                if (b == ACK) return;
                if (b == NAK) break;
                if (b == CAN)
                {
                    if (++cans >= 2) throw new TransferException("接收方取消了传输");
                    continue;
                }
                // 'C' 或噪声：忽略（YModem 块 0 后接收方会再发 'C'）
            }
        }
        throw new TransferException($"块 {blk} 重试 {MaxRetries} 次仍失败");
    }

    private void SendEot(CancellationToken ct)
    {
        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            _link.Write(new[] { EOT });
            int b = _link.ReadByte(AckTimeoutMs, ct);
            if (b == ACK) return;
            // NAK（YModem 标准流程）或超时：再发 EOT
        }
        throw new TransferException("EOT 未得到确认");
    }

    // ==================================================================
    // 接收
    // ==================================================================

    public Task<List<TransferFile>> ReceiveAsync(XModemVariant variant, CancellationToken ct) =>
        Task.Run(() => Receive(variant, ct), CancellationToken.None);

    /// <summary>接收文件。XModem 返回一个文件（名称为空，末尾的 0x1A 填充已去掉）；YModem 返回批量中的所有文件。</summary>
    public List<TransferFile> Receive(XModemVariant variant, CancellationToken ct)
    {
        try
        {
            var files = new List<TransferFile>();
            if (variant != XModemVariant.YModem)
            {
                bool crc = variant != XModemVariant.XModemChecksum;
                var data = ReceiveData(crc, 1, -1, "XModem", false, ct, out _);
                int len = data.Count;
                while (len > 0 && data[len - 1] == SUB) len--;
                files.Add(new TransferFile { Name = "", Data = data.Take(len).ToArray() });
                return files;
            }

            while (true)
            {
                var b0 = ReceiveBlock0(ct);
                if (b0 == null) break; // 空块 0：批量结束
                var (name, size) = b0.Value;
                _link.Write(new[] { ACK });
                var data = ReceiveData(true, 1, size, name, true, ct, out _);
                byte[] content = size >= 0 ? data.Take((int)Math.Min(size, data.Count)).ToArray() : data.ToArray();
                files.Add(new TransferFile { Name = name, Data = content });
                Report(name, content.Length, size, "完成");
            }
            return files;
        }
        catch (OperationCanceledException)
        {
            Cancel();
            throw;
        }
        catch (TransferException)
        {
            Cancel();
            throw;
        }
    }

    /// <summary>读一个块：返回 (类型, 块号, 数据)；类型为 EOT / CAN 时数据为空；超时或校验错时返回 null。</summary>
    private (byte Kind, byte Blk, byte[] Data)? ReadBlock(bool crc, int firstByteTimeout, CancellationToken ct, out string? error)
    {
        error = null;
        int h = _link.ReadByte(firstByteTimeout, ct);
        if (h < 0) { error = "超时"; return null; }
        if (h == EOT) return (EOT, 0, Array.Empty<byte>());
        if (h == CAN)
        {
            int h2 = _link.ReadByte(ByteTimeoutMs, ct);
            if (h2 == CAN) return (CAN, 0, Array.Empty<byte>());
            error = "噪声";
            return null;
        }
        if (h != SOH && h != STX) { error = $"无效的块头 0x{h:X2}"; return null; }
        int size = h == STX ? 1024 : 128;
        int total = 2 + size + (crc ? 2 : 1);
        var buf = new byte[total];
        for (int i = 0; i < total; i++)
        {
            int b = _link.ReadByte(ByteTimeoutMs, ct);
            if (b < 0) { error = "块内超时"; return null; }
            buf[i] = (byte)b;
        }
        byte blk = buf[0];
        if ((byte)(255 - blk) != buf[1]) { error = "块号校验错误"; return null; }
        var data = buf.AsSpan(2, size).ToArray();
        if (crc)
        {
            ushort c = Checksum.Crc16Ccitt(data, 0);
            if (buf[2 + size] != (byte)(c >> 8) || buf[3 + size] != (byte)c) { error = "CRC 错误"; return null; }
        }
        else
        {
            byte s = 0;
            foreach (byte b in data) s += b;
            if (buf[2 + size] != s) { error = "校验和错误"; return null; }
        }
        return (h == STX ? STX : SOH, blk, data);
    }

    /// <summary>发送启动字符并等待第一块。</summary>
    private (byte Kind, byte Blk, byte[] Data) StartAndRead(bool crc, CancellationToken ct, string name)
    {
        int attempts = Math.Max(1, StartTimeoutMs / Math.Max(1, StartIntervalMs));
        for (int i = 0; i < attempts; i++)
        {
            ct.ThrowIfCancellationRequested();
            _link.Write(new[] { crc ? CRCC : NAK });
            var r = ReadBlock(crc, StartIntervalMs, ct, out string? err);
            if (r != null)
            {
                if (r.Value.Kind == CAN) throw new TransferException("发送方取消了传输");
                return r.Value;
            }
            if (err != "超时")
            {
                TotalRetries++;
                Report(name, -1, -1, "首块错误：" + err);
                DrainLine(ct);
            }
        }
        throw new TransferException("等待发送方超时");
    }

    private (string Name, long Size)? ReceiveBlock0(CancellationToken ct)
    {
        var first = StartAndRead(true, ct, "YModem");
        for (int retry = 0; ; retry++)
        {
            if (first.Kind is SOH or STX && first.Blk == 0)
            {
                var d = first.Data;
                int z = Array.IndexOf(d, (byte)0);
                if (z <= 0)
                {
                    _link.Write(new[] { ACK });
                    return null; // 空文件名：结束
                }
                string name = Encoding.UTF8.GetString(d, 0, z);
                long size = -1;
                int end = z + 1;
                while (end < d.Length && d[end] != 0 && d[end] != (byte)' ') end++;
                if (end > z + 1 && long.TryParse(Encoding.ASCII.GetString(d, z + 1, end - z - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out long s))
                    size = s;
                Report(name, 0, size, "开始接收");
                return (name, size);
            }
            if (retry >= MaxRetries) throw new TransferException("未收到 YModem 块 0");
            TotalRetries++;
            DrainLine(ct);
            _link.Write(new[] { NAK });
            var r = ReadBlock(true, BlockTimeoutMs, ct, out _);
            if (r != null) first = r.Value;
        }
    }

    /// <summary>出错后丢弃线路上的残余数据，直到静默 150 ms（最多 3 秒）。</summary>
    private void DrainLine(CancellationToken ct)
    {
        var until = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < until && _link.ReadByte(150, ct) >= 0) { }
        _link.Purge();
    }

    private List<byte> ReceiveData(bool crc, byte firstBlk, long expected, string name, bool ymodem, CancellationToken ct, out int blocks)
    {
        var data = new List<byte>();
        byte expect = firstBlk;
        blocks = 0;
        int errors = 0;
        bool started = false;
        int eotCount = 0;
        (byte Kind, byte Blk, byte[] Data)? pending = null;

        if (!ymodem)
        {
            pending = StartAndRead(crc, ct, name);
        }
        else
        {
            _link.Write(new[] { CRCC }); // YModem：块 0 ACK 后再发 'C' 开始数据
        }

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            string? err = null;
            var r = pending ?? ReadBlock(crc, BlockTimeoutMs, ct, out err);
            pending = null;
            if (r == null)
            {
                if (++errors > MaxRetries) throw new TransferException($"连续错误过多（{err}）");
                TotalRetries++;
                Report(name, data.Count, expected, $"块 {expect} {err}，请求重发");
                DrainLine(ct);
                _link.Write(new[] { NAK });
                continue;
            }
            var (kind, blk, payload) = r.Value;
            if (kind == CAN) throw new TransferException("发送方取消了传输");
            if (kind == EOT)
            {
                // YModem：第一次 EOT 回 NAK，第二次回 ACK；XModem 直接 ACK
                if (ymodem && eotCount == 0 && started)
                {
                    eotCount++;
                    _link.Write(new[] { NAK });
                    continue;
                }
                _link.Write(new[] { ACK });
                return data;
            }
            errors = 0;
            if (blk == expect)
            {
                data.AddRange(payload);
                blocks++;
                started = true;
                expect = (byte)(expect + 1);
                _link.Write(new[] { ACK });
                Report(name, Math.Min(data.Count, expected >= 0 ? expected : data.Count), expected);
            }
            else if (blk == (byte)(expect - 1))
            {
                _link.Write(new[] { ACK }); // 重复块（之前的 ACK 丢失）
            }
            else
            {
                throw new TransferException($"块号错乱：期望 {expect}，收到 {blk}");
            }
        }
    }
}
