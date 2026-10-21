using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SerialTool.Features;

namespace SerialTool;

/// <summary>v1.3：接收区查找（Ctrl+F）与过滤。</summary>
public partial class MainWindow
{
    private static readonly string[] FilterModes = { "只显示匹配行", "隐藏匹配行" };

    private LineFilter? _search;
    private bool _filterOn;
    private bool _searchHold;     // 正在查看查找结果：暂停行视图自动滚动
    private int _searchCurrent;   // 当前是第几个匹配（1 起），0 = 未定位

    private bool FilterInclude => FilterModeCombo.SelectedIndex != 1;

    private void OpenSearch_Click(object sender, RoutedEventArgs e) => OpenSearchBar();

    private void OpenSearchBar()
    {
        MainTabs.SelectedItem = RxTab;
        SearchBar.Visibility = Visibility.Visible;
        SearchBox.Focus();
        SearchBox.SelectAll();
        UpdateSearchCount();
    }

    private void SearchClose_Click(object sender, RoutedEventArgs e) => CloseSearchBar();

    private void CloseSearchBar()
    {
        SearchBox.Text = "";          // 触发 UpdateSearch：清除高亮
        if (_filterOn)
        {
            FilterChk.IsChecked = false;
            SetFilter(false);
        }
        _searchHold = false;
        SearchBar.Visibility = Visibility.Collapsed;
        ScrollLinesToEnd();
    }

    private void SearchClear_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        if (_filterOn)
        {
            FilterChk.IsChecked = false;
            SetFilter(false);
        }
        _searchHold = false;
        ScrollLinesToEnd();
        SearchBox.Focus();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateSearch();

    private void SearchOption_Click(object sender, RoutedEventArgs e) => UpdateSearch();

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SearchMove((Keyboard.Modifiers & ModifierKeys.Shift) != 0);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseSearchBar();
            e.Handled = true;
        }
    }

    private void SearchNext_Click(object sender, RoutedEventArgs e) => SearchMove(false);
    private void SearchPrev_Click(object sender, RoutedEventArgs e) => SearchMove(true);

    /// <summary>查找词或选项变化：重建匹配器，刷新高亮、过滤与计数。</summary>
    private void UpdateSearch()
    {
        if (SearchBox == null) return;
        _search = LineFilter.Create(SearchBox.Text, SearchRegexChk.IsChecked == true, SearchCaseChk.IsChecked != true, out string? err);
        _searchCurrent = 0;
        if (err != null)
        {
            SearchCountText.Text = "正则无效";
            SearchCountText.ToolTip = err;
            SearchBox.SetResourceReference(Control.BorderBrushProperty, "DangerBrush");
        }
        else
        {
            SearchCountText.ToolTip = null;
            SearchBox.ClearValue(Control.BorderBrushProperty);
        }
        HighlightStyles.SetSearch(_search);
        if (_filterOn) ApplyLineFilter(); // 内部会 Refresh
        else ProtocolList.Items.Refresh();
        MonitorList.Items.Refresh();
        if (_search == null) _searchHold = false;
        UpdateSearchCount();
    }

    private void FilterChk_Click(object sender, RoutedEventArgs e) => SetFilter(FilterChk.IsChecked == true);

    private void FilterMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !_filterOn) return;
        ApplyLineFilter();
        UpdateSearchCount();
    }

    private void SetFilter(bool on)
    {
        FlushQueue();
        _filterOn = on;
        ApplyProtocolModeUi(); // 过滤需要行视图：普通文本视图会切换为行视图
        ApplyLineFilter();
        UpdateSearchCount();
        if (on && _search == null) SetStatus("过滤已开启：在查找框输入内容后生效");
        else SetStatus(on ? $"过滤：{FilterModeCombo.SelectedItem}「{SearchBox.Text}」" : "已取消过滤，显示全部行");
    }

    /// <summary>行视图过滤（TrimLines 重建集合后也要重新调用）。</summary>
    private void ApplyLineFilter()
    {
        if (ProtocolList == null) return;
        var f = _search;
        if (_filterOn && f != null)
        {
            bool include = FilterInclude;
            ProtocolList.Items.Filter = o => o is ProtocolLine l && f.Passes(l.Text, include);
        }
        else if (ProtocolList.Items.Filter != null)
        {
            ProtocolList.Items.Filter = null;
        }
        else
        {
            ProtocolList.Items.Refresh();
        }
    }

    private bool LineViewActive => ProtocolList.Visibility == Visibility.Visible;

    /// <summary>计算匹配总数（行视图按可见行统计；文本视图按全文）。</summary>
    private (int Total, List<(int Index, int Count)> Lines) CountMatches()
    {
        var lines = new List<(int, int)>();
        var f = _search;
        if (f == null) return (0, lines);
        int total = 0;
        if (LineViewActive)
        {
            var items = ProtocolList.Items;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] is not ProtocolLine l) continue;
                int n = f.FindAll(l.Text, 1000).Count;
                if (n == 0) continue;
                lines.Add((i, n));
                total += n;
            }
        }
        else
        {
            total = f.FindAll(RxBox.Text, 200_000).Count;
        }
        return (total, lines);
    }

    private void UpdateSearchCount()
    {
        if (SearchCountText == null || SearchBar.Visibility != Visibility.Visible) return;
        if (_search == null)
        {
            if (SearchCountText.Text != "正则无效" || SearchBox.Text.Length == 0)
                SearchCountText.Text = _filterOn ? "（输入查找内容）" : "";
            return;
        }
        var (total, _) = CountMatches();
        string filter = _filterOn ? $"  显示 {ProtocolList.Items.Count} 行" : "";
        SearchCountText.Text = total == 0 ? "无匹配" + filter
            : _searchCurrent > 0 ? $"{Math.Min(_searchCurrent, total)}/{total}{filter}"
            : $"共 {total} 处{filter}";
    }

    private void SearchMove(bool backward)
    {
        if (_search == null)
        {
            SearchBox.Focus();
            return;
        }
        var f = _search;
        if (LineViewActive)
        {
            var (total, lines) = CountMatches();
            if (lines.Count == 0)
            {
                SearchCountText.Text = "无匹配";
                return;
            }
            int cur = ProtocolList.SelectedIndex;
            int pick;
            if (!backward)
            {
                pick = lines.FindIndex(l => l.Index > cur);
                if (pick < 0) pick = 0;
            }
            else
            {
                pick = lines.FindLastIndex(l => l.Index < (cur < 0 ? int.MaxValue : cur));
                if (pick < 0) pick = lines.Count - 1;
            }
            int idx = lines[pick].Index;
            _searchHold = true;
            ProtocolList.SelectedIndex = idx;
            ProtocolList.ScrollIntoView(ProtocolList.Items[idx]);
            _searchCurrent = lines.Take(pick).Sum(l => l.Count) + 1;
            string filter = _filterOn ? $"  显示 {ProtocolList.Items.Count} 行" : "";
            SearchCountText.Text = $"{_searchCurrent}/{total}（第 {pick + 1}/{lines.Count} 行）{filter}";
            return;
        }

        string text = RxBox.Text;
        int start = backward ? RxBox.SelectionStart : RxBox.SelectionStart + Math.Max(RxBox.SelectionLength, 0);
        if (!backward && RxBox.SelectionLength == 0 && _searchCurrent == 0) start = 0;
        var (s, len) = f.FindNext(text, start, backward);
        if (s < 0)
        {
            SearchCountText.Text = "无匹配";
            return;
        }
        RxBox.Select(s, len);
        int line = RxBox.GetLineIndexFromCharacterIndex(s);
        if (line >= 0) RxBox.ScrollToLine(line);
        var all = f.FindAll(text, 200_000);
        _searchCurrent = all.FindIndex(m => m.Start == s) + 1;
        SearchCountText.Text = $"{_searchCurrent}/{all.Count}";
        if (AutoScrollChk.IsChecked == true)
            SetStatus("提示：查看查找结果时可勾选“暂停显示”，避免新数据把位置滚走");
    }
}
