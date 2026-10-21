using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SerialTool.Features;

/// <summary>一条“行”记录：时间、方向、原始字节、解码文本（不含行尾）。</summary>
public sealed class LineRecord
{
    public DateTime Time { get; init; }
    public bool IsTx { get; init; }
    public string? Source { get; init; }
    public byte[] Bytes { get; init; } = Array.Empty<byte>();
    public string Text { get; init; } = "";
}

/// <summary>
/// 把字节流按 \n 拆成行（同时保留原始字节），超长行强制截断；
/// 没有换行的数据在间隔超过 gapMs 后由 Flush 输出（适合 HEX 协议、无换行的数据）。
/// </summary>
public sealed class LineAssembler
{
    private readonly List<byte> _pending = new();
    private DateTime _firstTime, _lastTime;
    private string? _source;
    private Encoding _encoding;

    public LineAssembler(Encoding encoding, bool isTx = false, int maxLineBytes = 4096)
    {
        _encoding = encoding;
        IsTx = isTx;
        MaxLineBytes = maxLineBytes;
    }

    public bool IsTx { get; }
    public int MaxLineBytes { get; }

    public Encoding Encoding
    {
        get => _encoding;
        set => _encoding = value;
    }

    public void Reset() => _pending.Clear();

    public IEnumerable<LineRecord> Push(byte[] data, DateTime time, string? source = null)
    {
        var result = new List<LineRecord>();
        if (_pending.Count > 0 && !string.Equals(source, _source, StringComparison.Ordinal))
            result.Add(Take());
        foreach (byte b in data)
        {
            if (_pending.Count == 0)
            {
                _firstTime = time;
                _source = source;
            }
            _pending.Add(b);
            if (b == (byte)'\n' || _pending.Count >= MaxLineBytes)
                result.Add(Take());
        }
        _lastTime = time;
        return result;
    }

    /// <summary>未结束的行在静默 gapMs 后输出。</summary>
    public LineRecord? Flush(DateTime now, int gapMs)
    {
        if (_pending.Count == 0) return null;
        if ((now - _lastTime).TotalMilliseconds < gapMs) return null;
        return Take();
    }

    public LineRecord? FlushAll() => _pending.Count == 0 ? null : Take();

    private LineRecord Take()
    {
        byte[] bytes = _pending.ToArray();
        _pending.Clear();
        string text = _encoding.GetString(bytes).TrimEnd('\r', '\n');
        return new LineRecord { Time = _firstTime, IsTx = IsTx, Source = _source, Bytes = bytes, Text = text };
    }
}

/// <summary>CSV 导出（UTF-8 BOM，Excel 可直接打开中文）。</summary>
public static class CsvExport
{
    public static string Escape(string s)
    {
        if (s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>不可见控制字符用 \xHH 表示，避免 CSV 单元格内出现乱码。</summary>
    public static string CleanText(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char ch in s)
        {
            if (ch == '\t') sb.Append("\\t");
            else if (ch == '\r') sb.Append("\\r");
            else if (ch == '\n') sb.Append("\\n");
            else if (ch < 0x20 || ch == 0x7F) sb.Append("\\x").Append(((int)ch).ToString("X2"));
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    public static void WriteLines(TextWriter w, IEnumerable<LineRecord> records)
    {
        w.WriteLine("时间,方向,来源,字节数,HEX,文本");
        foreach (var r in records)
        {
            w.Write(r.Time.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
            w.Write(',');
            w.Write(r.IsTx ? "发送" : "接收");
            w.Write(',');
            w.Write(Escape(r.Source ?? ""));
            w.Write(',');
            w.Write(r.Bytes.Length.ToString(CultureInfo.InvariantCulture));
            w.Write(',');
            w.Write(Checksum.ToHex(r.Bytes));
            w.Write(',');
            w.WriteLine(Escape(CleanText(r.Text)));
        }
    }
}

/// <summary>
/// 从文本行中解析数值通道：
/// 1) 有 “名称=值 / 名称:值” 时按名称（如 temp=23.5,hum=40 → temp、hum）；
/// 2) 否则把行中所有数字按顺序作为 CH1、CH2…（如 “1.2, 3.4 5”）。
/// </summary>
public static class NumericLineParser
{
    public const int MaxChannels = 16;

    private static readonly Regex Named = new(
        @"(?<![\w.])([A-Za-z_\u4e00-\u9fa5][\w\u4e00-\u9fa5.\-]*)\s*[=:：]\s*([-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Number = new(
        @"(?<![\w.])[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?(?![\w.])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static List<(string Name, double Value)> Parse(string line)
    {
        var result = new List<(string, double)>();
        if (string.IsNullOrWhiteSpace(line)) return result;
        if (line.Length > 2000) line = line.Substring(0, 2000);

        foreach (Match m in Named.Matches(line))
        {
            if (!double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) continue;
            string name = m.Groups[1].Value;
            if (result.Any(r => r.Item1 == name)) continue;
            result.Add((name, v));
            if (result.Count >= MaxChannels) break;
        }
        if (result.Count > 0) return result;

        int idx = 0;
        foreach (Match m in Number.Matches(line))
        {
            if (!double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) continue;
            idx++;
            result.Add(("CH" + idx, v));
            if (result.Count >= MaxChannels) break;
        }
        return result;
    }
}

/// <summary>接收区查找 / 过滤条件。</summary>
public sealed class LineFilter
{
    private readonly Regex? _regex;
    private readonly string _literal;
    private readonly StringComparison _cmp;

    private LineFilter(string pattern, Regex? regex, bool ignoreCase)
    {
        _literal = pattern;
        _regex = regex;
        _cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }

    public string Pattern => _literal;

    public static LineFilter? Create(string pattern, bool isRegex, bool ignoreCase, out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(pattern)) return null;
        if (!isRegex) return new LineFilter(pattern, null, ignoreCase);
        try
        {
            var opts = RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
            var re = new Regex(pattern, opts, TimeSpan.FromMilliseconds(50));
            re.IsMatch(""); // 触发编译
            return new LineFilter(pattern, re, ignoreCase);
        }
        catch (ArgumentException ex)
        {
            error = "正则无效：" + ex.Message;
            return null;
        }
    }

    public bool IsMatch(string text)
    {
        if (_regex == null) return text.IndexOf(_literal, _cmp) >= 0;
        try { return _regex.IsMatch(text); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    /// <summary>过滤：include=true 只保留匹配行；false 隐藏匹配行。</summary>
    public bool Passes(string text, bool include) => IsMatch(text) == include;

    public List<(int Start, int Length)> FindAll(string text, int max = 1000)
    {
        var list = new List<(int, int)>();
        if (_regex == null)
        {
            int i = 0;
            while (list.Count < max && (i = text.IndexOf(_literal, i, _cmp)) >= 0)
            {
                list.Add((i, _literal.Length));
                i += Math.Max(1, _literal.Length);
            }
            return list;
        }
        try
        {
            for (var m = _regex.Match(text); m.Success && list.Count < max; m = m.NextMatch())
                if (m.Length > 0) list.Add((m.Index, m.Length));
        }
        catch (RegexMatchTimeoutException) { /* ignore */ }
        return list;
    }

    /// <summary>从 start 起（或反向）查找下一个匹配，循环。返回 (-1,0) 表示没有。</summary>
    public (int Start, int Length) FindNext(string text, int start, bool backward)
    {
        var all = FindAll(text, 200_000);
        if (all.Count == 0) return (-1, 0);
        if (!backward)
        {
            foreach (var m in all) if (m.Start >= start) return m;
            return all[0];
        }
        for (int k = all.Count - 1; k >= 0; k--) if (all[k].Start < start) return all[k];
        return all[^1];
    }
}

/// <summary>数值曲线数据：多个命名通道，按行（样本序号）对齐。</summary>
public sealed class CurveData
{
    public const int MaxRows = 100_000;

    public sealed class Row
    {
        public DateTime Time { get; init; }
        public double[] Values { get; init; } = Array.Empty<double>(); // 按通道序号，NaN = 该行没有此通道
    }

    private readonly List<string> _names = new();
    private readonly List<Row> _rows = new();

    public IReadOnlyList<string> Channels => _names;
    public IReadOnlyList<Row> Rows => _rows;
    public long TotalRows { get; private set; }
    public bool Paused { get; set; }
    public int Version { get; private set; }

    public void Clear()
    {
        _names.Clear();
        _rows.Clear();
        TotalRows = 0;
        Version++;
    }

    /// <summary>加入一行解析结果，返回是否加入。</summary>
    public bool Add(DateTime time, IReadOnlyList<(string Name, double Value)> values)
    {
        if (Paused || values.Count == 0) return false;
        foreach (var (name, _) in values)
            if (!_names.Contains(name) && _names.Count < NumericLineParser.MaxChannels) _names.Add(name);
        var arr = new double[_names.Count];
        for (int i = 0; i < arr.Length; i++) arr[i] = double.NaN;
        foreach (var (name, v) in values)
        {
            int idx = _names.IndexOf(name);
            if (idx >= 0) arr[idx] = v;
        }
        _rows.Add(new Row { Time = time, Values = arr });
        if (_rows.Count > MaxRows) _rows.RemoveRange(0, _rows.Count - MaxRows + 10_000);
        TotalRows++;
        Version++;
        return true;
    }

    public static double ValueAt(Row r, int channel) => channel < r.Values.Length ? r.Values[channel] : double.NaN;

    public void WriteCsv(TextWriter w)
    {
        w.Write("时间");
        foreach (var n in _names) w.Write("," + CsvExport.Escape(n));
        w.WriteLine();
        foreach (var r in _rows)
        {
            w.Write(r.Time.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
            for (int i = 0; i < _names.Count; i++)
            {
                double v = ValueAt(r, i);
                w.Write(',');
                if (!double.IsNaN(v)) w.Write(v.ToString("R", CultureInfo.InvariantCulture));
            }
            w.WriteLine();
        }
    }
}
