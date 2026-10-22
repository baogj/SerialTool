using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using SerialTool.Features;

namespace SerialTool;

public partial class MainWindow
{
    private List<AsciiLineTemplate> _asciiTemplates = new();
    private AsciiLineParser? _asciiRx, _asciiTx;
    private readonly ObservableCollection<FrameRow> _asciiRows = new();
    private readonly List<FrameRow> _pendingAsciiRows = new();
    private const int MaxAsciiRows = 5000;

    private string AsciiTemplatePath => AsciiLineTemplateStore.PathIn(Path.GetDirectoryName(SettingsStore.FilePath) ?? AppContext.BaseDirectory);

    private void InitControlsV17()
    {
        var s = _settings;
        _asciiTemplates = AsciiLineTemplateStore.Load(AsciiTemplatePath);
        if (!File.Exists(AsciiTemplatePath))
        {
            try { AsciiLineTemplateStore.Save(AsciiTemplatePath, _asciiTemplates); } catch (Exception ex) { App.LogCrash(ex); }
        }
        AsciiGrid.ItemsSource = _asciiRows;
        AsciiEnableChk.IsChecked = s.AsciiLineParseEnabled;
        AsciiTxChk.IsChecked = s.AsciiLineParseTx;
        RefreshAsciiTemplates(s.AsciiLineTemplateName);
    }

    private void CollectSettingsV17()
    {
        var s = _settings;
        s.AsciiLineTemplateName = (AsciiTplCombo.SelectedItem as AsciiLineTemplate)?.Name ?? "";
        s.AsciiLineParseEnabled = AsciiEnableChk.IsChecked == true;
        s.AsciiLineParseTx = AsciiTxChk.IsChecked == true;
    }

    private void FeedAsciiParsers(DataChunk c)
    {
        if (AsciiEnableChk.IsChecked != true) return;
        var p = c.IsTx ? (AsciiTxChk.IsChecked == true ? _asciiTx : null) : _asciiRx;
        if (p == null) return;
        foreach (var f in p.Push(c.Data, c.Time, c.IsTx, CurrentEncoding()))
        {
            string[] values = new string[p.Template.Fields.Count];
            for (int i = 0; i < values.Length; i++)
            {
                var name = p.Template.Fields[i].Name;
                var unit = p.Template.Fields[i].Unit;
                var v = f.Fields.FirstOrDefault(x => x.Name == name)?.Value ?? "";
                if (!string.IsNullOrEmpty(unit) && v.EndsWith(" " + unit, StringComparison.Ordinal))
                    v = v[..^(unit.Length + 1)];
                values[i] = v;
            }
            _pendingAsciiRows.Add(new FrameRow
            {
                Time = f.Time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
                Dir = f.IsTx ? "TX" : "RX",
                Status = f.Ok ? "✓ 正确" : "✗ " + (f.Error ?? "错误"),
                Values = values,
                Raw = f.Line,
                Ok = f.Ok,
            });
        }
    }

    private void FlushAsciiRows()
    {
        if (_pendingAsciiRows.Count == 0) return;
        var rows = _pendingAsciiRows.Count > 1000 ? _pendingAsciiRows.Skip(_pendingAsciiRows.Count - 1000).ToList() : _pendingAsciiRows.ToList();
        _pendingAsciiRows.Clear();
        int overflow = _asciiRows.Count + rows.Count - MaxAsciiRows;
        if (overflow > 0)
        {
            if (overflow >= _asciiRows.Count) _asciiRows.Clear();
            else for (int i = 0; i < overflow; i++) _asciiRows.RemoveAt(0);
        }
        foreach (var r in rows) _asciiRows.Add(r);
        UpdateAsciiStats();
        if (AutoScrollChk.IsChecked == true && AsciiGrid.IsVisible && _asciiRows.Count > 0)
        {
            var last = CollectionViewSource.GetDefaultView(_asciiRows).Cast<object>().LastOrDefault();
            if (last != null) AsciiGrid.ScrollIntoView(last);
        }
    }

    private void UpdateAsciiStats()
    {
        long ok = (_asciiRx?.OkCount ?? 0) + (_asciiTx?.OkCount ?? 0);
        long bad = (_asciiRx?.BadCount ?? 0) + (_asciiTx?.BadCount ?? 0);
        long skip = (_asciiRx?.SkippedLines ?? 0) + (_asciiTx?.SkippedLines ?? 0);
        AsciiStatsText.Text = $"正确 {ok}  错误 {bad}  跳过 {skip} 行";
    }

    private void RefreshAsciiTemplates(string? select)
    {
        AsciiTplCombo.ItemsSource = null;
        AsciiTplCombo.ItemsSource = _asciiTemplates;
        AsciiTplCombo.SelectedItem = _asciiTemplates.FirstOrDefault(t => t.Name == select) ?? _asciiTemplates.FirstOrDefault();
        ResetAsciiParsers();
    }

    private void ResetAsciiParsers()
    {
        var tpl = AsciiTplCombo.SelectedItem as AsciiLineTemplate;
        BuildAsciiColumns(tpl);
        if (tpl == null || tpl.Validate() != null)
        {
            _asciiRx = _asciiTx = null;
            AsciiInfoText.Text = tpl == null ? "请选择或新建 ASCII 行模板" : "模板无效：" + tpl.Validate();
            return;
        }
        _asciiRx = new AsciiLineParser(tpl);
        _asciiTx = new AsciiLineParser(tpl);
        AsciiInfoText.Text = DescribeAscii(tpl) + "    模板文件：" + AsciiTemplatePath;
    }

    private static string DescribeAscii(AsciiLineTemplate t)
    {
        string mode = t.Mode switch
        {
            AsciiSplitMode.Delimiter => "分隔符「" + t.Delimiter + "」",
            AsciiSplitMode.Regex => "正则",
            AsciiSplitMode.FixedWidth => "固定列宽",
            _ => "键值对「" + t.KeyValueSep + "」/「" + t.KeyValuePairSep + "」",
        };
        string filt = "";
        if (t.Prefix.Length > 0) filt += " 前缀「" + t.Prefix + "」";
        if (t.Suffix.Length > 0) filt += " 后缀「" + t.Suffix + "」";
        if (filt.Length == 0) filt = " 无前缀/后缀";
        return $"按行切分 · {mode} · {t.Fields.Count} 字段 ·{filt}" +
               (string.IsNullOrWhiteSpace(t.Encoding) ? " · 编码=会话" : " · 编码=" + t.Encoding);
    }

    private void BuildAsciiColumns(AsciiLineTemplate? tpl)
    {
        AsciiGrid.Columns.Clear();
        AsciiGrid.Columns.Add(new DataGridTextColumn { Header = "时间", Binding = new Binding(nameof(FrameRow.Time)), Width = 96 });
        AsciiGrid.Columns.Add(new DataGridTextColumn { Header = "方向", Binding = new Binding(nameof(FrameRow.Dir)), Width = 44 });
        AsciiGrid.Columns.Add(new DataGridTextColumn { Header = "状态", Binding = new Binding(nameof(FrameRow.Status)), Width = 120 });
        if (tpl != null)
        {
            for (int i = 0; i < tpl.Fields.Count; i++)
            {
                var f = tpl.Fields[i];
                string header = string.IsNullOrEmpty(f.Unit) ? f.Name : $"{f.Name} ({f.Unit})";
                AsciiGrid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding($"Values[{i}]"), Width = DataGridLength.Auto, MinWidth = 70 });
            }
        }
        AsciiGrid.Columns.Add(new DataGridTextColumn { Header = "原始行", Binding = new Binding(nameof(FrameRow.Raw)), Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 160 });
    }

    private void AsciiTpl_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading && AsciiGrid.Columns.Count > 0) return;
        ResetAsciiParsers();
    }

    private void AsciiOption_Click(object sender, RoutedEventArgs e)
    {
        _asciiRx?.Reset();
        _asciiTx?.Reset();
    }

    private void AsciiBadOnly_Click(object sender, RoutedEventArgs e)
    {
        var view = CollectionViewSource.GetDefaultView(_asciiRows);
        view.Filter = AsciiBadOnlyChk.IsChecked == true ? o => o is FrameRow r && !r.Ok : null;
    }

    private void AsciiClear_Click(object sender, RoutedEventArgs e)
    {
        _asciiRows.Clear();
        _pendingAsciiRows.Clear();
        ResetAsciiParsers();
    }

    private void AsciiEdit_Click(object sender, RoutedEventArgs e)
    {
        var current = (AsciiTplCombo.SelectedItem as AsciiLineTemplate)?.Name;
        var dlg = new AsciiLineTemplateWindow(_asciiTemplates, current) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _asciiTemplates = dlg.Result;
        try
        {
            AsciiLineTemplateStore.Save(AsciiTemplatePath, _asciiTemplates);
            SetStatus($"ASCII 行模板已保存（{_asciiTemplates.Count} 个）：{AsciiTemplatePath}");
        }
        catch (Exception ex)
        {
            SetStatus("保存 ASCII 模板失败：" + ex.Message, true);
        }
        RefreshAsciiTemplates(dlg.SelectedName ?? current);
    }

    private void AsciiExport_Click(object sender, RoutedEventArgs e)
    {
        if (_asciiRows.Count == 0)
        {
            SetStatus("没有可导出的行", true);
            return;
        }
        var dlg = new SaveFileDialog
        {
            Title = "导出 ASCII 解析结果",
            FileName = $"ascii_lines_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
            Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", AsciiGrid.Columns.Select(c => Csv(c.Header?.ToString() ?? ""))));
            foreach (var r in _asciiRows)
                sb.AppendLine(string.Join(",", new[] { r.Time, r.Dir, r.Status }.Concat(r.Values).Append(r.Raw).Select(Csv)));
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
            SetStatus($"已导出 {_asciiRows.Count} 行到 {dlg.FileName}");
        }
        catch (Exception ex)
        {
            SetStatus("导出失败：" + ex.Message, true);
        }
    }
}
