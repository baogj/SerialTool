using System;
using System.Collections.Generic;

namespace SerialTool.Features;

/// <summary>发送端自动追加的校验算法。</summary>
public enum ChecksumKind
{
    None,
    ModbusCrc16,     // poly 0xA001 (反射 0x8005), init 0xFFFF，低字节在前
    Crc16XModem,     // poly 0x1021, init 0x0000，高字节在前
    Crc16CcittFalse, // poly 0x1021, init 0xFFFF，高字节在前
    Sum8,            // 8 位累加和
    Xor8,            // 异或
}

public static class Checksum
{
    public static readonly IReadOnlyList<(ChecksumKind Kind, string Name)> Options = new[]
    {
        (ChecksumKind.None, "无"),
        (ChecksumKind.ModbusCrc16, "Modbus CRC16（低字节在前）"),
        (ChecksumKind.Crc16XModem, "CRC16-CCITT (XModem, 初值 0)"),
        (ChecksumKind.Crc16CcittFalse, "CRC16-CCITT-FALSE (初值 FFFF)"),
        (ChecksumKind.Sum8, "累加和 (8 位)"),
        (ChecksumKind.Xor8, "XOR 异或"),
    };

    public static string DisplayName(ChecksumKind k)
    {
        foreach (var o in Options) if (o.Kind == k) return o.Name;
        return k.ToString();
    }

    /// <summary>脚本中使用的短名：NONE / MODBUS / XMODEM / CCITT / SUM / XOR。</summary>
    public static bool TryParseShortName(string s, out ChecksumKind kind)
    {
        switch (s.Trim().ToUpperInvariant())
        {
            case "NONE": case "OFF": case "无": kind = ChecksumKind.None; return true;
            case "MODBUS": case "CRC16": case "MODBUSCRC16": kind = ChecksumKind.ModbusCrc16; return true;
            case "XMODEM": case "CCITT0": kind = ChecksumKind.Crc16XModem; return true;
            case "CCITT": case "CCITT-FALSE": case "CCITTFALSE": kind = ChecksumKind.Crc16CcittFalse; return true;
            case "SUM": case "SUM8": kind = ChecksumKind.Sum8; return true;
            case "XOR": case "XOR8": kind = ChecksumKind.Xor8; return true;
            default: kind = ChecksumKind.None; return false;
        }
    }

    public static ushort ModbusCrc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
        }
        return crc;
    }

    public static ushort Crc16Ccitt(ReadOnlySpan<byte> data, ushort init)
    {
        ushort crc = init;
        foreach (byte b in data)
        {
            crc ^= (ushort)(b << 8);
            for (int i = 0; i < 8; i++)
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }
        return crc;
    }

    public static byte Sum8(ReadOnlySpan<byte> data)
    {
        int s = 0;
        foreach (byte b in data) s += b;
        return (byte)s;
    }

    public static byte Xor8(ReadOnlySpan<byte> data)
    {
        byte x = 0;
        foreach (byte b in data) x ^= b;
        return x;
    }

    /// <summary>计算要追加的校验字节（按该算法约定的字节序）。</summary>
    public static byte[] Compute(ChecksumKind kind, ReadOnlySpan<byte> data)
    {
        switch (kind)
        {
            case ChecksumKind.ModbusCrc16:
                {
                    ushort c = ModbusCrc16(data);
                    return new[] { (byte)(c & 0xFF), (byte)(c >> 8) };
                }
            case ChecksumKind.Crc16XModem:
                {
                    ushort c = Crc16Ccitt(data, 0x0000);
                    return new[] { (byte)(c >> 8), (byte)(c & 0xFF) };
                }
            case ChecksumKind.Crc16CcittFalse:
                {
                    ushort c = Crc16Ccitt(data, 0xFFFF);
                    return new[] { (byte)(c >> 8), (byte)(c & 0xFF) };
                }
            case ChecksumKind.Sum8: return new[] { Sum8(data) };
            case ChecksumKind.Xor8: return new[] { Xor8(data) };
            default: return Array.Empty<byte>();
        }
    }

    public static byte[] Append(ChecksumKind kind, byte[] data)
    {
        if (kind == ChecksumKind.None) return data;
        byte[] c = Compute(kind, data);
        var r = new byte[data.Length + c.Length];
        Buffer.BlockCopy(data, 0, r, 0, data.Length);
        Buffer.BlockCopy(c, 0, r, data.Length, c.Length);
        return r;
    }

    public static string ToHex(ReadOnlySpan<byte> b)
    {
        var sb = new System.Text.StringBuilder(b.Length * 3);
        for (int i = 0; i < b.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(b[i].ToString("X2"));
        }
        return sb.ToString();
    }
}
