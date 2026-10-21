using System;
using System.Collections.Generic;
using System.Linq;

namespace SerialTool.Features;

/// <summary>从站数据区：线圈 / 离散输入 / 保持寄存器 / 输入寄存器（地址 0 ~ Size-1）。线程安全由调用方加锁 SyncRoot。</summary>
public sealed class ModbusDataModel
{
    public ModbusDataModel(int size = 100)
    {
        Resize(size);
    }

    public object SyncRoot { get; } = new();
    public int Size { get; private set; }
    public bool[] Coils { get; private set; } = Array.Empty<bool>();
    public bool[] DiscreteInputs { get; private set; } = Array.Empty<bool>();
    public ushort[] Holding { get; private set; } = Array.Empty<ushort>();
    public ushort[] Input { get; private set; } = Array.Empty<ushort>();

    public void Resize(int size)
    {
        size = Math.Clamp(size, 1, 65536);
        lock (SyncRoot)
        {
            Coils = Grow(Coils, size);
            DiscreteInputs = Grow(DiscreteInputs, size);
            Holding = Grow(Holding, size);
            Input = Grow(Input, size);
            Size = size;
        }
    }

    private static T[] Grow<T>(T[] a, int n)
    {
        var r = new T[n];
        Array.Copy(a, r, Math.Min(a.Length, n));
        return r;
    }
}

public sealed class ModbusSlaveLogEntry
{
    public DateTime Time { get; init; }
    public byte Slave { get; init; }
    public byte Function { get; init; }
    public string Request { get; init; } = "";
    public string Response { get; init; } = "";
    public string Summary { get; init; } = "";
    public bool IsError { get; init; }
    /// <summary>被写入的区域与地址范围（用于刷新表格）：'C'=线圈，'H'=保持寄存器。</summary>
    public char WrittenArea { get; init; }
    public int WrittenStart { get; init; }
    public int WrittenCount { get; init; }
}

/// <summary>
/// Modbus 从站模拟：从字节流中切出请求帧（RTU 按功能码推算长度 + CRC 校验；TCP 按 MBAP 长度），
/// 对指定从站地址作出应答。支持 01/02/03/04/05/06/0F/10，非法地址 / 数值 / 功能码返回异常应答。
/// </summary>
public sealed class ModbusSlave
{
    private readonly List<byte> _buf = new();
    private DateTime _last;

    public ModbusSlave(ModbusDataModel model)
    {
        Model = model;
    }

    public ModbusDataModel Model { get; }
    public byte SlaveId { get; set; } = 1;
    public ModbusFraming Framing { get; set; } = ModbusFraming.Rtu;
    /// <summary>超过该静默间隔时丢弃半帧（RTU 帧间隔）。</summary>
    public int GapResetMs { get; set; } = 100;
    /// <summary>为 true 时应答任意从站地址。</summary>
    public bool AnySlave { get; set; }

    public long Requests { get; private set; }
    public long Exceptions { get; private set; }
    public long CrcErrors { get; private set; }

    public void Reset() => _buf.Clear();

    /// <summary>喂入收到的字节，返回需要发送的应答帧和日志。</summary>
    public List<(byte[]? Response, ModbusSlaveLogEntry Log)> Push(byte[] data, DateTime now)
    {
        var result = new List<(byte[]?, ModbusSlaveLogEntry)>();
        if (_buf.Count > 0 && GapResetMs > 0 && (now - _last).TotalMilliseconds > GapResetMs) _buf.Clear();
        _last = now;
        _buf.AddRange(data);
        if (_buf.Count > 4096) _buf.RemoveRange(0, _buf.Count - 4096);

        while (_buf.Count > 0)
        {
            if (Framing == ModbusFraming.Tcp)
            {
                if (_buf.Count < 7) break;
                int len = (_buf[4] << 8) | _buf[5];
                if (_buf[2] != 0 || _buf[3] != 0 || len < 2 || len > 254)
                {
                    _buf.RemoveAt(0);
                    continue;
                }
                if (_buf.Count < 6 + len) break;
                byte[] frame = _buf.GetRange(0, 6 + len).ToArray();
                _buf.RemoveRange(0, 6 + len);
                var r = HandlePdu(frame[6], frame.AsSpan(7).ToArray(), frame, now);
                if (r.Pdu != null)
                {
                    var resp = new byte[7 + r.Pdu.Length];
                    resp[0] = frame[0];
                    resp[1] = frame[1];
                    int rl = r.Pdu.Length + 1;
                    resp[4] = (byte)(rl >> 8);
                    resp[5] = (byte)rl;
                    resp[6] = frame[6];
                    Buffer.BlockCopy(r.Pdu, 0, resp, 7, r.Pdu.Length);
                    result.Add((resp, WithResponse(r.Log, resp)));
                }
                else if (r.Log != null) result.Add((null, r.Log));
                continue;
            }

            // RTU
            if (_buf.Count < 2) break;
            byte fc = _buf[1];
            int need = RtuRequestLength(fc, out bool more);
            if (more) break;
            if (need < 0)
            {
                // 未知功能码：找一个 CRC 正确的长度
                need = FindCrcLength();
                if (need < 0)
                {
                    if (_buf.Count >= 256) _buf.RemoveAt(0);
                    break;
                }
            }
            if (_buf.Count < need) break;
            byte[] f = _buf.GetRange(0, need).ToArray();
            ushort crc = Checksum.ModbusCrc16(f.AsSpan(0, need - 2));
            if ((f[need - 2] | (f[need - 1] << 8)) != crc)
            {
                CrcErrors++;
                _buf.RemoveAt(0); // 重新同步
                continue;
            }
            _buf.RemoveRange(0, need);
            var rr = HandlePdu(f[0], f.AsSpan(1, need - 3).ToArray(), f, now);
            if (rr.Pdu != null)
            {
                var body = new byte[1 + rr.Pdu.Length];
                body[0] = f[0];
                Buffer.BlockCopy(rr.Pdu, 0, body, 1, rr.Pdu.Length);
                var resp = Checksum.Append(ChecksumKind.ModbusCrc16, body);
                result.Add((resp, WithResponse(rr.Log, resp)));
            }
            else if (rr.Log != null) result.Add((null, rr.Log));
        }
        return result;
    }

    private static ModbusSlaveLogEntry WithResponse(ModbusSlaveLogEntry? log, byte[] resp) => new()
    {
        Time = log!.Time, Slave = log.Slave, Function = log.Function, Request = log.Request, Summary = log.Summary,
        IsError = log.IsError, Response = Checksum.ToHex(resp),
        WrittenArea = log.WrittenArea, WrittenStart = log.WrittenStart, WrittenCount = log.WrittenCount,
    };

    private int RtuRequestLength(byte fc, out bool needMore)
    {
        needMore = false;
        switch (fc)
        {
            case 1: case 2: case 3: case 4: case 5: case 6:
                return 8;
            case 15: case 16:
                if (_buf.Count < 7) { needMore = true; return 0; }
                return 9 + _buf[6];
            default:
                return -1;
        }
    }

    private int FindCrcLength()
    {
        for (int n = 4; n <= Math.Min(_buf.Count, 256); n++)
        {
            var span = _buf.GetRange(0, n).ToArray();
            ushort crc = Checksum.ModbusCrc16(span.AsSpan(0, n - 2));
            if ((span[n - 2] | (span[n - 1] << 8)) == crc) return n;
        }
        return -1;
    }

    /// <summary>处理一个 PDU。返回应答 PDU（null = 不应答）与日志（null = 非本站请求，忽略）。</summary>
    public (byte[]? Pdu, ModbusSlaveLogEntry? Log) HandlePdu(byte slave, byte[] pdu, byte[] rawRequest, DateTime now)
    {
        bool broadcast = slave == 0;
        if (!broadcast && !AnySlave && slave != SlaveId) return (null, null);
        Requests++;
        byte fc = pdu.Length > 0 ? pdu[0] : (byte)0;
        string req = Checksum.ToHex(rawRequest);
        var (resp, summary, area, start, count) = Execute(pdu);
        bool isExc = resp.Length == 2 && (resp[0] & 0x80) != 0;
        if (isExc) Exceptions++;
        var log = new ModbusSlaveLogEntry
        {
            Time = now, Slave = slave, Function = fc, Request = req, Summary = summary, IsError = isExc,
            Response = broadcast ? "（广播，不应答）" : "",
            WrittenArea = isExc ? '\0' : area, WrittenStart = start, WrittenCount = count,
        };
        if (broadcast) return (null, log);
        return (resp, log);
    }

    private static byte[] Exc(byte fc, byte code) => new[] { (byte)(fc | 0x80), code };

    private static ushort U16(byte[] p, int i) => (ushort)((p[i] << 8) | p[i + 1]);

    private (byte[] Resp, string Summary, char Area, int Start, int Count) Execute(byte[] p)
    {
        if (p.Length == 0) return (Exc(0, 1), "空请求", '\0', 0, 0);
        byte fc = p[0];
        var m = Model;
        lock (m.SyncRoot)
        {
            switch (fc)
            {
                case 1:
                case 2:
                {
                    if (p.Length < 5) return (Exc(fc, 3), "请求长度错误", '\0', 0, 0);
                    int start = U16(p, 1), qty = U16(p, 3);
                    if (qty < 1 || qty > 2000) return (Exc(fc, 3), $"数量 {qty} 无效", '\0', 0, 0);
                    if (start + qty > m.Size) return (Exc(fc, 2), $"地址 {start}~{start + qty - 1} 超出范围（0~{m.Size - 1}）", '\0', 0, 0);
                    var src = fc == 1 ? m.Coils : m.DiscreteInputs;
                    int bytes = (qty + 7) / 8;
                    var r = new byte[2 + bytes];
                    r[0] = fc;
                    r[1] = (byte)bytes;
                    for (int i = 0; i < qty; i++)
                        if (src[start + i]) r[2 + i / 8] |= (byte)(1 << (i % 8));
                    return (r, $"读{(fc == 1 ? "线圈" : "离散输入")} {start} ×{qty}", '\0', 0, 0);
                }
                case 3:
                case 4:
                {
                    if (p.Length < 5) return (Exc(fc, 3), "请求长度错误", '\0', 0, 0);
                    int start = U16(p, 1), qty = U16(p, 3);
                    if (qty < 1 || qty > 125) return (Exc(fc, 3), $"数量 {qty} 无效", '\0', 0, 0);
                    if (start + qty > m.Size) return (Exc(fc, 2), $"地址 {start}~{start + qty - 1} 超出范围（0~{m.Size - 1}）", '\0', 0, 0);
                    var src = fc == 3 ? m.Holding : m.Input;
                    var r = new byte[2 + qty * 2];
                    r[0] = fc;
                    r[1] = (byte)(qty * 2);
                    for (int i = 0; i < qty; i++)
                    {
                        r[2 + i * 2] = (byte)(src[start + i] >> 8);
                        r[3 + i * 2] = (byte)src[start + i];
                    }
                    return (r, $"读{(fc == 3 ? "保持寄存器" : "输入寄存器")} {start} ×{qty}", '\0', 0, 0);
                }
                case 5:
                {
                    if (p.Length < 5) return (Exc(fc, 3), "请求长度错误", '\0', 0, 0);
                    int addr = U16(p, 1), val = U16(p, 3);
                    if (val != 0xFF00 && val != 0x0000) return (Exc(fc, 3), $"线圈值 0x{val:X4} 无效（应为 FF00/0000）", '\0', 0, 0);
                    if (addr >= m.Size) return (Exc(fc, 2), $"地址 {addr} 超出范围", '\0', 0, 0);
                    m.Coils[addr] = val == 0xFF00;
                    return (p.Take(5).ToArray(), $"写线圈 {addr} = {(val == 0xFF00 ? "ON" : "OFF")}", 'C', addr, 1);
                }
                case 6:
                {
                    if (p.Length < 5) return (Exc(fc, 3), "请求长度错误", '\0', 0, 0);
                    int addr = U16(p, 1);
                    ushort val = U16(p, 3);
                    if (addr >= m.Size) return (Exc(fc, 2), $"地址 {addr} 超出范围", '\0', 0, 0);
                    m.Holding[addr] = val;
                    return (p.Take(5).ToArray(), $"写寄存器 {addr} = {val}", 'H', addr, 1);
                }
                case 15:
                {
                    if (p.Length < 6) return (Exc(fc, 3), "请求长度错误", '\0', 0, 0);
                    int start = U16(p, 1), qty = U16(p, 3), bc = p[5];
                    if (qty < 1 || qty > 1968 || bc != (qty + 7) / 8 || p.Length < 6 + bc)
                        return (Exc(fc, 3), $"数量 {qty} / 字节数 {bc} 无效", '\0', 0, 0);
                    if (start + qty > m.Size) return (Exc(fc, 2), $"地址 {start}~{start + qty - 1} 超出范围", '\0', 0, 0);
                    for (int i = 0; i < qty; i++) m.Coils[start + i] = (p[6 + i / 8] & (1 << (i % 8))) != 0;
                    return (p.Take(5).ToArray(), $"写多个线圈 {start} ×{qty}", 'C', start, qty);
                }
                case 16:
                {
                    if (p.Length < 6) return (Exc(fc, 3), "请求长度错误", '\0', 0, 0);
                    int start = U16(p, 1), qty = U16(p, 3), bc = p[5];
                    if (qty < 1 || qty > 123 || bc != qty * 2 || p.Length < 6 + bc)
                        return (Exc(fc, 3), $"数量 {qty} / 字节数 {bc} 无效", '\0', 0, 0);
                    if (start + qty > m.Size) return (Exc(fc, 2), $"地址 {start}~{start + qty - 1} 超出范围", '\0', 0, 0);
                    for (int i = 0; i < qty; i++) m.Holding[start + i] = U16(p, 6 + i * 2);
                    return (p.Take(5).ToArray(), $"写多个寄存器 {start} ×{qty}", 'H', start, qty);
                }
                default:
                    return (Exc(fc, 1), $"不支持的功能码 0x{fc:X2}", '\0', 0, 0);
            }
        }
    }
}
