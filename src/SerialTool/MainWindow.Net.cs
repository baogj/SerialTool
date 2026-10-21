using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SerialTool.Features;

namespace SerialTool;

/// <summary>v1.3：连接类型（串口 / TCP / UDP）、TCP 服务器客户端、串口转发、CSV 记录、数值曲线。</summary>
public partial class MainWindow
{
    private static readonly List<Opt<ConnectionKind>> ConnTypeOptions = new()
    {
        new("串口", ConnectionKind.Serial), new("TCP 客户端", ConnectionKind.TcpClient),
        new("TCP 服务器", ConnectionKind.TcpServer), new("UDP", ConnectionKind.Udp),
    };

    private static readonly List<Opt<ConnectionKind>> BridgeTypeOptions = new()
    {
        new("TCP 服务器（等待网络端连入）", ConnectionKind.TcpServer),
        new("TCP 客户端（连接到远程）", ConnectionKind.TcpClient),
        new("UDP", ConnectionKind.Udp),
    };

    private static readonly int[] CurveWindows = { 100, 200, 500, 1000, 2000, 5000, 10000 };
    private const int MaxLineRecords = 200_000;

    private volatile SerialBridge? _bridge;
    private bool _bridgeBusy;
    private readonly List<LineRecord> _lineRecords = new();
    private LineAssembler? _rxRecorder, _txRecorder;
    private readonly CurveData _curve = new();
    private CurveElement? _curveElement;
    private bool _curveDirty;

    private string NotOpenMessage => CurrentConnKind() == ConnectionKind.Serial ? "串口未打开" : "网络连接未建立";

    private ConnectionKind CurrentConnKind() =>
        (ConnTypeCombo?.SelectedItem as Opt<ConnectionKind>)?.Value ?? ConnectionKind.Serial;

    private static string OpenButtonText(ConnectionKind kind, bool open) => kind switch
    {
        ConnectionKind.TcpClient => open ? "断开连接" : "连接",
        ConnectionKind.TcpServer => open ? "停止监听" : "开始监听",
        ConnectionKind.Udp => open ? "关闭 UDP" : "打开 UDP",
        _ => open ? "关闭串口" : "打开串口",
    };

    // ======================================================================
    // 初始化 / 设置
    // ======================================================================

    private void InitControlsV13()
    {
        var s = _settings;
        ConnTypeCombo.ItemsSource = ConnTypeOptions;
        ConnTypeCombo.SelectedItem = ConnTypeOptions.FirstOrDefault(o => o.Value.ToString() == s.ConnType) ?? ConnTypeOptions[0];
        RemoteHostBox.Text = s.RemoteHost;
        RemotePortBox.Text = s.RemotePort.ToString();
        LocalPortBox.Text = s.LocalPort.ToString();
        BridgeTypeCombo.ItemsSource = BridgeTypeOptions;
        BridgeTypeCombo.SelectedItem = BridgeTypeOptions.FirstOrDefault(o => o.Value.ToString() == s.BridgeType) ?? BridgeTypeOptions[0];
        BridgeHostBox.Text = s.BridgeHost;
        BridgePortBox.Text = s.BridgePort.ToString();
        BridgeLocalPortBox.Text = s.BridgeLocalPort.ToString();
        CurveWindowCombo.ItemsSource = CurveWindows;
        CurveWindowCombo.SelectedItem = CurveWindows.Contains(s.CurveWindow) ? s.CurveWindow : 500;
        FilterModeCombo.ItemsSource = FilterModes;
        FilterModeCombo.SelectedIndex = 0;
        InitModbusControls();
        UpdateConnTypeUi();
        UpdateBridgeTypeUi();
    }

    private void CollectSettingsV13()
    {
        var s = _settings;
        s.ConnType = CurrentConnKind().ToString();
        s.RemoteHost = RemoteHostBox.Text.Trim();
        if (int.TryParse(RemotePortBox.Text.Trim(), out int rp) && rp > 0 && rp <= 65535) s.RemotePort = rp;
        if (int.TryParse(LocalPortBox.Text.Trim(), out int lp) && lp >= 0 && lp <= 65535) s.LocalPort = lp;
        s.BridgeType = ((BridgeTypeCombo.SelectedItem as Opt<ConnectionKind>)?.Value ?? ConnectionKind.TcpServer).ToString();
        s.BridgeHost = BridgeHostBox.Text.Trim();
        if (int.TryParse(BridgePortBox.Text.Trim(), out int bp) && bp > 0 && bp <= 65535) s.BridgePort = bp;
        if (int.TryParse(BridgeLocalPortBox.Text.Trim(), out int blp) && blp >= 0 && blp <= 65535) s.BridgeLocalPort = blp;
        if (CurveWindowCombo.SelectedItem is int cw) s.CurveWindow = cw;
        CollectModbusSettings();
        CollectMonitorSettings();
    }

    /// <summary>Window_Loaded 末尾调用。</summary>
    private void InitV13()
    {
        _curveElement = new CurveElement(_curve) { WindowSize = (int)(CurveWindowCombo.SelectedItem ?? 500) };
        CurveHost.Child = _curveElement;
        ResetRecorders();
        EncodingCombo.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            ResetRecorders();
        };
        _statsTimer.Tick += (_, _) =>
        {
            UpdateBridgeUi();
            UpdateMonitorCounters();
            if (SearchBar.Visibility == Visibility.Visible) UpdateSearchCount();
        };
        InitMonitors();
        InitModbusRuntime();
    }

    private void ShutdownV13()
    {
        StopModbusPoll();
        var b = _bridge;
        _bridge = null;
        if (b != null)
        {
            try { Task.Run(b.Stop).Wait(2000); } catch { /* ignore */ }
        }
        CloseAllMonitors(true);
    }

    // ======================================================================
    // 连接类型
    // ======================================================================

    private void ConnTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateConnTypeUi();
        if (_loading) return;
        UpdateOpenState();
    }

    private void UpdateConnTypeUi()
    {
        if (SerialPanel == null || NetPanel == null) return;
        var kind = CurrentConnKind();
        bool serial = kind == ConnectionKind.Serial;
        SerialPanel.Visibility = serial ? Visibility.Visible : Visibility.Collapsed;
        NetPanel.Visibility = serial ? Visibility.Collapsed : Visibility.Visible;
        bool remote = kind is ConnectionKind.TcpClient or ConnectionKind.Udp;
        bool local = kind is ConnectionKind.TcpServer or ConnectionKind.Udp;
        var rv = remote ? Visibility.Visible : Visibility.Collapsed;
        var lv = local ? Visibility.Visible : Visibility.Collapsed;
        RemoteHostLabel.Visibility = RemoteHostBox.Visibility = rv;
        RemotePortLabel.Visibility = RemotePortBox.Visibility = rv;
        LocalPortLabel.Visibility = LocalPortBox.Visibility = lv;
        ServerPanel.Visibility = kind == ConnectionKind.TcpServer ? Visibility.Visible : Visibility.Collapsed;
        if (OpenBtn != null && _conn == null) OpenBtn.Content = OpenButtonText(kind, false);
    }

    private async Task OpenNetworkAsync(ConnectionKind kind)
    {
        if (_busy || _conn != null) return;
        string host = RemoteHostBox.Text.Trim();
        int rport = 0, lport = 0;
        string? err = null;
        if (kind is ConnectionKind.TcpClient or ConnectionKind.Udp)
        {
            bool needRemote = kind == ConnectionKind.TcpClient || host.Length > 0;
            if (kind == ConnectionKind.TcpClient && host.Length == 0) err = "请填写远程主机";
            else if (needRemote && (!int.TryParse(RemotePortBox.Text.Trim(), out rport) || rport < 1 || rport > 65535))
                err = "远程端口需为 1~65535";
        }
        if (err == null && kind is ConnectionKind.TcpServer or ConnectionKind.Udp)
        {
            if (!int.TryParse(LocalPortBox.Text.Trim(), out lport) || lport < 0 || lport > 65535)
                err = "本地端口需为 0~65535";
        }
        if (err != null)
        {
            SetStatus(err, true);
            MessageBox.Show(this, err, "连接", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _busy = true;
        UpdateOpenState();
        string what = kind switch
        {
            ConnectionKind.TcpClient => $"正在连接 {host}:{rport} …",
            ConnectionKind.TcpServer => $"正在监听端口 {lport} …",
            _ => $"正在打开 UDP 端口 {lport} …",
        };
        SetStatus(what);
        IConnection conn;
        try
        {
            conn = kind switch
            {
                ConnectionKind.TcpClient => await TcpClientConnection.ConnectAsync(host, rport, _queue),
                ConnectionKind.TcpServer => TcpServerConnection.Listen(lport, _queue),
                _ => await UdpConnection.OpenAsync(lport, host, rport, _queue),
            };
        }
        catch (Exception ex)
        {
            _busy = false;
            UpdateOpenState();
            string msg = $"{(kind == ConnectionKind.TcpServer ? "监听" : "连接")}失败：{NetUtil.Describe(ex)}";
            SetStatus(msg, true);
            MessageBox.Show(this, msg, "网络连接失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_closingWindow)
        {
            try { conn.Stop(); } catch { /* ignore */ }
            return;
        }
        AttachConnection(conn);
        _busy = false;
        UpdateOpenState();
        SetStatus(kind switch
        {
            ConnectionKind.TcpClient => $"已连接 {host}:{rport}",
            ConnectionKind.TcpServer => $"TCP 服务器已在端口 {((TcpServerConnection)conn).LocalPort} 监听，等待客户端连接",
            _ => $"UDP 已绑定本地端口 {((UdpConnection)conn).LocalPort}" + (rport > 0 ? $"，发往 {host}:{rport}" : "（未设置远程地址：回复最后一个来包地址）"),
        });
    }

    /// <summary>挂接新主连接的事件并启动。</summary>
    private void AttachConnection(IConnection c)
    {
        c.Faulted += msg => Dispatcher.BeginInvoke(() =>
        {
            if (_conn == c) HandleUnexpectedDisconnect(msg);
        });
        c.Warning += msg => Dispatcher.BeginInvoke(() => SetStatus(msg, true));
        c.Info += msg => Dispatcher.BeginInvoke(() => SetStatus(msg));
        c.Received += OnConnReceived;
        if (c is TcpServerConnection srv)
        {
            srv.ClientsChanged += () => Dispatcher.BeginInvoke(RefreshServerClients);
            srv.SendTargets = ServerSendSelectedChk.IsChecked == true ? Array.Empty<int>() : null;
        }
        _conn = c;
        c.Start();
        ApplyServerTargets();
    }

    /// <summary>主连接收到数据（后台线程）：交给 Modbus 主站与串口转发。</summary>
    private void OnConnReceived(byte[] data)
    {
        _mbWaiter.Feed(data);
        _bridge?.FromLocal(data);
    }

    // ---------- TCP 服务器客户端列表 ----------

    private void RefreshServerClients()
    {
        if (ServerClientList == null) return;
        if (_conn is not TcpServerConnection srv)
        {
            ServerClientList.ItemsSource = null;
            ServerClientsTitle.Text = "已连接客户端（0）";
            return;
        }
        var selected = ServerClientList.SelectedItems.Cast<TcpServerConnection.ClientInfo>().Select(c => c.Id).ToHashSet();
        var clients = srv.Clients;
        _syncingClients = true;
        try
        {
            ServerClientList.ItemsSource = clients;
            foreach (var c in clients)
                if (selected.Contains(c.Id)) ServerClientList.SelectedItems.Add(c);
        }
        finally
        {
            _syncingClients = false;
        }
        ServerClientsTitle.Text = $"已连接客户端（{clients.Count}）";
        ApplyServerTargets();
    }

    private bool _syncingClients;

    private void ServerClientList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingClients) return;
        ApplyServerTargets();
    }

    private void ServerSendSelectedChk_Click(object sender, RoutedEventArgs e) => ApplyServerTargets();

    private void ApplyServerTargets()
    {
        if (_conn is not TcpServerConnection srv) return;
        srv.SendTargets = ServerSendSelectedChk.IsChecked == true
            ? ServerClientList.SelectedItems.Cast<TcpServerConnection.ClientInfo>().Select(c => c.Id).ToArray()
            : null;
    }

    private void DisconnectClients_Click(object sender, RoutedEventArgs e)
    {
        if (_conn is not TcpServerConnection srv) return;
        var ids = ServerClientList.SelectedItems.Cast<TcpServerConnection.ClientInfo>().Select(c => c.Id).ToList();
        if (ids.Count == 0)
        {
            SetStatus("请先在列表中选择要断开的客户端", true);
            return;
        }
        foreach (int id in ids) srv.Disconnect(id);
    }

    // ======================================================================
    // 串口转发
    // ======================================================================

    private void BridgeType_Changed(object sender, SelectionChangedEventArgs e) => UpdateBridgeTypeUi();

    private ConnectionKind BridgeKind => (BridgeTypeCombo.SelectedItem as Opt<ConnectionKind>)?.Value ?? ConnectionKind.TcpServer;

    private void UpdateBridgeTypeUi()
    {
        if (BridgeHostBox == null) return;
        var k = BridgeKind;
        bool running = _bridge != null || _bridgeBusy;
        BridgeHostBox.IsEnabled = !running && k != ConnectionKind.TcpServer;
        BridgePortBox.IsEnabled = !running && k != ConnectionKind.TcpServer;
        BridgeLocalPortBox.IsEnabled = !running && k != ConnectionKind.TcpClient;
        BridgeTypeCombo.IsEnabled = !running;
        BridgeBtn.Content = _bridgeBusy ? "请稍候…" : running ? "停止转发" : "启动转发";
        BridgeBtn.Style = (Style)FindResource(running ? "DangerButton" : "AccentButton");
        BridgeBtn.IsEnabled = !_bridgeBusy;
        BridgeDot.Fill = _bridge != null ? OkBrush : IdleBrush;
    }

    private async void BridgeBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_bridgeBusy) return;
        var existing = _bridge;
        if (existing != null)
        {
            _bridge = null;
            _bridgeBusy = true;
            UpdateBridgeTypeUi();
            await Task.Run(existing.Stop);
            _bridgeBusy = false;
            UpdateBridgeTypeUi();
            BridgeStatusText.Text = "已停止";
            SetStatus("串口转发已停止");
            return;
        }

        var kind = BridgeKind;
        string host = BridgeHostBox.Text.Trim();
        int rport = 0, lport = 0;
        if (kind != ConnectionKind.TcpServer &&
            (kind == ConnectionKind.TcpClient || host.Length > 0) &&
            (!int.TryParse(BridgePortBox.Text.Trim(), out rport) || rport < 1 || rport > 65535))
        {
            SetStatus("转发：远程端口需为 1~65535", true);
            return;
        }
        if (kind == ConnectionKind.TcpClient && host.Length == 0)
        {
            SetStatus("转发：请填写远程主机", true);
            return;
        }
        if (kind != ConnectionKind.TcpClient &&
            (!int.TryParse(BridgeLocalPortBox.Text.Trim(), out lport) || lport < 0 || lport > 65535))
        {
            SetStatus("转发：本地端口需为 0~65535", true);
            return;
        }

        _bridgeBusy = true;
        UpdateBridgeTypeUi();
        BridgeStatusText.Text = "正在启动…";
        try
        {
            IConnection net = kind switch
            {
                ConnectionKind.TcpClient => await TcpClientConnection.ConnectAsync(host, rport, null),
                ConnectionKind.TcpServer => TcpServerConnection.Listen(lport, null),
                _ => await UdpConnection.OpenAsync(lport, host, rport, null),
            };
            var bridge = new SerialBridge(net, data => _conn?.Send(data) ?? false);
            net.Faulted += msg => Dispatcher.BeginInvoke(() =>
            {
                if (_bridge != bridge) return;
                _bridge = null;
                Task.Run(bridge.Stop);
                UpdateBridgeTypeUi();
                BridgeStatusText.Text = "已停止：" + msg;
                SetStatus("串口转发已停止：" + msg, true);
            });
            net.Info += msg => Dispatcher.BeginInvoke(() => SetStatus("[转发] " + msg));
            net.Warning += msg => Dispatcher.BeginInvoke(() => SetStatus("[转发] " + msg, true));
            bridge.Start();
            _bridge = bridge;
            BridgeStatusText.Text = "运行中：" + net.Name + (_conn == null ? "（主连接未打开，网络数据暂时丢弃）" : "");
            SetStatus($"串口转发已启动：{(_conn?.Name ?? "主连接")} ⇄ {net.Name}");
        }
        catch (Exception ex)
        {
            BridgeStatusText.Text = "启动失败：" + NetUtil.Describe(ex);
            SetStatus("串口转发启动失败：" + NetUtil.Describe(ex), true);
        }
        finally
        {
            _bridgeBusy = false;
            UpdateBridgeTypeUi();
        }
    }

    private void UpdateBridgeUi()
    {
        var b = _bridge;
        if (b == null) return;
        string extra = b.Network is TcpServerConnection srv ? $"    客户端：{srv.Clients.Count}" : "";
        BridgeCountText.Text = $"主连接 → 网络：{b.LocalToNetBytes:N0} 字节    网络 → 主连接：{b.NetToLocalBytes:N0} 字节    丢弃：{b.DroppedBytes:N0} 字节{extra}";
        BridgeStatusText.Text = "运行中：" + b.Network.Name + (_conn == null ? "（主连接未打开，网络数据暂时丢弃）" : "");
    }

    // ======================================================================
    // 行记录（CSV）与数值曲线
    // ======================================================================

    private void ResetRecorders()
    {
        var enc = CurrentEncoding();
        _rxRecorder = new LineAssembler(enc, false);
        _txRecorder = new LineAssembler(enc, true);
    }

    private int RecorderGapMs => Math.Max(50, _formatter.FrameGapMs);

    private void RecordChunk(DataChunk c)
    {
        var rec = c.IsTx ? _txRecorder : _rxRecorder;
        if (rec == null) return;
        foreach (var line in rec.Push(c.Data, c.Time, c.Source)) AddRecord(line);
    }

    private void FlushRecorders()
    {
        var now = DateTime.Now;
        var a = _rxRecorder?.Flush(now, RecorderGapMs);
        if (a != null) AddRecord(a);
        var b = _txRecorder?.Flush(now, RecorderGapMs);
        if (b != null) AddRecord(b);
        if (_curveDirty)
        {
            _curveDirty = false;
            _curveElement?.InvalidateVisual();
        }
    }

    private void AddRecord(LineRecord r)
    {
        _lineRecords.Add(r);
        if (_lineRecords.Count > MaxLineRecords) _lineRecords.RemoveRange(0, _lineRecords.Count - MaxLineRecords + 20_000);
        if (!r.IsTx && r.Text.Length > 0)
        {
            var values = NumericLineParser.Parse(r.Text);
            if (values.Count > 0 && _curve.Add(r.Time, values)) _curveDirty = true;
        }
    }

    private void ClearRecords()
    {
        _lineRecords.Clear();
        _rxRecorder?.Reset();
        _txRecorder?.Reset();
    }

    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        var a = _rxRecorder?.FlushAll();
        if (a != null) AddRecord(a);
        var b = _txRecorder?.FlushAll();
        if (b != null) AddRecord(b);
        if (_lineRecords.Count == 0)
        {
            SetStatus("还没有可导出的收发数据", true);
            return;
        }
        var dlg = new SaveFileDialog
        {
            Title = "导出收发数据为 CSV",
            FileName = $"serial_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
            Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            using (var w = new StreamWriter(dlg.FileName, false, new UTF8Encoding(true)))
                CsvExport.WriteLines(w, _lineRecords);
            SetStatus($"已导出 {_lineRecords.Count:N0} 行到 {dlg.FileName}");
        }
        catch (Exception ex)
        {
            SetStatus("导出失败：" + ex.Message, true);
            MessageBox.Show(this, ex.Message, "导出失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CurvePause_Click(object sender, RoutedEventArgs e)
    {
        _curve.Paused = CurvePauseChk.IsChecked == true;
        _curveElement?.InvalidateVisual();
    }

    private void CurveWindow_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_curveElement == null || CurveWindowCombo.SelectedItem is not int n) return;
        _curveElement.WindowSize = n;
        _curveElement.InvalidateVisual();
    }

    private void CurveClear_Click(object sender, RoutedEventArgs e)
    {
        _curve.Clear();
        _curveElement?.InvalidateVisual();
    }

    private void CurveExport_Click(object sender, RoutedEventArgs e)
    {
        if (_curve.Rows.Count == 0)
        {
            SetStatus("曲线还没有数据", true);
            return;
        }
        var dlg = new SaveFileDialog
        {
            Title = "导出曲线数据为 CSV",
            FileName = $"curve_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
            Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            using (var w = new StreamWriter(dlg.FileName, false, new UTF8Encoding(true)))
                _curve.WriteCsv(w);
            SetStatus($"已导出曲线 {_curve.Rows.Count:N0} 行、{_curve.Channels.Count} 个通道到 {dlg.FileName}");
        }
        catch (Exception ex)
        {
            SetStatus("导出失败：" + ex.Message, true);
        }
    }
}
