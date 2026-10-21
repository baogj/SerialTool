using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SerialTool.Features;

/// <summary>关键字高亮 + 自动回复规则（可序列化保存到 settings.json）。</summary>
public sealed class HighlightRule
{
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "";
    /// <summary>匹配内容：文本 / 正则 / HEX（如 "AA 55"）。</summary>
    public string Pattern { get; set; } = "";
    public bool IsRegex { get; set; }
    public bool IsHex { get; set; }
    public bool IgnoreCase { get; set; }
    /// <summary>高亮背景色，#RRGGBB。</summary>
    public string Color { get; set; } = "#FDE047";

    public bool ReplyEnabled { get; set; }
    public string ReplyContent { get; set; } = "";
    public bool ReplyIsHex { get; set; }
    public int ReplyDelayMs { get; set; }
    /// <summary>同一规则两次自动回复之间的最短间隔（防止互相回复死循环）。</summary>
    public int CooldownMs { get; set; } = 1000;

    public HighlightRule Clone() => (HighlightRule)MemberwiseClone();

    [JsonIgnore] public bool IsUsable => Enabled && !string.IsNullOrEmpty(Pattern);
}

public readonly record struct HighlightSpan(int Start, int Length, int RuleIndex);

/// <summary>编译好的规则：用于显示文本高亮与 RX 字节匹配。不依赖 WPF，便于单元测试。</summary>
public sealed class CompiledRule
{
    public HighlightRule Rule { get; }
    public int Index { get; }
    public Regex? DisplayRegex { get; }   // 在显示文本中查找
    public string? DisplayLiteral { get; }
    public byte[]? BytePattern { get; }    // 文本/HEX 字面量：按字节匹配 RX
    public Regex? ByteRegex { get; }       // 正则：按解码后的文本匹配 RX
    public string? Error { get; }

    public CompiledRule(HighlightRule rule, int index, Encoding encoding)
    {
        Rule = rule;
        Index = index;
        var opts = RegexOptions.CultureInvariant | (rule.IgnoreCase ? RegexOptions.IgnoreCase : 0);
        try
        {
            if (rule.IsHex)
            {
                if (!HexUtilLite.TryParse(rule.Pattern, out var bytes) || bytes.Length == 0)
                {
                    Error = "HEX 格式错误";
                    return;
                }
                BytePattern = bytes;
                // HEX 显示模式下的文本形式 "AA 55 01"
                DisplayLiteral = Checksum.ToHex(bytes);
                DisplayRegex = new Regex(Regex.Escape(DisplayLiteral), RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            }
            else if (rule.IsRegex)
            {
                DisplayRegex = new Regex(rule.Pattern, opts, TimeSpan.FromMilliseconds(50));
                ByteRegex = DisplayRegex;
            }
            else
            {
                DisplayLiteral = rule.Pattern;
                DisplayRegex = new Regex(Regex.Escape(rule.Pattern), opts);
                if (!rule.IgnoreCase) BytePattern = encoding.GetBytes(rule.Pattern);
                else ByteRegex = DisplayRegex; // 忽略大小写时走文本匹配
            }
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            DisplayRegex = null;
            ByteRegex = null;
            BytePattern = null;
        }
    }

    public bool IsValid => Error == null && (DisplayRegex != null || BytePattern != null);
}

public static class HighlightMatcher
{
    /// <summary>在一行显示文本中找出所有高亮区间（不重叠，先出现的规则优先）。</summary>
    public static List<HighlightSpan> Find(string text, IReadOnlyList<CompiledRule> rules)
    {
        var spans = new List<HighlightSpan>();
        if (string.IsNullOrEmpty(text) || rules.Count == 0) return spans;
        foreach (var r in rules)
        {
            if (r.DisplayRegex == null) continue;
            MatchCollection ms;
            try { ms = r.DisplayRegex.Matches(text); }
            catch (RegexMatchTimeoutException) { continue; }
            foreach (Match m in ms)
            {
                if (m.Length == 0) continue;
                if (Overlaps(spans, m.Index, m.Length)) continue;
                spans.Add(new HighlightSpan(m.Index, m.Length, r.Index));
            }
        }
        spans.Sort((a, b) => a.Start.CompareTo(b.Start));
        return spans;
    }

    private static bool Overlaps(List<HighlightSpan> spans, int start, int len)
    {
        int end = start + len;
        foreach (var s in spans)
            if (start < s.Start + s.Length && s.Start < end) return true;
        return false;
    }
}

/// <summary>
/// 自动回复匹配：维护一个滚动 RX 字节窗口，每条规则记住已扫描到的位置，
/// 同一段数据只会触发一次；另有每条规则冷却时间 + 全局速率限制，防止回环。
/// </summary>
public sealed class AutoReplyEngine
{
    private const int WindowBytes = 8192;
    private readonly List<byte> _buf = new(WindowBytes);
    private long _base;                    // _buf[0] 的绝对位置
    private long[] _scanFrom = Array.Empty<long>();
    private DateTime[] _lastFired = Array.Empty<DateTime>();
    private IReadOnlyList<CompiledRule> _rules = Array.Empty<CompiledRule>();
    private readonly Queue<DateTime> _recent = new();

    public Encoding Encoding { get; set; } = new UTF8Encoding(false);
    /// <summary>全局限制：每秒最多自动回复次数。</summary>
    public int MaxRepliesPerSecond { get; set; } = 20;
    public long Suppressed { get; private set; }

    public void SetRules(IReadOnlyList<CompiledRule> rules)
    {
        _rules = rules;
        long end = _base + _buf.Count;
        _scanFrom = new long[rules.Count];
        for (int i = 0; i < _scanFrom.Length; i++) _scanFrom[i] = end; // 只匹配之后到达的数据
        _lastFired = new DateTime[rules.Count];
    }

    public void Reset()
    {
        _buf.Clear();
        _base = 0;
        for (int i = 0; i < _scanFrom.Length; i++) _scanFrom[i] = 0;
        _recent.Clear();
    }

    /// <summary>送入新收到的 RX 字节，返回本次应触发回复的规则。</summary>
    public List<CompiledRule> Push(byte[] data, DateTime now)
    {
        var fired = new List<CompiledRule>();
        if (data.Length == 0) return fired;
        _buf.AddRange(data);
        if (_buf.Count > WindowBytes)
        {
            int drop = _buf.Count - WindowBytes;
            _buf.RemoveRange(0, drop);
            _base += drop;
        }

        for (int i = 0; i < _rules.Count; i++)
        {
            var r = _rules[i];
            if (!r.Rule.IsUsable || !r.Rule.ReplyEnabled || !r.IsValid) continue;
            if (_scanFrom[i] < _base) _scanFrom[i] = _base;
            int from = (int)(_scanFrom[i] - _base);
            int matchEnd = -1;

            if (r.BytePattern != null)
            {
                int idx = IndexOf(_buf, r.BytePattern, from);
                if (idx >= 0) matchEnd = idx + r.BytePattern.Length;
            }
            else if (r.ByteRegex != null)
            {
                var slice = _buf.GetRange(from, _buf.Count - from).ToArray();
                string text = Encoding.GetString(slice);
                try
                {
                    if (r.ByteRegex.IsMatch(text)) matchEnd = _buf.Count; // 已匹配部分全部视为消费
                }
                catch (RegexMatchTimeoutException) { }
            }

            if (matchEnd < 0) continue;
            _scanFrom[i] = _base + matchEnd;

            if ((now - _lastFired[i]).TotalMilliseconds < Math.Max(0, r.Rule.CooldownMs))
            {
                Suppressed++;
                continue;
            }
            while (_recent.Count > 0 && (now - _recent.Peek()).TotalMilliseconds > 1000) _recent.Dequeue();
            if (_recent.Count >= MaxRepliesPerSecond)
            {
                Suppressed++;
                continue;
            }
            _recent.Enqueue(now);
            _lastFired[i] = now;
            fired.Add(r);
        }
        return fired;
    }

    private static int IndexOf(List<byte> hay, byte[] needle, int from)
    {
        if (needle.Length == 0) return -1;
        for (int i = Math.Max(0, from); i <= hay.Count - needle.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && hay[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }
}

/// <summary>不依赖 HexUtil（它在 WPF 项目中），供规则与测试使用的简易 HEX 解析。</summary>
public static class HexUtilLite
{
    public static bool TryParse(string s, out byte[] data)
    {
        var list = new List<byte>();
        data = Array.Empty<byte>();
        foreach (var raw in s.Split(new[] { ' ', ',', ';', '-', ':', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string t = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? raw.Substring(2) : raw;
            if (t.Length == 1) t = "0" + t;
            if (t.Length % 2 != 0) return false;
            for (int i = 0; i < t.Length; i += 2)
            {
                if (!byte.TryParse(t.AsSpan(i, 2), System.Globalization.NumberStyles.HexNumber, null, out byte b)) return false;
                list.Add(b);
            }
        }
        data = list.ToArray();
        return true;
    }
}
