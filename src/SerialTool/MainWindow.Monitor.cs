using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SerialTool.Features;

namespace SerialTool;

/// <summary>
/// v1.3：多端口监视。可同时打开 N 个串口（各自参数、独立打开/关闭），
/// 收发数据合并显示在一个列表里（端口名前缀 + 每个端口一种颜色），可选择端口发送。
/// </summary>
public partial class MainWindow
{
    private static readonly string[] MonitorFormats = { "8N1", "8E1", "8O1", "8N2", "7E1", "7O1", "7N1" };
    private static readonly string[] MonitorColors =
        { "#2563EB", "#DC2626", "#16A34A", "#D97706", "#7C3AED", "#0891B2", "#DB2777", "#65A30D" };
    private const int MaxMonitorLines = 5000;

    private sealed class MonitorPort
    {
        public MonitorPortSettings Settings = new();
        public SerialSession? Session;
        public readonly ConcurrentQueue<DataChunk> Queue = new();
        public LineAssembler? Rx, Tx;
        public long RxBytes, TxBytes;
        public bool Busy;
        public Brush Brush = Brushes.Gray;
        public int Id;

        // UI
        public Border Swatch = null!;
        public TextBox AliasBox = null!;
        public ComboBox PortBox = null!;
        public ComboBox BaudBox = null!;
        public ComboBox FormatBox = null!;
        public CheckBox HexChk = null!;
        public Button OpenButton = null!;
        public TextBlock CountText = null!;

        public string Label => string.IsNullOrWhiteSpace(Settings.Alias) ? Settings.PortName : Settings.Alias.Trim();
        public override string ToString() => string.IsNullOrWhiteSpace(Settings.Alias) ? Settings.PortName : $"{Settings.Alias}（{Settings.PortName}）";
    }

    private readonly List<MonitorPort> _monitors = new();
    private readonly ObservableCollection<ProtocolLine> _monitorLines = new();
    private int _nextMonitorId;

    private void InitMonitors()
    {
        MonitorList.ItemsSource = _monitorLines;
        foreach (var s in _settings.MonitorPorts) AddMonitorRow(s);
        RefreshMonitorTargets();
    }

    private void CollectMonitorSettings()
    {
        foreach (var m in _monitors) ReadMonitorRow(m);
        _settings.MonitorPorts = _monitors.Select(m => m.Settings).ToList();
    }

    private void ReadMonitorRow(MonitorPort m)
    {
        m.Settings.Alias = m.AliasBox.Text.Trim();
        string port = (m.PortBox.SelectedItem as string) ?? m.PortBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(port)) m.Settings.PortName = port.Trim();
        if (int.TryParse(m.BaudBox.Text.Trim(), out int b) && b > 0) m.Settings.BaudRate = b;
        if (m.FormatBox.SelectedItem is string f) m.Settings.Format = f;
        m.Settings.Hex = m.HexChk.IsChecked == true;
    }

    private void MonitorAdd_Click(object sender, RoutedEventArgs e)
    {
        string used = string.Join(",", _monitors.Select(m => m.Settings.PortName));
        string? port = _lastPortNames.FirstOrDefault(n => !_monitors.Any(m => m.Settings.PortName.Equals(n, StringComparison.OrdinalIgnoreCase))
                                                          && !(SerialConn?.PortName.Equals(n, StringComparison.OrdinalIgnoreCase) ?? false));
        var s = new MonitorPortSettings
        {
            PortName = port ?? _lastPortNames.FirstOrDefault() ?? "",
            BaudRate = 115200,
            Color = MonitorColors[_monitors.Count % MonitorColors.Length],
        };
        AddMonitorRow(s);
        RefreshMonitorTargets();
        _ = used;
    }

    private void AddMonitorRow(MonitorPortSettings s)
    {
        var m = new MonitorPort { Settings = s, Id = ++_nextMonitorId };
        m.Brush = MakeMonitorBrush(s.Color);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        m.Swatch = new Border
        {
            Width = 16, Height = 16, CornerRadius = new CornerRadius(3), Background = m.Brush, Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, ToolTip = "点击切换颜色",
        };
        m.Swatch.MouseLeftButtonUp += (_, _) =>
        {
            int idx = Array.IndexOf(MonitorColors, m.Settings.Color);
            m.Settings.Color = MonitorColors[(idx + 1) % MonitorColors.Length];
            m.Brush = MakeMonitorBrush(m.Settings.Color);
            m.Swatch.Background = m.Brush;
        };
        row.Children.Add(m.Swatch);

        m.AliasBox = new TextBox { Width = 70, Text = s.Alias, ToolTip = "别名（显示为前缀，留空用端口名）", Margin = new Thickness(0, 2, 6, 2) };
        m.AliasBox.LostFocus += (_, _) => { m.Settings.Alias = m.AliasBox.Text.Trim(); RefreshMonitorTargets(); };
        row.Children.Add(Label("别名"));
        row.Children.Add(m.AliasBox);

        m.PortBox = new ComboBox { Width = 96, IsEditable = true, ItemsSource = _lastPortNames, Text = s.PortName, Margin = new Thickness(0, 2, 6, 2) };
        row.Children.Add(Label("端口"));
        row.Children.Add(m.PortBox);

        m.BaudBox = new ComboBox { Width = 92, IsEditable = true, ItemsSource = BaudPresets, Text = s.BaudRate.ToString(), Margin = new Thickness(0, 2, 6, 2) };
        row.Children.Add(Label("波特率"));
        row.Children.Add(m.BaudBox);

        m.FormatBox = new ComboBox { Width = 62, ItemsSource = MonitorFormats, SelectedItem = MonitorFormats.Contains(s.Format) ? s.Format : "8N1",
            Margin = new Thickness(0, 2, 6, 2), ToolTip = "数据位 / 校验 / 停止位" };
        row.Children.Add(m.FormatBox);

        m.HexChk = new CheckBox { Content = "HEX", IsChecked = s.Hex, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        m.HexChk.Click += (_, _) =>
        {
            m.Settings.Hex = m.HexChk.IsChecked == true;
            FlushMonitorAssemblers(m, true);
            ResetMonitorAssemblers(m);
        };
        row.Children.Add(m.HexChk);

        m.OpenButton = new Button { Content = "打开", MinWidth = 56, Style = (Style)FindResource("AccentButton"), Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 2, 8, 2) };
        m.OpenButton.Click += async (_, _) => await ToggleMonitorAsync(m);
        row.Children.Add(m.OpenButton);

        m.CountText = new TextBlock { Text = "RX 0 / TX 0", MinWidth = 130, VerticalAlignment = VerticalAlignment.Center, FontFamily = (FontFamily)FindResource("MonoFont") };
        m.CountText.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        row.Children.Add(m.CountText);

        var del = new Button { Content = "删除", Style = (Style)FindResource("SmallButton"), Margin = new Thickness(4, 2, 0, 2) };
        del.Click += async (_, _) =>
        {
            if (m.Session != null) await CloseMonitorAsync(m);
            _monitors.Remove(m);
            MonitorPortsPanel.Children.Remove(row);
            RefreshMonitorTargets();
        };
        row.Children.Add(del);

        _monitors.Add(m);
        MonitorPortsPanel.Children.Add(row);
        ResetMonitorAssemblers(m);
    }

    private TextBlock Label(string text)
    {
        var tb = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "FieldLabelBrush");
        return tb;
    }

    private static Brush MakeMonitorBrush(string color)
    {
        var c = HighlightStyles.ParseColor(color, Color.FromRgb(0x25, 0x63, 0xEB));
        if (ThemeManager.IsDark)
        {
            // 深色背景下提亮
            c = Color.FromRgb((byte)(c.R + (255 - c.R) * 0.35), (byte)(c.G + (255 - c.G) * 0.35), (byte)(c.B + (255 - c.B) * 0.35));
        }
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private void RefreshMonitorColors()
    {
        foreach (var m in _monitors)
        {
            m.Brush = MakeMonitorBrush(m.Settings.Color);
            m.Swatch.Background = m.Brush;
        }
    }

    private void RefreshMonitorPortNames(string[] names)
    {
        foreach (var m in _monitors)
        {
            string text = m.PortBox.Text;
            m.PortBox.ItemsSource = names;
            m.PortBox.Text = text;
        }
    }

    private void RefreshMonitorTargets()
    {
        if (MonitorTargetCombo == null) return;
        var keep = MonitorTargetCombo.SelectedItem as MonitorPort;
        MonitorTargetCombo.ItemsSource = null;
        MonitorTargetCombo.ItemsSource = _monitors.ToList();
        MonitorTargetCombo.SelectedItem = keep != null && _monitors.Contains(keep) ? keep : _monitors.FirstOrDefault();
    }

    private void ResetMonitorAssemblers(MonitorPort m)
    {
        var enc = CurrentEncoding();
        bool hex = m.Settings.Hex;
        m.Rx = new LineAssembler(enc, false, hex ? 64 : 4096);
        m.Tx = new LineAssembler(enc, true, hex ? 64 : 4096);
    }

    private static bool TryParseFormat(string f, out int dataBits, out Parity parity, out StopBits stop)
    {
        dataBits = 8;
        parity = Parity.None;
        stop = StopBits.One;
        if (f.Length != 3 || !char.IsDigit(f[0])) return false;
        dataBits = f[0] - '0';
        parity = f[1] switch { 'E' => Parity.Even, 'O' => Parity.Odd, _ => Parity.None };
        stop = f[2] == '2' ? StopBits.Two : StopBits.One;
        return true;
    }

    private async Task ToggleMonitorAsync(MonitorPort m)
    {
        if (m.Busy) return;
        if (m.Session != null)
        {
            await CloseMonitorAsync(m);
            return;
        }
        ReadMonitorRow(m);
        RefreshMonitorTargets();
        if (string.IsNullOrWhiteSpace(m.Settings.PortName))
        {
            SetStatus("多端口：请选择端口", true);
            return;
        }
        if (SerialConn != null && SerialConn.PortName.Equals(m.Settings.PortName, StringComparison.OrdinalIgnoreCase))
        {
            SetStatus($"{m.Settings.PortName} 已作为主连接打开，不能在多端口里重复打开", true);
            return;
        }
        if (_monitors.Any(o => o != m && o.Session != null && o.Session.PortName.Equals(m.Settings.PortName, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus($"{m.Settings.PortName} 已在多端口列表中打开", true);
            return;
        }
        TryParseFormat(m.Settings.Format, out int db, out var parity, out var stop);
        var port = new SerialPort(m.Settings.PortName)
        {
            BaudRate = m.Settings.BaudRate, DataBits = db, Parity = parity, StopBits = stop,
            ReadTimeout = 100, WriteTimeout = 3000, ReadBufferSize = 1 << 18, ParityReplace = 0, DiscardNull = false,
        };
        m.Busy = true;
        m.OpenButton.Content = "…";
        try
        {
            await Task.Run(port.Open);
        }
        catch (Exception ex)
        {
            try { port.Dispose(); } catch { /* ignore */ }
            m.Busy = false;
            UpdateMonitorRow(m);
            SetStatus(DescribeOpenError(ex, m.Settings.PortName), true);
            return;
        }
        var session = new SerialSession(port, m.Queue);
        session.Faulted += msg => Dispatcher.BeginInvoke(async () =>
        {
            if (m.Session != session) return;
            await CloseMonitorAsync(m);
            AddMonitorInfo(m, "已断开：" + msg);
            SetStatus($"[{m.Label}] {msg}", true);
        });
        session.Warning += msg => Dispatcher.BeginInvoke(() => SetStatus($"[{m.Label}] {msg}", true));
        m.Session = session;
        session.Start();
        m.Busy = false;
        ResetMonitorAssemblers(m);
        UpdateMonitorRow(m);
        AddMonitorInfo(m, $"已打开 {m.Settings.PortName} {m.Settings.BaudRate} {m.Settings.Format}");
    }

    private async Task CloseMonitorAsync(MonitorPort m)
    {
        var s = m.Session;
        if (s == null) return;
        m.Session = null;
        m.Busy = true;
        UpdateMonitorRow(m);
        await Task.WhenAny(Task.Run(s.Stop), Task.Delay(4000));
        m.Busy = false;
        FlushMonitorAssemblers(m, true);
        UpdateMonitorRow(m);
    }

    private void CloseAllMonitors(bool sync)
    {
        foreach (var m in _monitors)
        {
            var s = m.Session;
            m.Session = null;
            if (s == null) continue;
            if (sync) { try { Task.Run(s.Stop).Wait(2000); } catch { /* ignore */ } }
            else _ = Task.Run(s.Stop);
            if (!sync) UpdateMonitorRow(m);
        }
    }

    private void UpdateMonitorRow(MonitorPort m)
    {
        bool open = m.Session != null;
        m.OpenButton.Content = m.Busy ? "…" : open ? "关闭" : "打开";
        m.OpenButton.Style = (Style)FindResource(open ? "DangerButton" : "AccentButton");
        m.OpenButton.IsEnabled = !m.Busy;
        m.PortBox.IsEnabled = m.BaudBox.IsEnabled = m.FormatBox.IsEnabled = !open && !m.Busy;
    }

    private async void MonitorOpenAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var m in _monitors.ToList())
            if (m.Session == null) await ToggleMonitorAsync(m);
    }

    private async void MonitorCloseAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var m in _monitors.ToList())
            if (m.Session != null) await CloseMonitorAsync(m);
    }

    private void MonitorClear_Click(object sender, RoutedEventArgs e) => _monitorLines.Clear();

    private void UpdateMonitorCounters()
    {
        foreach (var m in _monitors)
            m.CountText.Text = $"RX {m.RxBytes:N0} / TX {m.TxBytes:N0}";
    }

    /// <summary>UI 定时器：取出各端口数据，拆行后合并显示。</summary>
    private void FlushMonitors()
    {
        if (_monitors.Count == 0) return;
        bool added = false;
        var now = DateTime.Now;
        foreach (var m in _monitors)
        {
            int guard = 0;
            while (guard++ < 2000 && m.Queue.TryDequeue(out var c))
            {
                if (c.IsTx) m.TxBytes += c.Data.Length;
                else m.RxBytes += c.Data.Length;
                var asm = c.IsTx ? m.Tx : m.Rx;
                if (asm == null) continue;
                foreach (var rec in asm.Push(c.Data, c.Time)) added |= AddMonitorLine(m, rec);
            }
            added |= FlushMonitorAssemblers(m, false, now);
        }
        if (added && AutoScrollChk.IsChecked == true && MonitorList.IsVisible && _monitorLines.Count > 0)
            MonitorList.ScrollIntoView(_monitorLines[^1]);
    }

    private bool FlushMonitorAssemblers(MonitorPort m, bool all, DateTime? now = null)
    {
        bool added = false;
        var t = now ?? DateTime.Now;
        foreach (var asm in new[] { m.Rx, m.Tx })
        {
            if (asm == null) continue;
            var rec = all ? asm.FlushAll() : asm.Flush(t, m.Settings.Hex ? Math.Max(20, _formatter.FrameGapMs) : 100);
            if (rec != null) added |= AddMonitorLine(m, rec);
        }
        return added;
    }

    private bool AddMonitorLine(MonitorPort m, LineRecord rec)
    {
        if (rec.IsTx && MonitorTxChk.IsChecked != true) return false;
        string body = m.Settings.Hex ? Checksum.ToHex(rec.Bytes) : CsvExport.CleanText(rec.Text).Replace("\\t", "\t");
        string time = MonitorTimeChk.IsChecked == true ? $"[{rec.Time:HH:mm:ss.fff}] " : "";
        string text = $"{time}[{m.Label}] {(rec.IsTx ? "发→ " : "收← ")}{body}";
        AddMonitorItem(new ProtocolLine { Text = text, IsTx = rec.IsTx, Time = rec.Time, Foreground = m.Brush, Tone = rec.IsTx ? LineTone.Tx : LineTone.Plain });
        return true;
    }

    private void AddMonitorInfo(MonitorPort m, string message)
    {
        AddMonitorItem(new ProtocolLine { Text = $"[{DateTime.Now:HH:mm:ss.fff}] [{m.Label}] —— {message}", Tone = LineTone.Meta, Time = DateTime.Now });
    }

    private void AddMonitorItem(ProtocolLine line)
    {
        _monitorLines.Add(line);
        int over = _monitorLines.Count - MaxMonitorLines;
        if (over > 0)
            while (over-- > 0) _monitorLines.RemoveAt(0);
    }

    private void MonitorSend_Click(object sender, RoutedEventArgs e) => MonitorSend();

    private void MonitorSendBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            MonitorSend();
            e.Handled = true;
        }
    }

    private void MonitorSend()
    {
        if (MonitorTargetCombo.SelectedItem is not MonitorPort m)
        {
            SetStatus("多端口：请先添加并选择目标端口", true);
            return;
        }
        if (m.Session == null)
        {
            SetStatus($"多端口：{m.Label} 未打开", true);
            return;
        }
        if (!TryBuildPayload(MonitorSendBox.Text, MonitorHexChk.IsChecked == true, null, null, true,
                out byte[] data, out int ms, out int ml, out string? err))
        {
            SetStatus(err ?? "发送内容无效", true);
            return;
        }
        if (data.Length == 0)
        {
            SetStatus("发送内容为空", true);
            return;
        }
        if (!m.Session.Send(data, ms, ml)) SetStatus("发送队列已满", true);
        else SetStatus($"已发送到 {m.Label}（{data.Length} 字节）");
    }

    private void MonitorList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            MonitorCopy_Click(sender, e);
            e.Handled = true;
        }
    }

    private void MonitorCopy_Click(object sender, RoutedEventArgs e)
    {
        var sel = MonitorList.SelectedItems.Cast<ProtocolLine>().OrderBy(l => _monitorLines.IndexOf(l)).ToList();
        if (sel.Count == 0) { SetStatus("请先选择要复制的行", true); return; }
        try
        {
            Clipboard.SetText(string.Join("\r\n", sel.Select(l => l.Text)));
            SetStatus($"已复制 {sel.Count} 行");
        }
        catch (Exception ex)
        {
            SetStatus("复制失败：" + ex.Message, true);
        }
    }
}
