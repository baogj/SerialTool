using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace SerialTool.Features;

/// <summary>规则高亮颜色表（索引 = 规则序号），由主窗口在规则变化时更新。</summary>
public static class HighlightStyles
{
    private static (Brush Bg, Brush Fg)[] _styles = Array.Empty<(Brush, Brush)>();

    /// <summary>当前参与高亮的规则（空 = 不高亮）。</summary>
    public static IReadOnlyList<CompiledRule> Active { get; private set; } = Array.Empty<CompiledRule>();
    public static int Version { get; private set; }

    public static void Set(IReadOnlyList<HighlightRule> rules, IReadOnlyList<CompiledRule> active)
    {
        var arr = new (Brush, Brush)[rules.Count];
        for (int i = 0; i < rules.Count; i++) arr[i] = Make(rules[i].Color);
        _styles = arr;
        Active = active;
        Version++;
    }

    public static List<HighlightSpan>? SpansFor(ProtocolLine line)
    {
        if (line.SpansVersion != Version)
        {
            line.Spans = Active.Count == 0 ? null : HighlightMatcher.Find(line.Text, Active);
            line.SpansVersion = Version;
        }
        return line.Spans;
    }

    public static (Brush Bg, Brush Fg) Get(int index) =>
        index >= 0 && index < _styles.Length ? _styles[index] : Make("#FDE047");

    public static Color ParseColor(string? s, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(string.IsNullOrWhiteSpace(s) ? "" : s.Trim()); }
        catch { return fallback; }
    }

    public static (Brush Bg, Brush Fg) Make(string? color)
    {
        var c = ParseColor(color, Color.FromRgb(0xFD, 0xE0, 0x47));
        var bg = new SolidColorBrush(c);
        bg.Freeze();
        double lum = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
        return (bg, lum > 140 ? Brushes.Black : Brushes.White);
    }
}

/// <summary>按 ProtocolLine 的类别着色，并对高亮区间加背景色。用于虚拟化列表。</summary>
public sealed class ColoredLineBlock : TextBlock
{
    public static readonly DependencyProperty LineProperty = DependencyProperty.Register(
        nameof(Line), typeof(ProtocolLine), typeof(ColoredLineBlock),
        new PropertyMetadata(null, (d, _) => ((ColoredLineBlock)d).Rebuild()));

    public ProtocolLine? Line
    {
        get => (ProtocolLine?)GetValue(LineProperty);
        set => SetValue(LineProperty, value);
    }

    private int _themeVersion = -1;

    public ColoredLineBlock()
    {
        TextWrapping = TextWrapping.Wrap;
        Loaded += (_, _) => { if (_themeVersion != ThemeManager.Version) Rebuild(); };
    }

    public void Rebuild()
    {
        _themeVersion = ThemeManager.Version;
        Inlines.Clear();
        var line = Line;
        if (line == null) return;
        Foreground = ThemeManager.ToneBrush(line.Tone);
        string text = line.Text;
        var spans = HighlightStyles.SpansFor(line);
        if (spans == null || spans.Count == 0)
        {
            Inlines.Add(new Run(text));
            return;
        }
        int pos = 0;
        foreach (var s in spans)
        {
            if (s.Start < pos || s.Start + s.Length > text.Length) continue;
            if (s.Start > pos) Inlines.Add(new Run(text.Substring(pos, s.Start - pos)));
            var (bg, fg) = HighlightStyles.Get(s.RuleIndex);
            Inlines.Add(new Run(text.Substring(s.Start, s.Length)) { Background = bg, Foreground = fg, FontWeight = FontWeights.SemiBold });
            pos = s.Start + s.Length;
        }
        if (pos < text.Length) Inlines.Add(new Run(text.Substring(pos)));
    }
}
