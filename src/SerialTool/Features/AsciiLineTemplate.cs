using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SerialTool.Features;

public enum AsciiSplitMode
{
    /// <summary>按分隔符切分列（逗号 / 空格 / 制表符 / 自定义）。</summary>
    Delimiter,
    /// <summary>正则捕获组：组 1、2、… 对应字段下标。</summary>
    Regex,
    /// <summary>固定列宽（每个字段 Start + Length）。</summary>
    FixedWidth,
    /// <summary>key=value 或 key:value 对。</summary>
    KeyValue,
}

public enum AsciiFieldType { Text, Int, Float, Hex }

/// <summary>ASCII 行模板中的一个字段。</summary>
public sealed class AsciiField
{
    public string Name { get; set; } = "";
    public AsciiFieldType Type { get; set; } = AsciiFieldType.Text;
    /// <summary>Delimiter / Regex：第几列或第几个捕获组（从 0 起）。KeyValue 时忽略。</summary>
    public int Index { get; set; }
    /// <summary>KeyValue 模式的键名。</summary>
    public string Key { get; set; } = "";
    /// <summary>FixedWidth：起始字符下标（0 起）。</summary>
    public int Start { get; set; }
    /// <summary>FixedWidth：字符长度。</summary>
    public int Length { get; set; } = 1;
    public double Scale { get; set; } = 1.0;
    public string Unit { get; set; } = "";

    public AsciiField Clone() => (AsciiField)MemberwiseClone();
}

/// <summary>
/// ASCII / 文本行协议模板：按行（\r\n / \n / \r）切分记录。
/// 帧头 / 帧尾可选（前缀 / 后缀过滤），不要求帧长度。
/// </summary>
public sealed class AsciiLineTemplate
{
    public string Name { get; set; } = "新 ASCII 模板";
    /// <summary>行必须以此外缀开头才解析（空 = 不限制）。</summary>
    public string Prefix { get; set; } = "";
    /// <summary>行必须以此外缀结尾才解析（空 = 不限制；不含换行符）。</summary>
    public string Suffix { get; set; } = "";
    public AsciiSplitMode Mode { get; set; } = AsciiSplitMode.Delimiter;
    /// <summary>Delimiter：分隔串（"," / " " / "\t" / 自定义）；Regex：正则表达式。</summary>
    public string Delimiter { get; set; } = ",";
    public bool TrimParts { get; set; } = true;
    /// <summary>KeyValue：键与值之间的分隔（"=" 或 ":"）。</summary>
    public string KeyValueSep { get; set; } = "=";
    /// <summary>KeyValue：多对之间的分隔。</summary>
    public string KeyValuePairSep { get; set; } = ",";
    /// <summary>空 = 跟随会话编码。</summary>
    public string Encoding { get; set; } = "";
    /// <summary>忽略空行。</summary>
    public bool SkipEmpty { get; set; } = true;
    /// <summary>半行超时：超过此间隔丢弃未完成的行缓冲（0 = 不丢弃）。</summary>
    public int PartialTimeoutMs { get; set; } = 1000;
    public List<AsciiField> Fields { get; set; } = new();

    public AsciiLineTemplate Clone()
    {
        var t = (AsciiLineTemplate)MemberwiseClone();
        t.Fields = Fields.Select(f => f.Clone()).ToList();
        return t;
    }

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)) return "模板名不能为空";
        Prefix ??= "";
        Suffix ??= "";
        Delimiter ??= "";
        KeyValueSep ??= "=";
        KeyValuePairSep ??= ",";
        Encoding ??= "";
        Fields ??= new List<AsciiField>();
        Fields.RemoveAll(f => f == null);
        if (Mode == AsciiSplitMode.Delimiter && Delimiter.Length == 0)
            return "分隔符不能为空（可用空格或 \\t）";
        if (Mode == AsciiSplitMode.Regex)
        {
            if (string.IsNullOrWhiteSpace(Delimiter)) return "正则不能为空";
            try { _ = new Regex(Delimiter, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)); }
            catch (Exception ex) { return "正则无效：" + ex.Message; }
        }
        if (Mode == AsciiSplitMode.KeyValue && KeyValueSep.Length == 0) return "键值分隔符不能为空";
        foreach (var f in Fields)
        {
            if (string.IsNullOrWhiteSpace(f.Name)) return "字段名不能为空";
            if (Mode == AsciiSplitMode.KeyValue && string.IsNullOrWhiteSpace(f.Key))
                return $"字段“{f.Name}”缺少键名";
            if (Mode == AsciiSplitMode.FixedWidth && f.Length < 1)
                return $"字段“{f.Name}”的固定宽度至少为 1";
            if (Mode == AsciiSplitMode.FixedWidth && f.Start < 0)
                return $"字段“{f.Name}”的起始位置不能为负";
            if (f.Index < 0) return $"字段“{f.Name}”的列下标不能为负";
        }
        return null;
    }

    /// <summary>示例：temp=23.5,hum=40</summary>
    public static AsciiLineTemplate SampleKeyValue() => new()
    {
        Name = "示例：温湿度 key=value",
        Mode = AsciiSplitMode.KeyValue,
        KeyValueSep = "=",
        KeyValuePairSep = ",",
        Fields =
        {
            new AsciiField { Name = "温度", Key = "temp", Type = AsciiFieldType.Float, Unit = "℃" },
            new AsciiField { Name = "湿度", Key = "hum", Type = AsciiFieldType.Float, Unit = "%" },
        },
    };

    /// <summary>示例：CSV 三列 id,value,status</summary>
    public static AsciiLineTemplate SampleCsv() => new()
    {
        Name = "示例：CSV 三列",
        Mode = AsciiSplitMode.Delimiter,
        Delimiter = ",",
        Fields =
        {
            new AsciiField { Name = "编号", Index = 0, Type = AsciiFieldType.Int },
            new AsciiField { Name = "数值", Index = 1, Type = AsciiFieldType.Float },
            new AsciiField { Name = "状态", Index = 2, Type = AsciiFieldType.Text },
        },
    };

    public static List<AsciiLineTemplate> Defaults() => new() { SampleKeyValue(), SampleCsv() };
}

public sealed class AsciiFieldValue
{
    public string Name { get; init; } = "";
    public string Value { get; init; } = "";
    public string Raw { get; init; } = "";
}

public sealed class ParsedAsciiLine
{
    public DateTime Time { get; init; }
    public bool IsTx { get; init; }
    public string Line { get; init; } = "";
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public List<AsciiFieldValue> Fields { get; init; } = new();
    public string Summary => string.Join("  ", Fields.Select(f => $"{f.Name}={f.Value}"));
}

/// <summary>按行切分字节流并按 ASCII 模板解析字段。单线程使用。</summary>
public sealed class AsciiLineParser
{
    private readonly AsciiLineTemplate _t;
    private readonly List<byte> _buf = new();
    private DateTime _lastTime;
    private Regex? _regex;
    private Encoding _enc = new UTF8Encoding(false);

    public AsciiLineParser(AsciiLineTemplate template)
    {
        _t = template.Clone();
        if (_t.Mode == AsciiSplitMode.Regex && !string.IsNullOrEmpty(_t.Delimiter))
            _regex = new Regex(_t.Delimiter, RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    }

    public AsciiLineTemplate Template => _t;
    public long OkCount { get; private set; }
    public long BadCount { get; private set; }
    public long SkippedLines { get; private set; }
    public int PendingBytes => _buf.Count;

    public void Reset() => _buf.Clear();

    /// <summary>喂入数据。encoding 为会话编码；模板 Encoding 非空时优先用模板的。</summary>
    public List<ParsedAsciiLine> Push(byte[] data, DateTime time, bool isTx, Encoding encoding)
    {
        var result = new List<ParsedAsciiLine>();
        if (_buf.Count > 0 && _t.PartialTimeoutMs > 0 && (time - _lastTime).TotalMilliseconds > _t.PartialTimeoutMs)
            _buf.Clear();
        _lastTime = time;
        if (data.Length == 0) return result;
        _buf.AddRange(data);
        _enc = ResolveEncoding(encoding);

        while (TryTakeLine(out string line))
        {
            if (_t.SkipEmpty && string.IsNullOrWhiteSpace(line)) continue;
            if (_t.Prefix.Length > 0 && !line.StartsWith(_t.Prefix, StringComparison.Ordinal))
            {
                SkippedLines++;
                continue;
            }
            if (_t.Suffix.Length > 0 && !line.EndsWith(_t.Suffix, StringComparison.Ordinal))
            {
                SkippedLines++;
                continue;
            }
            string body = line;
            if (_t.Prefix.Length > 0) body = body.Substring(_t.Prefix.Length);
            if (_t.Suffix.Length > 0) body = body.Substring(0, body.Length - _t.Suffix.Length);
            result.Add(ParseLine(line, body, time, isTx));
        }
        return result;
    }

    private Encoding ResolveEncoding(Encoding session)
    {
        if (string.IsNullOrWhiteSpace(_t.Encoding)) return session;
        try { return EncodingUtil.Get(_t.Encoding); }
        catch { return session; }
    }

    /// <summary>Take one complete line from the byte buffer (without EOL).</summary>
    private bool TryTakeLine(out string line)
    {
        line = "";
        int n = _buf.Count;
        if (n == 0) return false;
        for (int i = 0; i < n; i++)
        {
            byte b = _buf[i];
            if (b != 10 && b != 13) continue; // LF / CR
            // Lone CR at end of buffer: wait for possible LF
            if (b == 13 && i + 1 == n) return false;
            int end = i;
            int consume = i + 1;
            if (b == 13 && _buf[i + 1] == 10) consume = i + 2;
            line = _enc.GetString(_buf.GetRange(0, end).ToArray());
            _buf.RemoveRange(0, consume);
            return true;
        }
        if (_buf.Count > 1 << 20) _buf.RemoveRange(0, _buf.Count - 4096);
        return false;
    }

    private ParsedAsciiLine ParseLine(string rawLine, string body, DateTime time, bool isTx)
    {
        try
        {
            string[] parts = Split(body);
            var values = new List<AsciiFieldValue>();
            foreach (var f in _t.Fields)
            {
                string piece = Extract(f, body, parts);
                if (_t.TrimParts) piece = piece.Trim();
                values.Add(new AsciiFieldValue { Name = f.Name, Raw = piece, Value = Format(f, piece) });
            }
            OkCount++;
            return new ParsedAsciiLine { Time = time, IsTx = isTx, Line = rawLine, Ok = true, Fields = values };
        }
        catch (Exception ex)
        {
            BadCount++;
            return new ParsedAsciiLine { Time = time, IsTx = isTx, Line = rawLine, Ok = false, Error = ex.Message };
        }
    }

    private string[] Split(string body)
    {
        switch (_t.Mode)
        {
            case AsciiSplitMode.Delimiter:
            {
                string d = UnescapeDelim(_t.Delimiter);
                if (d == " ")
                    return body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                return body.Split(new[] { d }, StringSplitOptions.None);
            }
            case AsciiSplitMode.Regex:
            {
                var m = _regex!.Match(body);
                if (!m.Success) throw new FormatException("正则未匹配");
                var list = new List<string>();
                for (int i = 1; i < m.Groups.Count; i++) list.Add(m.Groups[i].Value);
                return list.ToArray();
            }
            case AsciiSplitMode.FixedWidth:
                return Array.Empty<string>(); // 按字段 Start/Length 取
            case AsciiSplitMode.KeyValue:
                return Array.Empty<string>();
            default:
                return Array.Empty<string>();
        }
    }

    private string Extract(AsciiField f, string body, string[] parts)
    {
        switch (_t.Mode)
        {
            case AsciiSplitMode.Delimiter:
            case AsciiSplitMode.Regex:
                if (f.Index < 0 || f.Index >= parts.Length) throw new FormatException($"缺少第 {f.Index + 1} 列");
                return parts[f.Index];
            case AsciiSplitMode.FixedWidth:
                if (f.Start >= body.Length) return "";
                int len = Math.Min(f.Length, body.Length - f.Start);
                return body.Substring(f.Start, len);
            case AsciiSplitMode.KeyValue:
            {
                string pairSep = UnescapeDelim(_t.KeyValuePairSep);
                string kvSep = UnescapeDelim(_t.KeyValueSep);
                var pairs = body.Split(new[] { pairSep }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var p in pairs)
                {
                    string s = _t.TrimParts ? p.Trim() : p;
                    int eq = s.IndexOf(kvSep, StringComparison.Ordinal);
                    if (eq <= 0) continue;
                    string key = s.Substring(0, eq);
                    string val = s.Substring(eq + kvSep.Length);
                    if (_t.TrimParts) { key = key.Trim(); val = val.Trim(); }
                    if (string.Equals(key, f.Key, StringComparison.OrdinalIgnoreCase)) return val;
                }
                throw new FormatException($"缺少键 “{f.Key}”");
            }
            default:
                return "";
        }
    }

    private static string Format(AsciiField f, string piece)
    {
        switch (f.Type)
        {
            case AsciiFieldType.Text:
                return string.IsNullOrEmpty(f.Unit) ? piece : piece + " " + f.Unit;
            case AsciiFieldType.Int:
            {
                if (!long.TryParse(piece.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v))
                    throw new FormatException($"“{f.Name}”不是整数：{piece}");
                double scaled = v * f.Scale;
                string text = Math.Abs(f.Scale - 1) < 1e-12 ? v.ToString(CultureInfo.InvariantCulture)
                    : scaled.ToString("0.###", CultureInfo.InvariantCulture);
                return string.IsNullOrEmpty(f.Unit) ? text : text + " " + f.Unit;
            }
            case AsciiFieldType.Float:
            {
                if (!double.TryParse(piece.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                    throw new FormatException($"“{f.Name}”不是数字：{piece}");
                v *= f.Scale;
                string text = v.ToString("0.###", CultureInfo.InvariantCulture);
                return string.IsNullOrEmpty(f.Unit) ? text : text + " " + f.Unit;
            }
            case AsciiFieldType.Hex:
            {
                string hex = piece.Trim().Replace(" ", "").Replace("0x", "", StringComparison.OrdinalIgnoreCase);
                if (hex.Length == 0 || hex.Length % 2 != 0) throw new FormatException($"“{f.Name}”HEX 无效：{piece}");
                for (int i = 0; i < hex.Length; i++)
                    if (!IsHex(hex[i])) throw new FormatException($"“{f.Name}”HEX 无效：{piece}");
                var spaced = new StringBuilder();
                for (int i = 0; i < hex.Length; i += 2)
                {
                    if (i > 0) spaced.Append(' ');
                    spaced.Append(char.ToUpperInvariant(hex[i]));
                    spaced.Append(char.ToUpperInvariant(hex[i + 1]));
                }
                return spaced.ToString();
            }
            default:
                return piece;
        }
    }

    private static bool IsHex(char c) => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');

    private static string UnescapeDelim(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return s.Replace("\\t", "\t", StringComparison.Ordinal)
                .Replace("\\r", "\r", StringComparison.Ordinal)
                .Replace("\\n", "\n", StringComparison.Ordinal)
                .Replace("\\\\", "\\", StringComparison.Ordinal);
    }
}

public static class AsciiLineTemplateStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string PathIn(string directory) => Path.Combine(directory, "ascii_line_templates.json");

    public static List<AsciiLineTemplate> Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var list = JsonSerializer.Deserialize<List<AsciiLineTemplate>>(File.ReadAllText(path, Encoding.UTF8), Options);
                if (list != null)
                {
                    list.RemoveAll(t => t == null);
                    foreach (var t in list)
                    {
                        t.Fields ??= new List<AsciiField>();
                        t.Fields.RemoveAll(f => f == null);
                        t.Name ??= "模板";
                        t.Prefix ??= "";
                        t.Suffix ??= "";
                        t.Delimiter ??= ",";
                        t.KeyValueSep ??= "=";
                        t.KeyValuePairSep ??= ",";
                        t.Encoding ??= "";
                    }
                    if (list.Count > 0) return list;
                }
            }
        }
        catch { /* use defaults */ }
        return AsciiLineTemplate.Defaults();
    }

    public static void Save(string path, List<AsciiLineTemplate> list)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list, Options), new UTF8Encoding(false));
        File.Move(tmp, path, true);
    }
}
