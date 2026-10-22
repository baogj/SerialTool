using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SerialTool.Features;

namespace SerialTool;

/// <summary>多项发送表格行。</summary>
public sealed class MultiSendRow : INotifyPropertyChanged
{
    private int _index;
    private bool _isCurrent;

    public MultiSendRow(MultiSendItem item) => Item = item;
    public MultiSendItem Item { get; }

    public int Index { get => _index; set => Set(ref _index, value); }
    public bool IsCurrent { get => _isCurrent; set => Set(ref _isCurrent, value); }

    public bool Enabled { get => Item.Enabled; set { Item.Enabled = value; OnChanged(); } }
    public string Name { get => Item.Name; set { Item.Name = value ?? ""; OnChanged(); } }
    public string Content { get => Item.Content; set { Item.Content = value ?? ""; OnChanged(); } }
    public bool Hex { get => Item.Hex; set { Item.Hex = value; OnChanged(); } }
    public string Checksum { get => Item.Checksum; set { Item.Checksum = string.IsNullOrWhiteSpace(value) ? "None" : value; OnChanged(); } }
    public int DelayMs
    {
        get => Item.DelayMs;
        set { Item.DelayMs = Math.Clamp(value, 0, 3_600_000); OnChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    private void Set<T>(ref T f, T v, [CallerMemberName] string? n = null)
    {
        if (Equals(f, v)) return;
        f = v;
        OnChanged(n);
    }
}

public partial class MainWindow
{
    private readonly ObservableCollection<MultiSendRow> _msRows = new();
    private readonly MultiSendEngine _msEngine = new();
    private bool _msUiBusy;

    private void InitControlsV16()
    {
        MsGrid.ItemsSource = _msRows;
        var items = _settings.MultiSendItems;
        if (items == null || items.Count == 0)
        {
            items = new()
            {
                new MultiSendItem { Name = "AT", Content = "AT\\r\\n", DelayMs = 200 },
                new MultiSendItem { Name = "版本", Content = "AT+GMR\\r\\n", DelayMs = 500 },
                new MultiSendItem { Name = "读寄存器", Content = "01 03 00 00 00 02", Hex = true, Checksum = "ModbusCrc16", DelayMs = 300, Enabled = false },
            };
        }
        LoadMultiSendRows(items);
        MsLoopChk.IsChecked = _settings.MultiSendLoop;

        _msEngine.StateChanged += st => Dispatcher.BeginInvoke(() => UpdateMsButtons(st));
        _msEngine.CurrentChanged += idx => Dispatcher.BeginInvoke(() => HighlightMsRow(idx));
        _msEngine.Finished += err => Dispatcher.BeginInvoke(() =>
        {
            HighlightMsRow(-1);
            UpdateMsButtons(MultiSendState.Idle);
            if (err != null) SetStatus("多项发送中止：" + err, true);
            else if (_msEngine.SendsCompleted > 0)
                SetStatus($"多项发送结束：共 {_msEngine.SendsCompleted} 条，循环 {_msEngine.LoopsCompleted} 轮");
            else
                SetStatus("多项发送已停止");
        });
    }

    private void CollectSettingsV16()
    {
        CommitMsGrid();
        _settings.MultiSendItems = _msRows.Select(r => r.Item.Clone()).ToList();
        _settings.MultiSendLoop = MsLoopChk.IsChecked == true;
    }

    private void ShutdownV16()
    {
        try { _msEngine.Stop(); } catch { /* ignore */ }
    }

    private void LoadMultiSendRows(System.Collections.Generic.List<MultiSendItem> items)
    {
        _msRows.Clear();
        foreach (var it in items)
        {
            it.Normalize();
            _msRows.Add(new MultiSendRow(it.Clone()));
        }
        ReindexMs();
    }

    private void ReindexMs()
    {
        for (int i = 0; i < _msRows.Count; i++) _msRows[i].Index = i + 1;
    }

    private void CommitMsGrid()
    {
        try
        {
            MsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            MsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        }
        catch { /* ignore */ }
    }

    private void HighlightMsRow(int idx)
    {
        for (int i = 0; i < _msRows.Count; i++)
            _msRows[i].IsCurrent = i == idx;
        if (idx >= 0 && idx < _msRows.Count)
            MsGrid.ScrollIntoView(_msRows[idx]);
    }

    private void UpdateMsButtons(MultiSendState st)
    {
        bool idle = st == MultiSendState.Idle;
        bool running = st == MultiSendState.Running;
        bool paused = st == MultiSendState.Paused;
        MsStartBtn.IsEnabled = idle;
        MsPauseBtn.IsEnabled = running || paused;
        MsPauseBtn.Content = paused ? "继续" : "暂停";
        MsStopBtn.IsEnabled = !idle;
        MsGrid.IsReadOnly = !idle;
        MsLoopChk.IsEnabled = idle;
        _msUiBusy = !idle;
        if (running)
            MsStatusText.Text = $"发送中… 已发 {_msEngine.SendsCompleted} 条，循环 {_msEngine.LoopsCompleted} 轮";
        else if (paused)
            MsStatusText.Text = $"已暂停（已发 {_msEngine.SendsCompleted} 条）— 点“继续”接着发";
        else
            MsStatusText.Text = "勾选要发的条目，启动后按顺序发送；当前条目会高亮。";
    }

    private void MsAdd_Click(object sender, RoutedEventArgs e)
    {
        if (_msUiBusy) return;
        var row = new MultiSendRow(new MultiSendItem { Name = $"条目 {_msRows.Count + 1}", Content = "", DelayMs = 100 });
        int at = MsGrid.SelectedIndex >= 0 ? MsGrid.SelectedIndex + 1 : _msRows.Count;
        _msRows.Insert(at, row);
        ReindexMs();
        MsGrid.SelectedItem = row;
        MsGrid.ScrollIntoView(row);
    }

    private void MsRemove_Click(object sender, RoutedEventArgs e)
    {
        if (_msUiBusy || MsGrid.SelectedItem is not MultiSendRow r) return;
        int i = _msRows.IndexOf(r);
        _msRows.Remove(r);
        ReindexMs();
        if (_msRows.Count > 0) MsGrid.SelectedIndex = Math.Min(i, _msRows.Count - 1);
    }

    private void MsUp_Click(object sender, RoutedEventArgs e) => MoveMs(-1);
    private void MsDown_Click(object sender, RoutedEventArgs e) => MoveMs(1);

    private void MoveMs(int d)
    {
        if (_msUiBusy || MsGrid.SelectedItem is not MultiSendRow r) return;
        int i = _msRows.IndexOf(r), j = i + d;
        if (j < 0 || j >= _msRows.Count) return;
        _msRows.Move(i, j);
        ReindexMs();
        MsGrid.SelectedItem = r;
    }

    private void MsSelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (_msUiBusy) return;
        foreach (var r in _msRows) r.Enabled = true;
    }

    private void MsSelectNone_Click(object sender, RoutedEventArgs e)
    {
        if (_msUiBusy) return;
        foreach (var r in _msRows) r.Enabled = false;
    }

    private async void MsStart_Click(object sender, RoutedEventArgs e)
    {
        if (_msEngine.IsBusy) return;
        if (_conn == null)
        {
            SetStatus(NotOpenMessage, true);
            return;
        }
        CommitMsGrid();
        var items = _msRows.Select(r => r.Item.Clone()).ToList();
        if (!items.Any(x => x.Enabled && !string.IsNullOrEmpty(x.Content)))
        {
            SetStatus("请至少勾选一条有内容的发送条目", true);
            return;
        }
        // 与定时发送互斥
        StopTimedSend(null);
        TimedChk.IsChecked = false;

        bool loop = MsLoopChk.IsChecked == true;
        UpdateMsButtons(MultiSendState.Running);
        SetStatus(loop ? "多项发送已启动（循环）" : "多项发送已启动");
        try
        {
            await _msEngine.StartAsync(items, loop, item =>
            {
                string? err = null;
                bool ok = Dispatcher.Invoke(() =>
                {
                    var kind = SessionCore.ParseChecksum(item.Checksum);
                    // 用条目自己的校验；行尾 / 转义跟随主发送区
                    if (!TryBuildPayload(item.Content, item.Hex, EscapeChk.IsChecked == true, kind, true,
                            out byte[] data, out int ms, out int ml, out err))
                        return false;
                    if (data.Length == 0) { err = "发送内容为空"; return false; }
                    var c = _conn;
                    if (c == null) { err = NotOpenMessage; return false; }
                    if (!c.Send(data, ms, ml)) { err = "发送队列已满或连接正在关闭"; return false; }
                    return true;
                });
                return ok ? null : (err ?? "发送失败");
            });
        }
        catch (Exception ex)
        {
            SetStatus("多项发送启动失败：" + ex.Message, true);
            UpdateMsButtons(MultiSendState.Idle);
        }
    }

    private void MsPause_Click(object sender, RoutedEventArgs e)
    {
        if (_msEngine.State == MultiSendState.Paused) _msEngine.Resume();
        else if (_msEngine.State == MultiSendState.Running) _msEngine.Pause();
    }

    private void MsStop_Click(object sender, RoutedEventArgs e) => _msEngine.Stop();
}
