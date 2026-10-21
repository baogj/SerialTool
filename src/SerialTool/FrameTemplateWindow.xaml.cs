using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using SerialTool.Features;

namespace SerialTool;

public partial class FrameTemplateWindow : Window
{
    private readonly ObservableCollection<FrameTemplate> _items;
    private ObservableCollection<FrameField>? _fields;

    public FrameTemplateWindow(IEnumerable<FrameTemplate> templates, string? selected)
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        ChecksumCombo.ItemsSource = Checksum.Options.Select(o => new Opt<ChecksumKind>(o.Name, o.Kind)).ToList();
        LengthSizeCombo.ItemsSource = new[] { 1, 2, 4 };
        TypeColumn.ItemsSource = Enum.GetValues(typeof(FrameFieldType));
        _items = new ObservableCollection<FrameTemplate>(templates.Select(t => t.Clone()));
        TplList.ItemsSource = _items;
        TplList.SelectedItem = _items.FirstOrDefault(t => t.Name == selected) ?? _items.FirstOrDefault();
        UpdateEditor();
    }

    public List<FrameTemplate> Result { get; private set; } = new();
    public string? SelectedName { get; private set; }

    private FrameTemplate? Current => TplList.SelectedItem as FrameTemplate;

    private void Commit()
    {
        FieldGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        FieldGrid.CommitEdit(DataGridEditingUnit.Row, true);
        SyncFields();
    }

    private void SyncFields()
    {
        if (Current != null && _fields != null) Current.Fields = _fields.ToList();
    }

    private void UpdateEditor()
    {
        var t = Current;
        EditorPanel.IsEnabled = t != null;
        EditorPanel.DataContext = t;
        _fields = t == null ? null : new ObservableCollection<FrameField>(t.Fields);
        FieldGrid.ItemsSource = _fields;
        LengthPanel.IsEnabled = t?.HasLength == true;
        TestResult.Text = "";
    }

    private void TplList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 切换前保存上一个模板的字段
        if (e.RemovedItems.Count > 0 && e.RemovedItems[0] is FrameTemplate prev && _fields != null)
        {
            FieldGrid.CommitEdit(DataGridEditingUnit.Row, true);
            prev.Fields = _fields.ToList();
        }
        UpdateEditor();
    }

    private void Name_TextChanged(object sender, TextChangedEventArgs e)
    {
        // 刷新左侧列表显示的名称
        if (Current != null) TplList.Items.Refresh();
    }

    private void Layout_Changed(object sender, RoutedEventArgs e) => LengthPanel.IsEnabled = HasLengthChk.IsChecked == true;

    private string UniqueName(string baseName)
    {
        string n = baseName;
        for (int i = 2; _items.Any(t => t.Name == n); i++) n = $"{baseName} {i}";
        return n;
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        Commit();
        var t = new FrameTemplate { Name = UniqueName("新模板"), Header = "AA 55", HasLength = true, LengthOffset = 2, LengthSize = 1, Checksum = ChecksumKind.Sum8, ChecksumFrom = 2 };
        t.Fields.Add(new FrameField { Name = "命令", Offset = 3, Type = FrameFieldType.U8 });
        _items.Add(t);
        TplList.SelectedItem = t;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        Commit();
        if (Current == null) return;
        var t = Current.Clone();
        t.Name = UniqueName(Current.Name + " 副本");
        _items.Add(t);
        TplList.SelectedItem = t;
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Current == null) return;
        int i = TplList.SelectedIndex;
        _fields = null;
        _items.Remove(Current);
        if (_items.Count > 0) TplList.SelectedIndex = Math.Min(i, _items.Count - 1);
        UpdateEditor();
    }

    private void Sample_Click(object sender, RoutedEventArgs e)
    {
        Commit();
        var t = FrameTemplate.Sample();
        t.Name = UniqueName(t.Name);
        _items.Add(t);
        TplList.SelectedItem = t;
        TestBox.Text = "AA 55 05 01 00 FA 01 C2 C3 AA 55 05 01 00 FA 01 C2 00";
    }

    private void AddField_Click(object sender, RoutedEventArgs e)
    {
        if (_fields == null) return;
        int next = _fields.Count == 0 ? 3 : _fields.Max(f => f.Offset + f.Size);
        var f = new FrameField { Name = "字段" + (_fields.Count + 1), Offset = next, Type = FrameFieldType.U8 };
        _fields.Add(f);
        SyncFields();
        FieldGrid.SelectedItem = f;
        FieldGrid.ScrollIntoView(f);
    }

    private void RemoveField_Click(object sender, RoutedEventArgs e)
    {
        if (_fields == null || FieldGrid.SelectedItem is not FrameField f) return;
        FieldGrid.CommitEdit(DataGridEditingUnit.Row, true);
        _fields.Remove(f);
        SyncFields();
    }

    private void Test_Click(object sender, RoutedEventArgs e)
    {
        Commit();
        var t = Current;
        if (t == null) return;
        string? err = t.Validate();
        if (err != null)
        {
            TestResult.Text = "模板无效：" + err;
            return;
        }
        if (!FrameHex.TryParse(TestBox.Text, out var data) || data.Length == 0)
        {
            TestResult.Text = "请输入 HEX 测试数据，如 AA 55 05 01 00 FA 01 C2 C3";
            return;
        }
        var p = new FrameParser(t);
        var frames = p.Push(data, DateTime.Now);
        var sb = new StringBuilder();
        foreach (var f in frames)
            sb.AppendLine((f.Ok ? "✓ " : "✗ " + f.Error + "  ") + f.Summary + "    [" + Checksum.ToHex(f.Raw) + "]");
        if (frames.Count == 0) sb.AppendLine("没有完整帧。");
        if (p.Pending > 0) sb.AppendLine($"剩余 {p.Pending} 字节未成帧（等待更多数据）。");
        if (p.SkippedBytes > 0) sb.AppendLine($"丢弃 {p.SkippedBytes} 字节（未匹配帧头）。");
        TestResult.Text = sb.ToString().TrimEnd();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Commit();
        foreach (var t in _items)
        {
            if (string.IsNullOrWhiteSpace(t.Name))
            {
                ErrorText.Text = "模板名称不能为空";
                TplList.SelectedItem = t;
                return;
            }
            string? err = t.Validate();
            if (err != null)
            {
                ErrorText.Text = $"“{t.Name}”：{err}";
                TplList.SelectedItem = t;
                return;
            }
        }
        var dup = _items.GroupBy(t => t.Name).FirstOrDefault(g => g.Count() > 1);
        if (dup != null)
        {
            ErrorText.Text = $"模板名称重复：{dup.Key}";
            return;
        }
        Result = _items.Select(t => t.Clone()).ToList();
        SelectedName = Current?.Name;
        DialogResult = true;
    }
}
