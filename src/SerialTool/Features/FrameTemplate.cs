using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SerialTool.Features;

public enum FrameFieldType { U8, I8, U16, I16, U32, I32, Float, Bytes }

/// <summary>帧中的一个命名字段（偏移从帧起始字节算起，含帧头）。</summary>
public sealed class FrameField
{
    public string Name { get; set; } = "";
    public int Offset { get; set; }
    public FrameFieldType Type { get; set; } = FrameFieldType.U8;
    public bool BigEndian { get; set; } = true;
    public double Scale { get; set; } = 1.0;
    public string Unit { get; set; } = "";
    /// <summary>Bytes 类型的字节数。</summary>
    public int Length { get; set; } = 1;

    [JsonIgnore]
    public int Size => Type switch
    {
        FrameFieldType.U8 or FrameFieldType.I8 => 1,
        FrameFieldType.U16 or FrameFieldType.I16 => 2,
        FrameFieldType.U32 or FrameFieldType.I32 or FrameFieldType.Float => 4,
        _ => Math.Max(1, Length),
    };

    public FrameField Clone() => (FrameField)MemberwiseClone();
}

/// <summary>
/// 自定义协议帧模板。帧结构：[帧头][…][长度字段][…][校验][帧尾]。
/// 帧长度确定方式（按优先级）：长度字段 → 固定长度 → 帧尾分隔。
/// 长度字段的值 L 计数的范围由 LengthCounts* 决定：
///   帧总长 = L + LengthAdjust + （未计入的帧头/长度字段/校验/帧尾字节数）。
/// </summary>
public sealed class FrameTemplate
{
    public string Name { get; set; } = "新模板";
    public string Header { get; set; } = "AA 55";
    public string Tail { get; set; } = "";

    public bool HasLength { get; set; } = true;
    public int LengthOffset { get; set; } = 2;
    public int LengthSize { get; set; } = 1;          // 1 / 2
    public bool LengthBigEndian { get; set; } = true;
    public bool LengthCountsHeader { get; set; }
    public bool LengthCountsLengthField { get; set; }
    public bool LengthCountsChecksum { get; set; }
    public bool LengthCountsTail { get; set; }
    public int LengthAdjust { get; set; }

    /// <summary>无长度字段且无帧尾时使用的固定帧长（0 = 不使用）。</summary>
    public int FixedLength { get; set; }

    public ChecksumKind Checksum { get; set; } = ChecksumKind.Sum8;
    /// <summary>校验计算起点（帧内偏移，0 = 含帧头）；终点为校验字段之前。</summary>
    public int ChecksumFrom { get; set; }
    /// <summary>校验字节顺序与算法默认相反（如 Modbus CRC 改为高字节在前）。</summary>
    public bool ChecksumSwap { get; set; }

    public int MaxFrameLength { get; set; } = 1024;
    /// <summary>半帧等待超时：两次数据间隔超过此值时丢弃未完成的帧（0 = 不丢弃）。</summary>
    public int PartialTimeoutMs { get; set; } = 1000;

    public List<FrameField> Fields { get; set; } = new();

    public FrameTemplate Clone()
    {
        var t = (FrameTemplate)MemberwiseClone();
        t.Fields = Fields.Select(f => f.Clone()).ToList();
        return t;
    }

    [JsonIgnore] public byte[] HeaderBytes => FrameHex.Parse(Header);
    [JsonIgnore] public byte[] TailBytes => FrameHex.Parse(Tail);
    [JsonIgnore] public int ChecksumLength => Features.Checksum.Compute(Checksum, new byte[] { 0 }).Length;

    /// <summary>检查模板是否可用。</summary>
    public string? Validate()
    {
        if (!FrameHex.TryParse(Header, out var h) || h.Length == 0) return "帧头必须是非空的 HEX，如 AA 55";
        if (!FrameHex.TryParse(Tail, out _)) return "帧尾 HEX 无效";
        if (HasLength)
        {
            if (LengthSize is not (1 or 2 or 4)) return "长度字段字节数只能是 1 / 2 / 4";
            if (LengthOffset < 0) return "长度字段偏移不能为负";
        }
        else if (FixedLength <= 0 && TailBytes.Length == 0)
        {
            return "需要长度字段、固定长度或帧尾三者之一来确定帧长";
        }
        if (MaxFrameLength < 4 || MaxFrameLength > 65536) return "最大帧长需为 4~65536";
        foreach (var f in Fields)
        {
            if (string.IsNullOrWhiteSpace(f.Name)) return "字段名不能为空";
            if (f.Offset < 0) return $"字段“{f.Name}”偏移不能为负";
        }
        return null;
    }

    /// <summary>内置示例：AA 55 | LEN | CMD | 温度 i16 ×0.1 | 湿度 u16 ×0.1 | SUM8（从 LEN 起算）。</summary>
    public static FrameTemplate Sample() => new()
    {
        Name = "示例：AA55 温湿度帧",
        Header = "AA 55",
        HasLength = true,
        LengthOffset = 2,
        LengthSize = 1,
        Checksum = ChecksumKind.Sum8,
        ChecksumFrom = 2,
        Fields =
        {
            new FrameField { Name = "命令", Offset = 3, Type = FrameFieldType.U8 },
            new FrameField { Name = "温度", Offset = 4, Type = FrameFieldType.I16, Scale = 0.1, Unit = "℃" },
            new FrameField { Name = "湿度", Offset = 6, Type = FrameFieldType.U16, Scale = 0.1, Unit = "%" },
        },
    };
}

public static class FrameHex
{
    public static bool TryParse(string? s, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        string clean = new string((s ?? "").Where(c => !char.IsWhiteSpace(c) && c != ',' && c != '-').ToArray());
        if (clean.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) clean = clean.Substring(2);
        clean = clean.Replace("0x", "").Replace("0X", "");
        if (clean.Length % 2 != 0) return false;
        var r = new byte[clean.Length / 2];
        for (int i = 0; i < r.Length; i++)
            if (!byte.TryParse(clean.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r[i])) return false;
        bytes = r;
        return true;
    }

    public static byte[] Parse(string? s) => TryParse(s, out var b) ? b : Array.Empty<byte>();
}

public sealed class FrameFieldValue
{
    public string Name { get; init; } = "";
    public string Value { get; init; } = "";
    public double? Number { get; init; }
    public string Hex { get; init; } = "";
}

public sealed class ParsedFrame
{
    public DateTime Time { get; init; }
    public bool IsTx { get; init; }
    public byte[] Raw { get; init; } = Array.Empty<byte>();
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public List<FrameFieldValue> Fields { get; init; } = new();

    public string Summary => string.Join("  ", Fields.Select(f => $"{f.Name}={f.Value}"));
}

/// <summary>按模板把字节流切成帧并解析字段。只在单线程使用。</summary>
public sealed class FrameParser
{
    private readonly FrameTemplate _t;
    private readonly byte[] _header;
    private readonly byte[] _tail;
    private readonly int _csLen;
    private readonly List<byte> _buf = new();
    private DateTime _lastTime;

    public FrameParser(FrameTemplate template)
    {
        _t = template.Clone();
        _header = _t.HeaderBytes;
        _tail = _t.TailBytes;
        _csLen = _t.ChecksumLength;
    }

    public FrameTemplate Template => _t;
    public long OkCount { get; private set; }
    public long BadCount { get; private set; }
    public long SkippedBytes { get; private set; }
    public int Pending => _buf.Count;

    public void Reset() => _buf.Clear();

    public List<ParsedFrame> Push(byte[] data, DateTime time, bool isTx = false)
    {
        var result = new List<ParsedFrame>();
        if (_buf.Count > 0 && _t.PartialTimeoutMs > 0 && (time - _lastTime).TotalMilliseconds > _t.PartialTimeoutMs)
        {
            SkippedBytes += _buf.Count;
            _buf.Clear();
        }
        _lastTime = time;
        _buf.AddRange(data);
        if (_header.Length == 0) return result;

        while (true)
        {
            int h = IndexOf(_buf, _header, 0);
            if (h < 0)
            {
                int keep = Math.Min(_buf.Count, _header.Length - 1);
                SkippedBytes += _buf.Count - keep;
                _buf.RemoveRange(0, _buf.Count - keep);
                break;
            }
            if (h > 0)
            {
                SkippedBytes += h;
                _buf.RemoveRange(0, h);
            }

            int total = DetermineLength(out bool needMore, out string? lenError);
            if (needMore) break;
            if (lenError != null)
            {
                result.Add(Bad(_buf.Take(Math.Min(_buf.Count, _header.Length + 4)).ToArray(), lenError, time, isTx));
                _buf.RemoveAt(0); // 重新同步
                continue;
            }
            if (_buf.Count < total) break;

            byte[] raw = _buf.GetRange(0, total).ToArray();
            if (_tail.Length > 0 && !EndsWith(raw, _tail))
            {
                result.Add(Bad(raw, "帧尾不匹配", time, isTx));
                _buf.RemoveAt(0); // 长度可能不可信：只丢一个字节再找帧头
                continue;
            }
            _buf.RemoveRange(0, total);
            result.Add(Decode(raw, time, isTx));
        }
        return result;
    }

    private ParsedFrame Bad(byte[] raw, string error, DateTime time, bool isTx)
    {
        BadCount++;
        return new ParsedFrame { Raw = raw, Ok = false, Error = error, Time = time, IsTx = isTx };
    }

    /// <summary>根据当前缓冲（帧头在 0）确定帧总长。</summary>
    private int DetermineLength(out bool needMore, out string? error)
    {
        needMore = false;
        error = null;
        int minLen = _header.Length + _csLen + _tail.Length;
        if (_t.HasLength)
        {
            int end = _t.LengthOffset + _t.LengthSize;
            if (_buf.Count < end) { needMore = true; return 0; }
            long v = 0;
            for (int i = 0; i < _t.LengthSize; i++)
            {
                int idx = _t.LengthBigEndian ? _t.LengthOffset + i : _t.LengthOffset + _t.LengthSize - 1 - i;
                v = (v << 8) | _buf[idx];
            }
            long total = v + _t.LengthAdjust
                         + (_t.LengthCountsHeader ? 0 : _header.Length)
                         + (_t.LengthCountsLengthField ? 0 : _t.LengthSize)
                         + (_t.LengthCountsChecksum ? 0 : _csLen)
                         + (_t.LengthCountsTail ? 0 : _tail.Length);
            if (!_t.LengthCountsHeader && !_t.LengthCountsLengthField)
                total += Math.Max(0, _t.LengthOffset - _header.Length); // 帧头与长度字段之间的字节（如地址）按“未计入”处理
            if (total < Math.Max(minLen, end) || total > _t.MaxFrameLength)
            {
                error = $"长度字段无效（值 {v}，推算帧长 {total}）";
                return 0;
            }
            return (int)total;
        }
        if (_t.FixedLength > 0)
        {
            return Math.Max(_t.FixedLength, minLen);
        }
        // 帧尾分隔
        int from = _header.Length;
        int pos = IndexOf(_buf, _tail, from + _csLen);
        if (pos < 0)
        {
            if (_buf.Count > _t.MaxFrameLength) { error = "超过最大帧长仍未找到帧尾"; return 0; }
            needMore = true;
            return 0;
        }
        return pos + _tail.Length;
    }

    private ParsedFrame Decode(byte[] raw, DateTime time, bool isTx)
    {
        int csPos = raw.Length - _tail.Length - _csLen;
        if (_csLen > 0)
        {
            int from = Math.Clamp(_t.ChecksumFrom, 0, csPos);
            var calc = Features.Checksum.Compute(_t.Checksum, raw.AsSpan(from, csPos - from).ToArray());
            if (_t.ChecksumSwap) Array.Reverse(calc);
            var got = raw.AsSpan(csPos, _csLen).ToArray();
            if (!calc.SequenceEqual(got))
            {
                BadCount++;
                return new ParsedFrame
                {
                    Raw = raw, Ok = false, Time = time, IsTx = isTx,
                    Error = $"校验错误：收到 {Features.Checksum.ToHex(got)}，应为 {Features.Checksum.ToHex(calc)}",
                    Fields = DecodeFields(raw),
                };
            }
        }
        OkCount++;
        return new ParsedFrame { Raw = raw, Ok = true, Time = time, IsTx = isTx, Fields = DecodeFields(raw) };
    }

    private List<FrameFieldValue> DecodeFields(byte[] raw)
    {
        var list = new List<FrameFieldValue>();
        foreach (var f in _t.Fields)
        {
            if (f.Offset + f.Size > raw.Length)
            {
                list.Add(new FrameFieldValue { Name = f.Name, Value = "（越界）" });
                continue;
            }
            list.Add(DecodeField(f, raw));
        }
        return list;
    }

    public static FrameFieldValue DecodeField(FrameField f, byte[] raw)
    {
        var span = raw.AsSpan(f.Offset, f.Size);
        string hex = Features.Checksum.ToHex(span.ToArray());
        if (f.Type == FrameFieldType.Bytes) return new FrameFieldValue { Name = f.Name, Value = hex, Hex = hex };
        ulong u = 0;
        for (int i = 0; i < f.Size; i++)
            u = (u << 8) | (f.BigEndian ? span[i] : span[f.Size - 1 - i]);
        double v = f.Type switch
        {
            FrameFieldType.U8 => (double)(byte)u,
            FrameFieldType.I8 => (double)(sbyte)(byte)u,
            FrameFieldType.U16 => (double)(ushort)u,
            FrameFieldType.I16 => (double)(short)(ushort)u,
            FrameFieldType.U32 => (double)(uint)u,
            FrameFieldType.I32 => (double)(int)(uint)u,
            _ => (double)BitConverter.Int32BitsToSingle((int)(uint)u),
        };
        double scaled = v * (f.Scale == 0 ? 1 : f.Scale);
        bool integral = f.Type != FrameFieldType.Float && (f.Scale == 1 || f.Scale == 0);
        string text = integral
            ? ((long)scaled).ToString(CultureInfo.InvariantCulture)
            : Math.Round(scaled, 6).ToString("0.######", CultureInfo.InvariantCulture);
        if (!string.IsNullOrEmpty(f.Unit)) text += " " + f.Unit;
        return new FrameFieldValue { Name = f.Name, Value = text, Number = scaled, Hex = hex };
    }

    private static int IndexOf(List<byte> buf, byte[] pat, int from)
    {
        if (pat.Length == 0) return -1;
        for (int i = Math.Max(0, from); i + pat.Length <= buf.Count; i++)
        {
            bool ok = true;
            for (int k = 0; k < pat.Length; k++)
                if (buf[i + k] != pat[k]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }

    private static bool EndsWith(byte[] raw, byte[] tail)
    {
        if (raw.Length < tail.Length) return false;
        for (int k = 0; k < tail.Length; k++)
            if (raw[raw.Length - tail.Length + k] != tail[k]) return false;
        return true;
    }
}

/// <summary>模板文件：设置目录下的 frame_templates.json。</summary>
public static class FrameTemplateStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string PathIn(string directory) => System.IO.Path.Combine(directory, "frame_templates.json");

    public static List<FrameTemplate> Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var list = JsonSerializer.Deserialize<List<FrameTemplate>>(File.ReadAllText(path, Encoding.UTF8), Options);
                if (list != null)
                {
                    list.RemoveAll(t => t == null);
                    foreach (var t in list)
                    {
                        t.Fields ??= new List<FrameField>();
                        t.Fields.RemoveAll(f => f == null);
                        t.Name ??= "模板";
                        t.Header ??= "";
                        t.Tail ??= "";
                    }
                    if (list.Count > 0) return list;
                }
            }
        }
        catch
        {
            // 文件损坏：回退到示例
        }
        return new List<FrameTemplate> { FrameTemplate.Sample() };
    }

    public static void Save(string path, List<FrameTemplate> templates)
    {
        string? dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(templates, Options), new UTF8Encoding(false));
        File.Move(tmp, path, true);
    }

    public static string Serialize(List<FrameTemplate> t) => JsonSerializer.Serialize(t, Options);
    public static List<FrameTemplate>? Deserialize(string json) => JsonSerializer.Deserialize<List<FrameTemplate>>(json, Options);
}
