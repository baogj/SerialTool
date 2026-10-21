using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SerialTool.Features;

public enum ModbusFraming { Rtu, Tcp }
public enum RegisterFormat { UInt16, Int16, Hex, Binary, Int32, UInt32, Float32 }
/// <summary>32 位数值跨两个寄存器时的字节序（A = 最高字节）。</summary>
public enum WordOrder { ABCD, CDAB, BADC, DCBA }
public enum ModbusResultStatus { Incomplete, Ok, Exception, CrcError, Timeout, NoResponseExpected }

public sealed class ModbusRequest
{
    public byte Slave { get; init; }
    public byte Function { get; init; }
    public ushort Start { get; init; }
    public ushort Quantity { get; init; }
    public ushort[] Registers { get; init; } = Array.Empty<ushort>();
    public bool[] Coils { get; init; } = Array.Empty<bool>();
    public ModbusFraming Framing { get; init; }
    public ushort TransactionId { get; init; }

    public bool IsRead => Function is 1 or 2 or 3 or 4;
    public bool IsBitFunction => Function is 1 or 2 or 5 or 15;
}

public sealed class ModbusResult
{
    public ModbusResultStatus Status { get; init; }
    public byte ExceptionCode { get; init; }
    public ushort[] Registers { get; init; } = Array.Empty<ushort>();
    public bool[] Coils { get; init; } = Array.Empty<bool>();
    public byte[] Raw { get; init; } = Array.Empty<byte>();
    /// <summary>写功能的回显：地址 / 值或数量。</summary>
    public ushort EchoAddress { get; init; }
    public ushort EchoValue { get; init; }
    public int Consumed { get; init; }
    public double ElapsedMs { get; set; }
}

/// <summary>Modbus 主站：组帧（RTU / Modbus TCP MBAP）、解析应答、寄存器值格式化。纯逻辑，可单元测试。</summary>
public static class ModbusMaster
{
    public static readonly (byte Code, string Name)[] Functions =
    {
        (0x01, "01 读线圈"), (0x02, "02 读离散输入"), (0x03, "03 读保持寄存器"), (0x04, "04 读输入寄存器"),
        (0x05, "05 写单个线圈"), (0x06, "06 写单个寄存器"), (0x0F, "0F 写多个线圈"), (0x10, "10 写多个寄存器"),
    };

    public static readonly (RegisterFormat Format, string Name)[] Formats =
    {
        (RegisterFormat.UInt16, "UInt16 无符号"), (RegisterFormat.Int16, "Int16 有符号"), (RegisterFormat.Hex, "Hex 十六进制"),
        (RegisterFormat.Binary, "Binary 二进制"), (RegisterFormat.Int32, "Int32（两寄存器）"),
        (RegisterFormat.UInt32, "UInt32（两寄存器）"), (RegisterFormat.Float32, "Float32（两寄存器）"),
    };

    public static bool Is32Bit(RegisterFormat f) => f is RegisterFormat.Int32 or RegisterFormat.UInt32 or RegisterFormat.Float32;

    public static string ExceptionName(byte code) => code switch
    {
        1 => "非法功能码",
        2 => "非法数据地址",
        3 => "非法数据值",
        4 => "从站设备故障",
        5 => "确认（处理中）",
        6 => "从站设备忙",
        8 => "存储奇偶校验错误",
        10 => "网关路径不可用",
        11 => "网关目标设备无响应",
        _ => "未知异常",
    };

    // ------------------------------------------------------------------
    // 组帧
    // ------------------------------------------------------------------

    public static byte[] BuildPdu(ModbusRequest r)
    {
        var p = new List<byte> { r.Function };
        switch (r.Function)
        {
            case 1: case 2: case 3: case 4:
                AddU16(p, r.Start);
                AddU16(p, r.Quantity);
                break;
            case 5:
                AddU16(p, r.Start);
                AddU16(p, r.Coils.Length > 0 && r.Coils[0] ? (ushort)0xFF00 : (ushort)0x0000);
                break;
            case 6:
                AddU16(p, r.Start);
                AddU16(p, r.Registers.Length > 0 ? r.Registers[0] : (ushort)0);
                break;
            case 15:
            {
                int n = r.Coils.Length;
                AddU16(p, r.Start);
                AddU16(p, (ushort)n);
                int bytes = (n + 7) / 8;
                p.Add((byte)bytes);
                for (int i = 0; i < bytes; i++)
                {
                    byte b = 0;
                    for (int bit = 0; bit < 8; bit++)
                    {
                        int idx = i * 8 + bit;
                        if (idx < n && r.Coils[idx]) b |= (byte)(1 << bit);
                    }
                    p.Add(b);
                }
                break;
            }
            case 16:
                AddU16(p, r.Start);
                AddU16(p, (ushort)r.Registers.Length);
                p.Add((byte)(r.Registers.Length * 2));
                foreach (var v in r.Registers) AddU16(p, v);
                break;
            default:
                throw new ArgumentException("不支持的功能码");
        }
        return p.ToArray();
    }

    public static byte[] BuildFrame(ModbusRequest r)
    {
        byte[] pdu = BuildPdu(r);
        if (r.Framing == ModbusFraming.Tcp)
        {
            var f = new byte[7 + pdu.Length];
            f[0] = (byte)(r.TransactionId >> 8);
            f[1] = (byte)r.TransactionId;
            f[2] = 0;
            f[3] = 0;
            int len = pdu.Length + 1;
            f[4] = (byte)(len >> 8);
            f[5] = (byte)len;
            f[6] = r.Slave;
            Buffer.BlockCopy(pdu, 0, f, 7, pdu.Length);
            return f;
        }
        var body = new byte[1 + pdu.Length];
        body[0] = r.Slave;
        Buffer.BlockCopy(pdu, 0, body, 1, pdu.Length);
        return Checksum.Append(ChecksumKind.ModbusCrc16, body);
    }

    private static void AddU16(List<byte> p, ushort v)
    {
        p.Add((byte)(v >> 8));
        p.Add((byte)v);
    }

    /// <summary>根据界面输入生成请求，校验范围。values：逗号/空格分隔，支持十进制、0x 十六进制、负数、小数（Float32）。</summary>
    public static bool TryCreate(int slave, byte function, int start, int quantity, string? values,
        RegisterFormat format, WordOrder order, ModbusFraming framing, ushort transactionId,
        out ModbusRequest request, out string? error)
    {
        request = null!;
        error = null;
        if (slave < 0 || slave > 247) { error = "从站地址需为 0~247（0 为广播，只能用于写）"; return false; }
        if (start < 0 || start > 0xFFFF) { error = "起始地址需为 0~65535"; return false; }
        if (slave == 0 && function is 1 or 2 or 3 or 4) { error = "广播地址 0 不能用于读操作"; return false; }

        ushort[] regs = Array.Empty<ushort>();
        bool[] coils = Array.Empty<bool>();
        ushort qty = 0;
        switch (function)
        {
            case 1: case 2:
                if (quantity < 1 || quantity > 2000) { error = "读线圈/离散输入数量需为 1~2000"; return false; }
                qty = (ushort)quantity;
                break;
            case 3: case 4:
                if (quantity < 1 || quantity > 125) { error = "读寄存器数量需为 1~125"; return false; }
                qty = (ushort)quantity;
                break;
            case 5:
                if (!TryParseCoils(values, out coils, out error)) return false;
                if (coils.Length != 1) { error = "写单个线圈需要 1 个值（0/1、ON/OFF）"; return false; }
                qty = 1;
                break;
            case 15:
                if (!TryParseCoils(values, out coils, out error)) return false;
                if (coils.Length < 1 || coils.Length > 1968) { error = "写多个线圈需要 1~1968 个值"; return false; }
                qty = (ushort)coils.Length;
                break;
            case 6:
                if (!TryParseRegisterValues(values, Is32Bit(format) ? RegisterFormat.Int16 : format, order, out regs, out error)) return false;
                if (regs.Length != 1) { error = "写单个寄存器需要 1 个 16 位值（32 位数值请用 10 写多个寄存器）"; return false; }
                qty = 1;
                break;
            case 16:
                if (!TryParseRegisterValues(values, format, order, out regs, out error)) return false;
                if (regs.Length < 1 || regs.Length > 123) { error = "写多个寄存器需要 1~123 个寄存器"; return false; }
                qty = (ushort)regs.Length;
                break;
            default:
                error = "不支持的功能码";
                return false;
        }
        if (start + Math.Max(1, (int)qty) - 1 > 0xFFFF) { error = "地址范围超出 65535"; return false; }

        request = new ModbusRequest
        {
            Slave = (byte)slave, Function = function, Start = (ushort)start, Quantity = qty,
            Registers = regs, Coils = coils, Framing = framing, TransactionId = transactionId,
        };
        return true;
    }

    private static string[] SplitValues(string? text) =>
        (text ?? "").Split(new[] { ',', ' ', ';', '\t', '\r', '\n', '，' }, StringSplitOptions.RemoveEmptyEntries);

    public static bool TryParseCoils(string? text, out bool[] coils, out string? error)
    {
        error = null;
        var list = new List<bool>();
        foreach (var raw in SplitValues(text))
        {
            string s = raw.Trim().ToUpperInvariant();
            if (s is "1" or "ON" or "TRUE" or "FF00") list.Add(true);
            else if (s is "0" or "OFF" or "FALSE" or "0000") list.Add(false);
            else
            {
                coils = Array.Empty<bool>();
                error = $"线圈值无效：“{raw}”（应为 0/1 或 ON/OFF）";
                return false;
            }
        }
        coils = list.ToArray();
        if (coils.Length == 0) error = "请在“写入值”中填写线圈值，如 1,0,1";
        return coils.Length > 0;
    }

    /// <summary>把一组数值编码为寄存器（32 位格式每个值占两个寄存器）。</summary>
    public static bool TryParseRegisterValues(string? text, RegisterFormat format, WordOrder order, out ushort[] regs, out string? error)
    {
        error = null;
        var list = new List<ushort>();
        foreach (var raw in SplitValues(text))
        {
            string s = raw.Trim();
            try
            {
                switch (format)
                {
                    case RegisterFormat.Float32:
                    {
                        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                            throw new FormatException();
                        uint bits = BitConverter.SingleToUInt32Bits((float)d);
                        list.AddRange(SplitU32(bits, order));
                        break;
                    }
                    case RegisterFormat.Int32:
                    case RegisterFormat.UInt32:
                    {
                        long v = ParseInteger(s);
                        if (format == RegisterFormat.Int32 ? v < int.MinValue || v > int.MaxValue : v < 0 || v > uint.MaxValue)
                            throw new OverflowException();
                        list.AddRange(SplitU32(unchecked((uint)v), order));
                        break;
                    }
                    case RegisterFormat.Binary:
                    {
                        string b = s.StartsWith("0b", StringComparison.OrdinalIgnoreCase) ? s.Substring(2) : s;
                        b = b.Replace("_", "");
                        if (b.Length == 0 || b.Length > 16 || b.Any(ch => ch != '0' && ch != '1')) throw new FormatException();
                        list.Add(Convert.ToUInt16(b, 2));
                        break;
                    }
                    case RegisterFormat.Hex:
                    {
                        string h = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s.Substring(2) : s;
                        list.Add(ushort.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        break;
                    }
                    default:
                    {
                        long v = ParseInteger(s);
                        if (v < short.MinValue || v > ushort.MaxValue) throw new OverflowException();
                        list.Add(unchecked((ushort)v));
                        break;
                    }
                }
            }
            catch (OverflowException)
            {
                regs = Array.Empty<ushort>();
                error = $"数值超出范围：“{raw}”";
                return false;
            }
            catch (Exception)
            {
                regs = Array.Empty<ushort>();
                error = $"数值无效：“{raw}”";
                return false;
            }
        }
        regs = list.ToArray();
        if (regs.Length == 0) error = "请在“写入值”中填写要写入的数值，如 100,200";
        return regs.Length > 0;
    }

    private static long ParseInteger(string s)
    {
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return long.Parse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (s.StartsWith("-0x", StringComparison.OrdinalIgnoreCase))
            return -long.Parse(s.Substring(3), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return long.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    /// <summary>32 位值 → 两个寄存器（按字节序）。</summary>
    public static ushort[] SplitU32(uint v, WordOrder order)
    {
        byte a = (byte)(v >> 24), b = (byte)(v >> 16), c = (byte)(v >> 8), d = (byte)v;
        return order switch
        {
            WordOrder.CDAB => new[] { W(c, d), W(a, b) },
            WordOrder.BADC => new[] { W(b, a), W(d, c) },
            WordOrder.DCBA => new[] { W(d, c), W(b, a) },
            _ => new[] { W(a, b), W(c, d) },
        };
    }

    /// <summary>两个寄存器 → 32 位值（按字节序）。</summary>
    public static uint JoinU32(ushort r0, ushort r1, WordOrder order)
    {
        byte h0 = (byte)(r0 >> 8), l0 = (byte)r0, h1 = (byte)(r1 >> 8), l1 = (byte)r1;
        (byte a, byte b, byte c, byte d) = order switch
        {
            WordOrder.CDAB => (h1, l1, h0, l0),
            WordOrder.BADC => (l0, h0, l1, h1),
            WordOrder.DCBA => (l1, h1, l0, h0),
            _ => (h0, l0, h1, l1),
        };
        return ((uint)a << 24) | ((uint)b << 16) | ((uint)c << 8) | d;
    }

    private static ushort W(byte hi, byte lo) => (ushort)((hi << 8) | lo);

    /// <summary>格式化第 index 个寄存器。32 位格式：偶数偏移处显示合并后的值，奇数偏移显示“(续)”。</summary>
    public static string FormatRegister(IReadOnlyList<ushort> regs, int index, RegisterFormat format, WordOrder order)
    {
        ushort v = regs[index];
        switch (format)
        {
            case RegisterFormat.Int16: return ((short)v).ToString(CultureInfo.InvariantCulture);
            case RegisterFormat.Hex: return "0x" + v.ToString("X4");
            case RegisterFormat.Binary:
            {
                string s = Convert.ToString(v, 2).PadLeft(16, '0');
                return s.Substring(0, 4) + " " + s.Substring(4, 4) + " " + s.Substring(8, 4) + " " + s.Substring(12, 4);
            }
            case RegisterFormat.Int32:
            case RegisterFormat.UInt32:
            case RegisterFormat.Float32:
            {
                if (index % 2 == 1) return "（续）";
                if (index + 1 >= regs.Count) return "（缺少下一个寄存器）";
                uint u = JoinU32(v, regs[index + 1], order);
                return format switch
                {
                    RegisterFormat.Int32 => unchecked((int)u).ToString(CultureInfo.InvariantCulture),
                    RegisterFormat.UInt32 => u.ToString(CultureInfo.InvariantCulture),
                    _ => FormatFloat(BitConverter.UInt32BitsToSingle(u)),
                };
            }
            default: return v.ToString(CultureInfo.InvariantCulture);
        }
    }

    public static string FormatFloat(float f)
    {
        if (float.IsNaN(f)) return "NaN";
        if (float.IsInfinity(f)) return f > 0 ? "+Inf" : "-Inf";
        return f.ToString("G7", CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------------
    // 解析应答
    // ------------------------------------------------------------------

    /// <summary>在接收缓冲中寻找与请求匹配的应答。返回 Incomplete 表示还需要更多字节。</summary>
    public static ModbusResult TryParse(IReadOnlyList<byte> buf, ModbusRequest req)
    {
        return req.Framing == ModbusFraming.Tcp ? ParseTcp(buf, req) : ParseRtu(buf, req);
    }

    private static int ExpectedPduLength(IReadOnlyList<byte> buf, int pduOffset, ModbusRequest req, out bool needMore)
    {
        needMore = false;
        if (buf.Count <= pduOffset) { needMore = true; return -1; }
        byte fc = buf[pduOffset];
        if (fc == (req.Function | 0x80)) return 2;
        if (fc != req.Function) return -1;
        switch (req.Function)
        {
            case 1: case 2: case 3: case 4:
                if (buf.Count <= pduOffset + 1) { needMore = true; return -1; }
                return 2 + buf[pduOffset + 1];
            default:
                return 5;
        }
    }

    private static ModbusResult ParseRtu(IReadOnlyList<byte> buf, ModbusRequest req)
    {
        bool sawCrcError = false;
        for (int i = 0; i + 1 < buf.Count || i < buf.Count; i++)
        {
            if (buf[i] != req.Slave) continue;
            int pduLen = ExpectedPduLength(buf, i + 1, req, out bool needMore);
            if (needMore) return Incomplete(sawCrcError);
            if (pduLen < 0) continue;
            int total = 1 + pduLen + 2;
            if (i + total > buf.Count) return Incomplete(sawCrcError);
            ushort calc = ModbusRtuParserCrc(buf, i, total - 2);
            ushort got = (ushort)(buf[i + total - 2] | (buf[i + total - 1] << 8));
            var raw = Slice(buf, i, total);
            if (calc != got)
            {
                sawCrcError = true;
                continue;
            }
            return Decode(buf, i + 1, pduLen, req, raw, i + total);
        }
        return Incomplete(sawCrcError);
    }

    private static ModbusResult Incomplete(bool crc) =>
        new() { Status = crc ? ModbusResultStatus.CrcError : ModbusResultStatus.Incomplete };

    private static ModbusResult ParseTcp(IReadOnlyList<byte> buf, ModbusRequest req)
    {
        int i = 0;
        while (i + 7 <= buf.Count)
        {
            ushort tid = (ushort)((buf[i] << 8) | buf[i + 1]);
            ushort pid = (ushort)((buf[i + 2] << 8) | buf[i + 3]);
            int len = (buf[i + 4] << 8) | buf[i + 5];
            if (pid != 0 || len < 2 || len > 254)
            {
                i++;
                continue;
            }
            if (i + 6 + len > buf.Count) return Incomplete(false);
            if (tid == req.TransactionId && buf[i + 6] == req.Slave)
            {
                var raw = Slice(buf, i, 6 + len);
                int pduLen = len - 1;
                int expected = ExpectedPduLength(buf, i + 7, req, out bool needMore);
                if (!needMore && expected == pduLen)
                    return Decode(buf, i + 7, pduLen, req, raw, i + 6 + len);
            }
            i += 6 + len;
        }
        return Incomplete(false);
    }

    private static ModbusResult Decode(IReadOnlyList<byte> buf, int p, int pduLen, ModbusRequest req, byte[] raw, int consumed)
    {
        byte fc = buf[p];
        if ((fc & 0x80) != 0)
            return new ModbusResult { Status = ModbusResultStatus.Exception, ExceptionCode = buf[p + 1], Raw = raw, Consumed = consumed };

        switch (req.Function)
        {
            case 1: case 2:
            {
                int byteCount = buf[p + 1];
                int n = Math.Min(req.Quantity, byteCount * 8);
                var coils = new bool[n];
                for (int k = 0; k < n; k++) coils[k] = (buf[p + 2 + k / 8] & (1 << (k % 8))) != 0;
                return new ModbusResult { Status = ModbusResultStatus.Ok, Coils = coils, Raw = raw, Consumed = consumed };
            }
            case 3: case 4:
            {
                int byteCount = buf[p + 1];
                int n = byteCount / 2;
                var regs = new ushort[n];
                for (int k = 0; k < n; k++) regs[k] = (ushort)((buf[p + 2 + k * 2] << 8) | buf[p + 3 + k * 2]);
                return new ModbusResult { Status = ModbusResultStatus.Ok, Registers = regs, Raw = raw, Consumed = consumed };
            }
            default:
                return new ModbusResult
                {
                    Status = ModbusResultStatus.Ok, Raw = raw, Consumed = consumed,
                    EchoAddress = (ushort)((buf[p + 1] << 8) | buf[p + 2]),
                    EchoValue = (ushort)((buf[p + 3] << 8) | buf[p + 4]),
                };
        }
    }

    private static byte[] Slice(IReadOnlyList<byte> buf, int offset, int count)
    {
        var r = new byte[count];
        for (int k = 0; k < count; k++) r[k] = buf[offset + k];
        return r;
    }

    private static ushort ModbusRtuParserCrc(IReadOnlyList<byte> buf, int offset, int count)
    {
        ushort crc = 0xFFFF;
        for (int k = 0; k < count; k++)
        {
            crc ^= buf[offset + k];
            for (int b = 0; b < 8; b++)
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
        }
        return crc;
    }
}

/// <summary>
/// 一次请求-应答事务：Begin 之后，接收线程调用 Feed 喂入数据，WaitAsync 等待应答或超时。
/// 同一时间只有一个事务（主站是串行的）。
/// </summary>
public sealed class ModbusTransactionWaiter
{
    private readonly object _lock = new();
    private readonly List<byte> _buf = new();
    private ModbusRequest? _req;
    private TaskCompletionSource<ModbusResult>? _tcs;
    private ModbusResult? _pendingCrcError;

    public bool Busy
    {
        get { lock (_lock) return _req != null; }
    }

    public Task<ModbusResult> Begin(ModbusRequest req)
    {
        lock (_lock)
        {
            _buf.Clear();
            _req = req;
            _pendingCrcError = null;
            _tcs = new TaskCompletionSource<ModbusResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _tcs.Task;
        }
    }

    /// <summary>喂入收到的字节（任意线程）。</summary>
    public void Feed(byte[] data)
    {
        TaskCompletionSource<ModbusResult>? done = null;
        ModbusResult? result = null;
        lock (_lock)
        {
            if (_req == null || _tcs == null) return;
            if (_buf.Count + data.Length > 4096) _buf.Clear();
            _buf.AddRange(data);
            var r = ModbusMaster.TryParse(_buf, _req);
            if (r.Status == ModbusResultStatus.CrcError)
            {
                _pendingCrcError = new ModbusResult { Status = ModbusResultStatus.CrcError, Raw = _buf.ToArray() };
                return; // 继续等待，可能后面还有正确的帧；超时时报 CRC 错误
            }
            if (r.Status == ModbusResultStatus.Incomplete) return;
            result = r;
            done = _tcs;
            _req = null;
            _tcs = null;
        }
        done?.TrySetResult(result!);
    }

    /// <summary>等待应答；超时后结束事务（若期间收到过 CRC 错误帧则返回 CrcError）。</summary>
    public async Task<ModbusResult> WaitAsync(Task<ModbusResult> task, int timeoutMs, CancellationToken ct = default)
    {
        var finished = await Task.WhenAny(task, Task.Delay(timeoutMs, ct)).ConfigureAwait(false);
        if (finished == task) return await task.ConfigureAwait(false);
        lock (_lock)
        {
            var crc = _pendingCrcError;
            byte[] raw = _buf.ToArray();
            _req = null;
            _tcs = null;
            _pendingCrcError = null;
            if (task.IsCompleted) return task.Result;
            return crc ?? new ModbusResult { Status = ModbusResultStatus.Timeout, Raw = raw };
        }
    }

    public void Cancel()
    {
        TaskCompletionSource<ModbusResult>? t;
        lock (_lock)
        {
            t = _tcs;
            _req = null;
            _tcs = null;
        }
        t?.TrySetResult(new ModbusResult { Status = ModbusResultStatus.Timeout });
    }
}
