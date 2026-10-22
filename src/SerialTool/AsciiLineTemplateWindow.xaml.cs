using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using SerialTool.Features;

namespace SerialTool;

public partial class AsciiLineTemplateWindow : Window
{
    private readonly ObservableCollection<AsciiLineTemplate> _items;
    private ObservableCollection<AsciiField>? _fields;
    private bool _loading;

    private static readonly AsciiSplitMode[] Modes =
    {
        AsciiSplitMode.Delimiter, AsciiSplitMode.Regex, AsciiSplitMode.FixedWidth, AsciiSplitMode.KeyValue,
    };
    private static readonly string[] ModeNames = { "分隔符列", "正则捕获组", "固定列宽", "键值对 key=value" };
    private static readonly AsciiFieldType[] FieldTypes =
    {
        AsciiFieldType.Text, AsciiFieldType.Int, AsciiFieldType.Float, AsciiFieldType.Hex,
    };

    public AsciiLineTemplateWindow(IEnumerable<AsciiLineTemplate> templates, string? selected)
    {
        InitializeComponent();
        _items = new ObservableCollection<AsciiLineTemplate>(templates.Select(t => t.Clone()));
        if (_items.Count == 0) _items.Add(AsciiLineTemplate.SampleKeyValue());
        ModeCombo.ItemsSource = ModeNames;
        TypeCol.ItemsSource = FieldTypes;
        EncCombo.ItemsSource = new[] { "", "UTF-8", "GBK", "ASCII", "UTF-16" };
        TplList.ItemsSource = _items;
        TplList.SelectedItem = _items.FirstOrDefault(t => t.Name == selected) ?? _items[0];
    }

    public List<AsciiLineTemplate> Result { get; private set; } = new();
    public string? SelectedName { get; private set; }
    private AsciiLineTemplate? Current => TplList.SelectedItem as AsciiLineTemplate;

    private void TplList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.RemovedItems.Count > 0 && e.RemovedItems[0] is AsciiLineTemplate prev)
            SaveUi(prev);
        LoadUi(Current);
    }

    private void LoadUi(AsciiLineTemplate? t)
    {
        _loading = true;
        try
        {
            if (t == null) return;
            NameBox.Text = t.Name;
            PrefixBox.Text = t.Prefix;
            SuffixBox.Text = t.Suffix;
            ModeCombo.SelectedIndex = Array.IndexOf(Modes, t.Mode);
            if (ModeCombo.SelectedIndex < 0) ModeCombo.SelectedIndex = 0;
            DelimBox.Text = t.Delimiter;
            KvSepBox.Text = t.KeyValueSep;
            KvPairBox.Text = t.KeyValuePairSep;
            EncCombo.Text = t.Encoding ?? "";
            TrimChk.IsChecked = t.TrimParts;
            SkipEmptyChk.IsChecked = t.SkipEmpty;
            _fields = new ObservableCollection<AsciiField>(t.Fields.Select(f => f.Clone()));
            FieldGrid.ItemsSource = _fields;
            ApplyModeUi();
        }
        finally { _loading = false; }
    }

    private void SaveUi(AsciiLineTemplate t)
    {
        CommitGrid();
        t.Name = NameBox.Text.Trim();
        if (t.Name.Length == 0) t.Name = "未命名";
        t.Prefix = PrefixBox.Text ?? "";
        t.Suffix = SuffixBox.Text ?? "";
        int mi = ModeCombo.SelectedIndex;
        t.Mode = mi >= 0 && mi < Modes.Length ? Modes[mi] : AsciiSplitMode.Delimiter;
        t.Delimiter = DelimBox.Text ?? "";
        t.KeyValueSep = KvSepBox.Text ?? "=";
        t.KeyValuePairSep = KvPairBox.Text ?? ",";
        t.Encoding = EncCombo.Text?.Trim() ?? "";
        t.TrimParts = TrimChk.IsChecked == true;
        t.SkipEmpty = SkipEmptyChk.IsChecked == true;
        t.Fields = _fields?.Select(f => f.Clone()).ToList() ?? new List<AsciiField>();
    }

    private void CommitGrid()
    {
        try
        {
            FieldGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            FieldGrid.CommitEdit(DataGridEditingUnit.Row, true);
        }
        catch { /* ignore */ }
    }

    private void ApplyModeUi()
    {
        var mode = ModeCombo.SelectedIndex >= 0 && ModeCombo.SelectedIndex < Modes.Length
            ? Modes[ModeCombo.SelectedIndex] : AsciiSplitMode.Delimiter;
        DelimLabel.Text = mode == AsciiSplitMode.Regex ? "正则" : "分隔符";
        DelimBox.IsEnabled = mode is AsciiSplitMode.Delimiter or AsciiSplitMode.Regex;
        KvSepBox.IsEnabled = KvPairBox.IsEnabled = mode == AsciiSplitMode.KeyValue;
    }

    private void Mode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        ApplyModeUi();
        Field_Changed(sender, e);
    }

    private void Field_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || Current == null) return;
        SaveUi(Current);
        TplList.Items.Refresh();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (Current != null) SaveUi(Current);
        var t = new AsciiLineTemplate { Name = UniqueName("新模板"), Mode = AsciiSplitMode.Delimiter, Delimiter = ",",
            Fields = { new AsciiField { Name = "列1", Index = 0 } } };
        _items.Add(t);
        TplList.SelectedItem = t;
    }

    private void Del_Click(object sender, RoutedEventArgs e)
    {
        if (Current == null || _items.Count <= 1) return;
        int i = TplList.SelectedIndex;
        _items.Remove(Current);
        TplList.SelectedIndex = Math.Min(i, _items.Count - 1);
    }

    private void SampleKv_Click(object sender, RoutedEventArgs e)
    {
        if (Current != null) SaveUi(Current);
        var t = AsciiLineTemplate.SampleKeyValue();
        t.Name = UniqueName(t.Name);
        _items.Add(t);
        TplList.SelectedItem = t;
    }

    private void SampleCsv_Click(object sender, RoutedEventArgs e)
    {
        if (Current != null) SaveUi(Current);
        var t = AsciiLineTemplate.SampleCsv();
        t.Name = UniqueName(t.Name);
        _items.Add(t);
        TplList.SelectedItem = t;
    }

    private string UniqueName(string baseName)
    {
        var used = _items.Select(x => x.Name).ToHashSet();
        if (!used.Contains(baseName)) return baseName;
        for (int n = 2; ; n++)
        {
            string s = $"{baseName} ({n})";
            if (!used.Contains(s)) return s;
        }
    }

    private void AddField_Click(object sender, RoutedEventArgs e)
    {
        if (_fields == null) return;
        _fields.Add(new AsciiField { Name = $"列{_fields.Count + 1}", Index = _fields.Count, Key = $"k{_fields.Count + 1}", Length = 4 });
    }

    private void DelField_Click(object sender, RoutedEventArgs e)
    {
        if (_fields == null || FieldGrid.SelectedItem is not AsciiField f) return;
        _fields.Remove(f);
    }

    private void Test_Click(object sender, RoutedEventArgs e)
    {
        if (Current == null) return;
        SaveUi(Current);
        var err = Current.Validate();
        if (err != null) { TestOut.Text = "模板无效：" + err; return; }
        var p = new AsciiLineParser(Current);
        var enc = string.IsNullOrWhiteSpace(Current.Encoding) ? Encoding.UTF8 : EncodingUtil.Get(Current.Encoding);
        string text = TestBox.Text;
        if (!text.EndsWith('\n') && !text.EndsWith('\r')) text += "\n";
        var rows = p.Push(enc.GetBytes(text), DateTime.Now, false, enc);
        if (rows.Count == 0) { TestOut.Text = "没有解析出完整行（请以换行结束）"; return; }
        var sb = new StringBuilder();
        foreach (var r in rows)
            sb.AppendLine(r.Ok ? "✓ " + r.Summary : "✗ " + r.Error + " | " + r.Line);
        TestOut.Text = sb.ToString().TrimEnd();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Current != null) SaveUi(Current);
        foreach (var t in _items)
        {
            var err = t.Validate();
            if (err != null)
            {
                MessageBox.Show(this, $"模板「{t.Name}」：{err}", "ASCII 模板", MessageBoxButton.OK, MessageBoxImage.Warning);
                TplList.SelectedItem = t;
                return;
            }
        }
        Result = _items.Select(t => t.Clone()).ToList();
        SelectedName = Current?.Name;
        DialogResult = true;
    }
}
