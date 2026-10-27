using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SerialTool.Features;

/// <summary>多字节数值的字节序（以 32 位 ABCD = 大端为基准命名，Modbus 常用叫法）。</summary>
public enum ByteOrder
{
    /// <summary>大端：A B C D。</summary>
    ABCD,
    /// <summary>小端：D C B A（整体反转）。</summary>
    DCBA,
    /// <summary>字内字节交换：B A D C（每个 16 位字内两字节互换）。</summary>
    BADC,
    /// <summary>字交换：C D A B（16 位字的顺序反转，字内字节不变）。</summary>
    CDAB,
}

public enum NumType { Int8, UInt8, Int16, UInt16, Int32, UInt32, Int64, UInt64, Float, Double }

/// <summary>字节串的文本表示形式。</summary>
public enum ByteFormat { Hex, Text, Decimal, Binary }

/// <summary>数据转换工具：HEX / 文本 / 十进制 / 二进制 / 整数 / 浮点之间互转。不依赖 WPF。</summary>
public static class DataConvert
{
    public static readonly ByteOrder[] AllOrders = { ByteOrder.ABCD, ByteOrder.DCBA, ByteOrder.BADC, ByteOrder.CDAB };
    public static readonly NumType[] AllTypes = (NumType[])Enum.GetValues(typeof(NumType));

    public static string OrderLabel(ByteOrder o) => o switch
    {
        ByteOrder.ABCD => "ABCD（大端）",
        ByteOrder.DCBA => "DCBA（小端）",
        ByteOrder.BADC => "BADC（字内交换）",
        _ => "CDAB（字交换）",
    };

    public static string TypeLabel(NumType t) => t switch
    {
        NumType.Int8 => "int8",
        NumType.UInt8 => "uint8",
        NumType.Int16 => "int16",
        NumType.UInt16 => "uint16",
        NumType.Int32 => "int32",
        NumType.UInt32 => "uint32",
        NumType.Int64 => "int64",
        NumType.UInt64 => "uint64",
        NumType.Float => "float",
        _ => "double",
    };

    public static int SizeOf(NumType t) => t switch
    {
        NumType.Int8 or NumType.UInt8 => 1,
        NumType.Int16 or NumType.UInt16 => 2,
        NumType.Int32 or NumType.UInt32 or NumType.Float => 4,
        _ => 8,
    };

    /// <summary>
    /// 在“大端字节”与指定字节序之间转换（该变换是自反的：同一函数既用于编码也用于解码）。
    /// 1 字节不变；2 字节时 ABCD/CDAB 不变、DCBA/BADC 互换两字节；8 字节时 CDAB 反转 4 个 16 位字的顺序。
    /// </summary>
    public static byte[] Reorder(byte[] data, ByteOrder order)
    {
        var r = (byte[])data.Clone();
        int n = r.Length;
        switch (order)
        {
            case ByteOrder.ABCD:
                break;
            case ByteOrder.DCBA:
                Array.Reverse(r);
                break;
            case ByteOrder.BADC:
                for (int i = 0; i + 1 < n; i += 2) (r[i], r[i + 1]) = (r[i + 1], r[i]);
                break;
            case ByteOrder.CDAB:
                if (n % 2 != 0) break;
                int words = n / 2;
                for (int w = 0; w < words; w++)
                {
                    r[w * 2] = data[(words - 1 - w) * 2];
                    r[w * 2 + 1] = data[(words - 1 - w) * 2 + 1];
                }
                break;
        }
        return r;
    }

    /// <summary>每 groupSize 字节一组反转（大小端互换）；末尾不足一组的部分原样保留。</summary>
    public static byte[] EndianSwap(byte[] data, int groupSize)
    {
        if (groupSize < 2) return (byte[])data.Clone();
        var r = (byte[])data.Clone();
        for (int i = 0; i + groupSize <= r.Length; i += groupSize) Array.Reverse(r, i, groupSize);
        return r;
    }

    // ------------------------------------------------------------------
    // 数值 ↔ 字节
    // ------------------------------------------------------------------

    /// <summary>把数值文本按类型与字节序编码为字节。支持 0x 前缀的十六进制整数。</summary>
    public static bool TryEncode(NumType type, string text, ByteOrder order, out byte[] bytes, out string error)
    {
        bytes = Array.Empty<byte>();
        error = "";
        text = (text ?? "").Trim().Replace("_", "");
        if (text.Length == 0) { error = "请输入数值"; return false; }
        byte[] be;
        try
        {
            switch (type)
            {
                case NumType.Float:
                case NumType.Double:
                {
                    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                    {
                        if (text.Equals("nan", StringComparison.OrdinalIgnoreCase)) d = double.NaN;
                        else if (text.Equals("inf", StringComparison.OrdinalIgnoreCase) || text.Equals("+inf", StringComparison.OrdinalIgnoreCase)) d = double.PositiveInfinity;
                        else if (text.Equals("-inf", StringComparison.OrdinalIgnoreCase)) d = double.NegativeInfinity;
                        else { error = "不是有效的浮点数"; return false; }
                    }
                    if (type == NumType.Float)
                    {
                        if (!double.IsNaN(d) && !double.IsInfinity(d) && Math.Abs(d) > float.MaxValue) { error = "超出 float 范围"; return false; }
                        be = BitConverter.GetBytes((float)d);
                    }
                    else be = BitConverter.GetBytes(d);
                    if (BitConverter.IsLittleEndian) Array.Reverse(be);
                    break;
                }
                default:
                {
                    bool signed = type is NumType.Int8 or NumType.Int16 or NumType.Int32 or NumType.Int64;
                    int size = SizeOf(type);
                    System.Numerics.BigInteger v;
                    bool neg = text.StartsWith("-");
                    string body = neg || text.StartsWith("+") ? text.Substring(1) : text;
                    if (body.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!ulong.TryParse(body.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hv)) { error = "不是有效的十六进制整数"; return false; }
                        v = hv;
                        // 0x 形式按原始位模式理解（如 int16 输入 0xFFFF 表示 -1）
                        if (!neg && signed && size < 8 && hv < (1UL << (size * 8)) && hv >= (1UL << (size * 8 - 1)))
                            v -= System.Numerics.BigInteger.One << (size * 8);
                        else if (!neg && signed && size == 8 && hv > long.MaxValue)
                            v -= System.Numerics.BigInteger.One << 64;
                    }
                    else
                    {
                        if (!System.Numerics.BigInteger.TryParse(body, NumberStyles.None, CultureInfo.InvariantCulture, out v)) { error = "不是有效的整数"; return false; }
                    }
                    if (neg) v = -v;
                    var min = signed ? -(System.Numerics.BigInteger.One << (size * 8 - 1)) : System.Numerics.BigInteger.Zero;
                    var max = signed ? (System.Numerics.BigInteger.One << (size * 8 - 1)) - 1 : (System.Numerics.BigInteger.One << (size * 8)) - 1;
                    if (v < min || v > max) { error = $"超出 {TypeLabel(type)} 范围（{min} ~ {max}）"; return false; }
                    if (v < 0) v += System.Numerics.BigInteger.One << (size * 8);
                    be = new byte[size];
                    for (int i = size - 1; i >= 0; i--) { be[i] = (byte)(v & 0xFF); v >>= 8; }
                    break;
                }
            }
        }
        catch (Exception ex) { error = ex.Message; return false; }
        bytes = Reorder(be, order);
        return true;
    }

    /// <summary>把字节（长度必须等于类型大小）按字节序解码为数值文本。</summary>
    public static bool TryDecode(NumType type, byte[] data, ByteOrder order, out string value)
    {
        value = "";
        int size = SizeOf(type);
        if (data.Length != size) return false;
        var be = Reorder(data, order);
        ulong u = 0;
        foreach (var b in be) u = (u << 8) | b;
        var ci = CultureInfo.InvariantCulture;
        switch (type)
        {
            case NumType.Int8: value = ((sbyte)u).ToString(ci); break;
            case NumType.UInt8: value = ((byte)u).ToString(ci); break;
            case NumType.Int16: value = ((short)u).ToString(ci); break;
            case NumType.UInt16: value = ((ushort)u).ToString(ci); break;
            case NumType.Int32: value = ((int)u).ToString(ci); break;
            case NumType.UInt32: value = ((uint)u).ToString(ci); break;
            case NumType.Int64: value = ((long)u).ToString(ci); break;
            case NumType.UInt64: value = u.ToString(ci); break;
            case NumType.Float: value = FormatFloat(BitConverter.Int32BitsToSingle((int)(uint)u)); break;
            case NumType.Double: value = FormatDouble(BitConverter.Int64BitsToDouble((long)u)); break;
        }
        return true;
    }

    public static string FormatFloat(float f) =>
        float.IsNaN(f) ? "NaN" : float.IsPositiveInfinity(f) ? "+Inf" : float.IsNegativeInfinity(f) ? "-Inf" : f.ToString("R", CultureInfo.InvariantCulture);

    public static string FormatDouble(double d) =>
        double.IsNaN(d) ? "NaN" : double.IsPositiveInfinity(d) ? "+Inf" : double.IsNegativeInfinity(d) ? "-Inf" : d.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>把一串字节按类型连续解码成多个数值（多余不足一个的尾部字节忽略）。</summary>
    public static List<string> DecodeArray(NumType type, byte[] data, ByteOrder order)
    {
        int size = SizeOf(type);
        var list = new List<string>();
        for (int i = 0; i + size <= data.Length; i += size)
        {
            var chunk = new byte[size];
            Array.Copy(data, i, chunk, 0, size);
            TryDecode(type, chunk, order, out var s);
            list.Add(s);
        }
        return list;
    }

    // ------------------------------------------------------------------
    // 字节串 ↔ 文本表示
    // ------------------------------------------------------------------

    public static string Format(byte[] data, ByteFormat fmt, Encoding enc) => fmt switch
    {
        ByteFormat.Hex => string.Join(" ", data.Select(b => b.ToString("X2"))),
        ByteFormat.Text => enc.GetString(data),
        ByteFormat.Decimal => string.Join(" ", data.Select(b => b.ToString(CultureInfo.InvariantCulture))),
        _ => string.Join(" ", data.Select(b => Convert.ToString(b, 2).PadLeft(8, '0'))),
    };

    private static readonly char[] Seps = { ' ', ',', ';', '\t', '\r', '\n', '-', ':', '|' };
    private static readonly char[] DecSeps = { ' ', ',', ';', '\t', '\r', '\n', '|' };

    public static bool TryParse(string input, ByteFormat fmt, Encoding enc, out byte[] data, out string error)
    {
        data = Array.Empty<byte>();
        error = "";
        input ??= "";
        switch (fmt)
        {
            case ByteFormat.Text:
                data = enc.GetBytes(input);
                return true;
            case ByteFormat.Hex:
                return TryParseHex(input, out data, out error);
            case ByteFormat.Decimal:
            {
                var list = new List<byte>();
                foreach (var tok in input.Split(DecSeps, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!int.TryParse(tok, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) || v < -128 || v > 255)
                    {
                        error = $"无效的十进制字节：{tok}（应为 0~255，或 -128~-1）";
                        return false;
                    }
                    list.Add((byte)v);
                }
                data = list.ToArray();
                return true;
            }
            default:
            {
                var list = new List<byte>();
                foreach (var tokRaw in input.Split(Seps, StringSplitOptions.RemoveEmptyEntries))
                {
                    string tok = tokRaw.StartsWith("0b", StringComparison.OrdinalIgnoreCase) ? tokRaw.Substring(2) : tokRaw;
                    if (tok.Length == 0 || tok.Any(c => c != '0' && c != '1'))
                    {
                        error = $"无效的二进制：{tokRaw}";
                        return false;
                    }
                    if (tok.Length % 8 != 0 && tok.Length > 8) { error = $"二进制位数应为 8 的倍数：{tokRaw}"; return false; }
                    if (tok.Length <= 8) list.Add(Convert.ToByte(tok, 2));
                    else for (int i = 0; i < tok.Length; i += 8) list.Add(Convert.ToByte(tok.Substring(i, 8), 2));
                }
                data = list.ToArray();
                return true;
            }
        }
    }

    /// <summary>HEX 解析：允许空格/逗号/0x 前缀，连续书写时按两位一组；奇数位报错。</summary>
    public static bool TryParseHex(string input, out byte[] data, out string error)
    {
        data = Array.Empty<byte>();
        error = "";
        var list = new List<byte>();
        foreach (var raw in (input ?? "").Split(Seps, StringSplitOptions.RemoveEmptyEntries))
        {
            string t = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? raw.Substring(2) : raw;
            if (t.EndsWith("h", StringComparison.OrdinalIgnoreCase) && t.Length > 1) t = t.Substring(0, t.Length - 1);
            if (t.Length == 0) continue;
            if (t.Length == 1) t = "0" + t;
            if (t.Length % 2 != 0) { error = $"HEX 位数为奇数：{raw}"; return false; }
            for (int i = 0; i < t.Length; i += 2)
            {
                if (!byte.TryParse(t.Substring(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
                {
                    error = $"无效的 HEX：{raw}";
                    return false;
                }
                list.Add(b);
            }
        }
        data = list.ToArray();
        return true;
    }
}
