using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using SerialTool.Features;

namespace SerialTool;

public partial class HighlightRulesWindow : Window
{
    public static readonly string[] PresetColors =
    {
        "#FDE047", "#F87171", "#4ADE80", "#60A5FA", "#A78BFA", "#F472B6", "#FB923C", "#2DD4BF", "#E5E7EB",
    };

    private readonly ObservableCollection<HighlightRule> _items;
    private readonly Encoding _encoding;

    public HighlightRulesWindow(IEnumerable<HighlightRule> rules, Encoding encoding)
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        _encoding = encoding;
        _items = new ObservableCollection<HighlightRule>(rules.Select(r => r.Clone()));
        RuleGrid.ItemsSource = _items;
    }

    public List<HighlightRule> Result { get; private set; } = new();

    private void CommitEdits()
    {
        RuleGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        RuleGrid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        CommitEdits();
        var r = new HighlightRule
        {
            Name = $"规则{_items.Count + 1}",
            Color = PresetColors[_items.Count % PresetColors.Length],
        };
        _items.Add(r);
        RuleGrid.SelectedItem = r;
        RuleGrid.ScrollIntoView(r);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        CommitEdits();
        if (RuleGrid.SelectedItem is HighlightRule r) _items.Remove(r);
    }

    private void Up_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void Down_Click(object sender, RoutedEventArgs e) => Move(1);

    private void Move(int delta)
    {
        CommitEdits();
        if (RuleGrid.SelectedItem is not HighlightRule r) return;
        int i = _items.IndexOf(r), j = i + delta;
        if (i < 0 || j < 0 || j >= _items.Count) return;
        _items.Move(i, j);
        RuleGrid.SelectedItem = r;
    }

    private List<string> Validate()
    {
        var errors = new List<string>();
        for (int i = 0; i < _items.Count; i++)
        {
            var r = _items[i];
            string name = $"第 {i + 1} 条「{(string.IsNullOrWhiteSpace(r.Name) ? r.Pattern : r.Name)}」";
            if (!r.Enabled) continue;
            if (string.IsNullOrEmpty(r.Pattern)) { errors.Add(name + "：匹配内容为空"); continue; }
            var c = new CompiledRule(r, i, _encoding);
            if (!c.IsValid) errors.Add(name + "：" + c.Error);
            if (r.ReplyEnabled && string.IsNullOrEmpty(r.ReplyContent)) errors.Add(name + "：勾选了自动回复但回复内容为空");
            if (r.ReplyEnabled && r.ReplyIsHex && !HexUtil.TryParse(r.ReplyContent, out _, out string? he))
                errors.Add(name + "：回复内容 " + he);
            if (r.ReplyEnabled && r.CooldownMs < 100)
                errors.Add(name + "：冷却时间小于 100 ms，容易造成回复风暴（建议 ≥ 500）");
        }
        return errors;
    }

    private void Test_Click(object sender, RoutedEventArgs e)
    {
        CommitEdits();
        var compiled = _items.Select((r, i) => new CompiledRule(r, i, _encoding)).Where(c => c.Rule.IsUsable && c.IsValid).ToList();
        string text = TestBox.Text;
        var spans = HighlightMatcher.Find(text, compiled);
        TestResult.Text = spans.Count == 0
            ? "没有规则命中"
            : "命中：" + string.Join("，", spans.Select(s =>
              {
                  var r = _items[s.RuleIndex];
                  return $"{(string.IsNullOrWhiteSpace(r.Name) ? r.Pattern : r.Name)}「{text.Substring(s.Start, s.Length)}」"
                         + (r.ReplyEnabled ? " → 回复" : "");
              }));
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        CommitEdits();
        var errors = Validate();
        if (errors.Count > 0)
        {
            var ans = MessageBox.Show(this, "以下规则有问题（有错误的规则会被忽略）：\n\n" + string.Join("\n", errors) + "\n\n仍然保存吗？",
                "规则检查", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (ans != MessageBoxResult.Yes) return;
        }
        Result = _items
            .Where(r => !string.IsNullOrWhiteSpace(r.Name) || !string.IsNullOrEmpty(r.Pattern))
            .Select(r => r.Clone())
            .ToList();
        DialogResult = true;
    }
}

/// <summary>"#RRGGBB" 文本 → 画刷（颜色列的色块）。</summary>
public sealed class ColorTextToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var c = HighlightStyles.ParseColor(value as string, Colors.Transparent);
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
