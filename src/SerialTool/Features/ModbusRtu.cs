using System;
using System.Collections.Generic;
using System.Text;

namespace SerialTool.Features;

public enum ModbusFrameKind
{
    Request,
    Response,
    Exception,
    Unknown,
}

public sealed class ModbusFrame
{
    public byte Address { get; init; }
    public byte Function { get; init; }
    public int Length { get; init; }
    public bool CrcOk { get; init; }
    public ushort CrcReceived { get; init; }
    public ushort CrcCalculated { get; init; }
    public ModbusFrameKind Kind { get; init; }
    public byte[] Raw { get; init; } = Array.Empty<byte>();
    public DateTime Time { get; init; }
    public bool IsTx { get; init; }

    public string Summary
    {
        get
        {
            string dir = IsTx ? "发→" : "收←";
            string crc = CrcOk ? "CRC✓" : $"CRC✗(算{CrcCalculated:X4}/收{CrcReceived:X4})";
            string kind = Kind switch
            {
                ModbusFrameKind.Exception => "异常",
                ModbusFrameKind.Request => "请求",
                ModbusFrameKind.Response => "响应",
                _ => "帧",
            };
            return $"[{Time:HH:mm:ss.fff}] {dir} {kind} Addr=0x{Address:X2} FC=0x{Function:X2} Len={Length} {crc}";
        }
    }
}

/// <summary>
/// 流式 Modbus RTU 帧检测：按功能码推算长度并用 CRC16 校验。
/// </summary>
public sealed class ModbusRtuParser
{
    private readonly List<byte> _buf = new(256);
    private DateTime _lastByteTime;
    private readonly int _gapMs;

    public ModbusRtuParser(int interFrameGapMs = 50)
    {
        _gapMs = Math.Max(5, interFrameGapMs);
    }

    public void Reset()
    {
        _buf.Clear();
        _lastByteTime = default;
    }

    public IReadOnlyList<ModbusFrame> Push(DataChunk chunk)
    {
        var frames = new List<ModbusFrame>();
        if (chunk.Data.Length == 0) return frames;

        if (_buf.Count > 0 && _lastByteTime != default &&
            (chunk.Time - _lastByteTime).TotalMilliseconds > _gapMs)
        {
            // 间隔过大：丢弃不完整缓冲，避免错帧粘连
            _buf.Clear();
        }

        foreach (byte b in chunk.Data)
        {
            _buf.Add(b);
            _lastByteTime = chunk.Time;
            TryExtract(frames, chunk.IsTx, chunk.Time);
        }

        // 防止异常流无限增长
        if (_buf.Count > 512) _buf.RemoveRange(0, _buf.Count - 64);
        return frames;
    }

    private void TryExtract(List<ModbusFrame> frames, bool isTx, DateTime time)
    {
        while (true)
        {
            if (_buf.Count < 4) return;
            int need = EstimateLength(_buf);
            if (need < 0)
            {
                // 无法识别：滑动 1 字节
                _buf.RemoveAt(0);
                continue;
            }
            if (_buf.Count < need) return;

            var raw = _buf.GetRange(0, need).ToArray();
            _buf.RemoveRange(0, need);
            ushort recv = (ushort)(raw[need - 2] | (raw[need - 1] << 8));
            ushort calc = Crc16(raw, 0, need - 2);
            bool ok = recv == calc;
            byte addr = raw[0];
            byte fc = raw[1];
            var kind = (fc & 0x80) != 0 ? ModbusFrameKind.Exception
                : isTx ? ModbusFrameKind.Request
                : ModbusFrameKind.Response;
            // 请求/响应启发式：若功能码高位未置且长度为典型请求长度则标 Request
            if ((fc & 0x80) == 0)
            {
                kind = IsTypicalRequest(fc, need) ? (isTx ? ModbusFrameKind.Request : ModbusFrameKind.Response)
                    : (isTx ? ModbusFrameKind.Request : ModbusFrameKind.Response);
            }

            frames.Add(new ModbusFrame
            {
                Address = addr,
                Function = fc,
                Length = need,
                CrcOk = ok,
                CrcReceived = recv,
                CrcCalculated = calc,
                Kind = kind,
                Raw = raw,
                Time = time,
                IsTx = isTx,
            });
        }
    }

    private static bool IsTypicalRequest(byte fc, int len)
    {
        byte f = (byte)(fc & 0x7F);
        return f switch
        {
            0x01 or 0x02 or 0x03 or 0x04 or 0x05 or 0x06 => len == 8,
            0x0F or 0x10 => len >= 9,
            _ => false,
        };
    }

    /// <summary>根据已缓冲前缀估算整帧长度；无法判断返回 -1。</summary>
    private static int EstimateLength(List<byte> buf)
    {
        if (buf.Count < 2) return -1;
        byte fc = buf[1];
        if ((fc & 0x80) != 0)
            return 5; // 异常响应：addr+fc+ex+crc

        byte f = (byte)(fc & 0x7F);
        switch (f)
        {
            case 0x01:
            case 0x02:
            case 0x03:
            case 0x04:
                // 请求 8；响应至少 5（addr+fc+bc+data+crc）
                if (buf.Count >= 3)
                {
                    // 若第 3 字节像 byte count（响应），且总量匹配
                    int bc = buf[2];
                    int respLen = 3 + bc + 2;
                    if (bc >= 1 && bc <= 250 && buf.Count >= Math.Min(respLen, 8))
                    {
                        // 优先按响应长度（当缓冲足够或明确不是 8 字节请求）
                        if (buf.Count >= respLen || (buf.Count > 8 && bc > 0))
                            return respLen;
                    }
                }
                return 8; // 默认按请求
            case 0x05:
            case 0x06:
                return 8;
            case 0x0F:
            case 0x10:
                if (buf.Count < 7) return 7; // 等够 byte count 字段
                {
                    int bc = buf[6];
                    return 9 + bc; // addr fc start(2) qty(2) bc data crc(2)
                }
            default:
                // 未知功能码：尝试在 4..64 长度内找 CRC 匹配
                return FindCrcMatch(buf);
        }
    }

    private static int FindCrcMatch(List<byte> buf)
    {
        int max = Math.Min(buf.Count, 64);
        for (int len = 4; len <= max; len++)
        {
            ushort recv = (ushort)(buf[len - 2] | (buf[len - 1] << 8));
            if (recv == Crc16(buf, 0, len - 2))
                return len;
        }
        return -1;
    }

    public static ushort Crc16(IList<byte> data, int offset, int count)
    {
        ushort crc = 0xFFFF;
        for (int i = 0; i < count; i++)
        {
            crc ^= data[offset + i];
            for (int b = 0; b < 8; b++)
            {
                bool lsb = (crc & 1) != 0;
                crc >>= 1;
                if (lsb) crc ^= 0xA001;
            }
        }
        return crc;
    }

    public static string FormatHex(byte[] raw)
    {
        var sb = new StringBuilder(raw.Length * 3);
        for (int i = 0; i < raw.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(raw[i].ToString("X2"));
        }
        return sb.ToString();
    }
}
