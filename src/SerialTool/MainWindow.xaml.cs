using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

using SerialTool.Features;

namespace SerialTool;

public partial class MainWindow : Window, Features.ScriptEngine.IHost
{
    private const int MaxDisplayChars = 1_500_000;   // 接收区超过该长度时裁剪
    private const int TrimDisplayChars = 1_000_000;  // 裁剪后保留的长度
    private const long MaxBytesPerTick = 4L * 1024 * 1024;
    private const int MaxHistory = 50;
    private const long MaxSendFileBytes = 16L * 1024 * 1024;

    internal static readonly string[] BaudPresets =
    {
        "110", "300", "600", "1200", "2400", "4800", "9600", "14400", "19200", "38400", "57600",
        "115200", "128000", "230400", "256000", "460800", "500000", "921600",
        "1000000", "1500000", "2000000", "3000000",
    };

    internal static readonly List<Opt<int>> DataBitsOptions = new()
    {
        new("5", 5), new("6", 6), new("7", 7), new("8", 8),
    };

    internal static readonly List<Opt<StopBits>> StopBitsOptions = new()
    {
        new("1", StopBits.One), new("1.5", StopBits.OnePointFive), new("2", StopBits.Two),
    };

    internal static readonly List<Opt<Parity>> ParityOptions = new()
    {
        new("无 (None)", Parity.None), new("奇 (Odd)", Parity.Odd), new("偶 (Even)", Parity.Even),
        new("标记 (Mark)", Parity.Mark), new("空格 (Space)", Parity.Space),
    };

    internal static readonly List<Opt<Handshake>> FlowOptions = new()
    {
        new("无", Handshake.None), new("RTS/CTS 硬件流控", Handshake.RequestToSend),
        new("XON/XOFF 软件流控", Handshake.XOnXOff), new("RTS/CTS + XON/XOFF", Handshake.RequestToSendXOnXOff),
    };

    internal static readonly List<Opt<string>> LineEndingOptions = new()
    {
        new("无", ""), new("\\r  (CR)", "\r"), new("\\n  (LF)", "\n"), new("\\r\\n  (CRLF)", "\r\n"),
    };

    private static readonly Brush OkBrush = Frozen(Color.FromRgb(0x22, 0xC5, 0x5E));
    private static readonly Brush IdleBrush = Frozen(Color.FromRgb(0x9C, 0xA3, 0xAF));
    private static readonly Brush StatusNormalBrush = Frozen(Color.FromRgb(0xE5, 0xE7, 0xEB));
    private static readonly Brush StatusErrorBrush = Frozen(Color.FromRgb(0xFC, 0xA5, 0xA5));

    private readonly AppSettings _settings;
    private readonly ConcurrentQueue<DataChunk> _queue = new();
    private readonly DisplayFormatter _formatter = new();
    private readonly DispatcherTimer _uiTimer;
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _sendTimer;
    private readonly StringBuilder _pauseBuffer = new();

    private IConnection? _conn;
    private SerialSession? SerialConn => _conn as SerialSession;
    private bool _busy;          // 正在打开 / 关闭
    private bool _loading = true; // 初始化期间屏蔽控件事件
    private bool _historyUpdating;
    private bool _closingWindow;
    private long _rxTotal, _txTotal, _shownRx = -1, _shownTx = -1;
    private int _displayLen;
    private StreamWriter? _logWriter;
    private string? _logPath;
    private string[] _lastPortNames = Array.Empty<string>();
    private int _refreshVersion;
    private string? _reconnectPort; // 意外断开且启用自动重连时，等待该端口重新出现

    // ---- v1.1 新功能 ----
    private readonly ThroughputMeter _meter = new();
    private readonly WaveformBuffer _waveBuf = new();
    private WaveformElement? _waveElement;
    private ObservableCollection<ProtocolLine> _protocolLines = new();
    private AtLineParser? _atParser;
    private readonly ModbusRtuParser _modbus = new(50);
    private ScriptEngine? _script;
    private readonly DispatcherTimer _statsTimer;
    private const int MaxProtocolLines = 5000;
    internal static readonly string[] ProtocolModes = { "普通", "AT", "Modbus RTU" };
    private static readonly int[] WaveWindows = { 256, 512, 1024, 2048, 4096, 8192 };
    private readonly List<ProtocolLine> _pausedLines = new();   // 协议/高亮视图暂停期间缓存的行

    // ---- v1.2：校验 / 高亮 / 自动回复 / 主题 ----
    private List<CompiledRule> _compiledRules = new();
    private List<CompiledRule> _highlightRules = new();   // 当前参与高亮的规则（已启用且有效）
    private readonly AutoReplyEngine _autoReply = new();
    private long _autoReplyCount;
    private bool _lineViewPlain;          // 普通模式下是否改用着色行视图（有高亮规则时）
    private readonly StringBuilder _openLine = new();
    private bool _hasOpenItem;            // _protocolLines 最后一项是否为未结束的行
    private bool _pendingCr;
    private bool _syncingProtocol;
    private const int MaxOpenLineChars = 2000;

    private static Brush FreezeStatic(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public MainWindow()
    {
        _settings = SettingsStore.Load();
        ThemeManager.Apply(ThemeManager.Parse(_settings.Theme)); // 先套主题，再创建控件
        InitializeComponent();
        ThemeManager.Attach(this);

        _uiTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
        _uiTimer.Tick += (_, _) => FlushQueue();
        _pollTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1000) };
        _pollTimer.Tick += (_, _) => PollPorts();
        _sendTimer = new DispatcherTimer(DispatcherPriority.Normal);
        _sendTimer.Tick += (_, _) => TimedSendTick();
        _statsTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1000) };
        _statsTimer.Tick += (_, _) => UpdateThroughputUi();

        InitControls();
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    // ======================================================================
    // 初始化 / 设置持久化
    // ======================================================================

    private void InitControls()
    {
        var s = _settings;

        BaudCombo.ItemsSource = BaudPresets;
        BaudCombo.Text = s.BaudRate.ToString();
        DataBitsCombo.ItemsSource = DataBitsOptions;
        DataBitsCombo.SelectedItem = DataBitsOptions.FirstOrDefault(o => o.Value == s.DataBits) ?? DataBitsOptions[3];
        StopBitsCombo.ItemsSource = StopBitsOptions;
        StopBitsCombo.SelectedItem = StopBitsOptions.FirstOrDefault(o => o.Value == s.StopBits) ?? StopBitsOptions[0];
        ParityCombo.ItemsSource = ParityOptions;
        ParityCombo.SelectedItem = ParityOptions.FirstOrDefault(o => o.Value == s.Parity) ?? ParityOptions[0];
        FlowCombo.ItemsSource = FlowOptions;
        FlowCombo.SelectedItem = FlowOptions.FirstOrDefault(o => o.Value == s.Handshake) ?? FlowOptions[0];
        DtrChk.IsChecked = s.Dtr;
        RtsChk.IsChecked = s.Rts;
        AutoReconnectChk.IsChecked = s.AutoReconnect;
        UpdateRtsAvailability();

        HexDisplayChk.IsChecked = s.HexDisplay;
        TimestampChk.IsChecked = s.ShowTimestamp;
        ShowTxChk.IsChecked = s.ShowTx;
        AutoScrollChk.IsChecked = s.AutoScroll;
        EncodingCombo.ItemsSource = EncodingUtil.Names;
        EncodingCombo.SelectedItem = EncodingUtil.Names.Contains(s.Encoding) ? s.Encoding : EncodingUtil.Names[0];
        FrameGapBox.Text = s.FrameGapMs.ToString();

        HexSendChk.IsChecked = s.HexSend;
        EscapeChk.IsChecked = s.Escape;
        EscapeChk.IsEnabled = !s.HexSend;
        LineEndingCombo.ItemsSource = LineEndingOptions;
        LineEndingCombo.SelectedItem = LineEndingOptions.FirstOrDefault(o => o.Value == s.LineEnding) ?? LineEndingOptions[0];
        IntervalBox.Text = s.TimerIntervalMs.ToString();
        SendBox.Text = s.SendText;

        _formatter.Encoding = CurrentEncoding();
        ApplyFormatterOptions();

        if (s.WindowWidth >= MinWidth && s.WindowHeight >= MinHeight)
        {
            Width = s.WindowWidth;
            Height = s.WindowHeight;
        }
        if (s.WindowMaximized) WindowState = WindowState.Maximized;
        if (s.SendPanelHeight >= 170) SendRow.Height = new GridLength(s.SendPanelHeight);

        ProtocolCombo.ItemsSource = ProtocolModes;
        ProtocolCombo.SelectedItem = ProtocolModes.Contains(s.ProtocolMode) ? s.ProtocolMode : ProtocolModes[0];
        WaveShowTxChk.IsChecked = s.WaveShowTx;
        WaveWindowCombo.ItemsSource = WaveWindows;
        int ww = s.WaveWindowLength <= 0 ? 1024 : s.WaveWindowLength;
        WaveWindowCombo.SelectedItem = WaveWindows.Contains(ww) ? ww : 1024;
        _waveBuf.ShowTx = s.WaveShowTx;
        _waveBuf.WindowLength = (int)(WaveWindowCombo.SelectedItem ?? 1024);
        ScriptBox.Text = s.ScriptText ?? "";
        ProtocolList.ItemsSource = _protocolLines;
        ProtocolTopCombo.ItemsSource = ProtocolModes;
        ProtocolTopCombo.SelectedItem = ProtocolCombo.SelectedItem;

        ChecksumCombo.ItemsSource = ChecksumOptions;
        ChecksumCombo.SelectedItem = ChecksumOptions.FirstOrDefault(o => o.Value.ToString() == s.Checksum) ?? ChecksumOptions[0];
        ThemeCombo.ItemsSource = ThemeOptions;
        ThemeCombo.SelectedItem = ThemeOptions.FirstOrDefault(o => o.Value == ThemeManager.Mode) ?? ThemeOptions[0];
        HighlightChk.IsChecked = s.HighlightEnabled;
        AutoReplyChk.IsChecked = s.AutoReplyEnabled;
        AutoReplyBadge.Visibility = s.AutoReplyEnabled ? Visibility.Visible : Visibility.Collapsed;
        var ver = typeof(MainWindow).Assembly.GetName().Version;
        AppTitleText.Text = $"SerialTool 串口调试助手  v{ver?.Major}.{ver?.Minor}.{ver?.Build}";
        RebuildRules();
        InitControlsV13();
        InitControlsV14();
        InitControlsV17();
        ApplyProtocolModeUi();
        UpdateChecksumPreview();
        InitControlsV15();
        InitControlsV16();
    }

    private void CollectSettings()
    {
        var s = _settings;
        if (int.TryParse(BaudCombo.Text.Trim(), out int baud) && baud > 0) s.BaudRate = baud;
        if (DataBitsCombo.SelectedItem is Opt<int> db) s.DataBits = db.Value;
        if (StopBitsCombo.SelectedItem is Opt<StopBits> sb) s.StopBits = sb.Value;
        if (ParityCombo.SelectedItem is Opt<Parity> p) s.Parity = p.Value;
        if (FlowCombo.SelectedItem is Opt<Handshake> h) s.Handshake = h.Value;
        if (PortCombo.SelectedItem is PortItem pi) s.PortName = pi.Name;
        s.Dtr = DtrChk.IsChecked == true;
        s.Rts = RtsChk.IsChecked == true;
        s.AutoReconnect = AutoReconnectChk.IsChecked == true;

        s.HexDisplay = HexDisplayChk.IsChecked == true;
        s.ShowTimestamp = TimestampChk.IsChecked == true;
        s.ShowTx = ShowTxChk.IsChecked == true;
        s.AutoScroll = AutoScrollChk.IsChecked == true;
        s.Encoding = EncodingCombo.SelectedItem as string ?? "UTF-8";
        if (int.TryParse(FrameGapBox.Text.Trim(), out int gap) && gap >= 0) s.FrameGapMs = gap;

        s.HexSend = HexSendChk.IsChecked == true;
        s.Escape = EscapeChk.IsChecked == true;
        s.LineEnding = (LineEndingCombo.SelectedItem as Opt<string>)?.Value ?? "";
        if (int.TryParse(IntervalBox.Text.Trim(), out int iv) && iv >= 1) s.TimerIntervalMs = iv;
        s.SendText = SendBox.Text;
        s.ProtocolMode = ProtocolCombo.SelectedItem as string ?? "普通";
        s.WaveShowTx = WaveShowTxChk.IsChecked == true;
        if (WaveWindowCombo.SelectedItem is int wlen) s.WaveWindowLength = wlen;
        s.ScriptText = ScriptBox.Text;
        s.Checksum = CurrentChecksum().ToString();
        s.Theme = ThemeManager.Mode.ToString();
        s.HighlightEnabled = HighlightChk.IsChecked == true;
        s.AutoReplyEnabled = AutoReplyChk.IsChecked == true;
        CollectSettingsV13();
        CollectSettingsV14();
        CollectSettingsV17();
        CollectSettingsV16();
        CollectSettingsV15();

        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (!bounds.IsEmpty)
        {
            s.WindowWidth = bounds.Width;
            s.WindowHeight = bounds.Height;
        }
        s.WindowMaximized = WindowState == WindowState.Maximized;
        s.SendPanelHeight = SendRow.ActualHeight;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _loading = false;
        RenderQuickButtons();
        RenderHistory();
        UpdateOpenState();
        RefreshPorts(false);
        _uiTimer.Start();
        _pollTimer.Start();
        _statsTimer.Start();
        _waveElement = new WaveformElement(_waveBuf);
        WaveHost.Child = _waveElement;
        _script = new ScriptEngine(this);
        _script.Output += msg => Dispatcher.BeginInvoke(() =>
        {
            ScriptOutBox.AppendText(msg + "\r\n");
            ScriptOutBox.ScrollToEnd();
        });
        _script.RunningChanged += running => Dispatcher.BeginInvoke(() =>
        {
            ScriptRunBtn.IsEnabled = !running;
            ScriptStopBtn.IsEnabled = running;
        });
        _atParser = new AtLineParser(CurrentEncoding());
        _meter.Reset();
        InitV12();
        InitV13();
        InitV14();
        InitV15();
        SetStatus("就绪。设置文件：" + SettingsStore.FilePath);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _closingWindow = true;
        _uiTimer.Stop();
        _pollTimer.Stop();
        _sendTimer.Stop();
        _statsTimer.Stop();
        try { _script?.Stop(); } catch { /* ignore */ }
        try { ShutdownV13(); } catch (Exception ex) { App.LogCrash(ex); }
        try { ShutdownV14(); } catch (Exception ex) { App.LogCrash(ex); }
        try
        {
            CollectSettings();
            SettingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            App.LogCrash(ex);
        }
        try { ShutdownV15(); } catch (Exception ex) { App.LogCrash(ex); }
        try { ShutdownV16(); } catch (Exception ex) { App.LogCrash(ex); }

        var s = _conn;
        _conn = null;
        _mainChannel.Connection = null;
        if (s != null)
        {
            try { Task.Run(s.Stop).Wait(2500); } catch { /* ignore */ }
        }
        FlushQueue(); // 把最后的数据写入日志
        CloseLog();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            RefreshPorts(true);
            e.Handled = true;
        }
        else if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            OpenSearchBar();
            e.Handled = true;
        }
    }

    // ======================================================================
    // 状态显示
    // ======================================================================

    private void SetStatus(string message, bool error = false)
    {
        StatusText.Text = $"[{DateTime.Now:HH:mm:ss}] {message}";
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, error ? "StatusErrorBrush" : "StatusFgBrush");
        StatusText.ToolTip = message;
    }

    private void UpdateOpenState()
    {
        bool open = _conn != null;
        var kind = open ? _conn!.Kind : CurrentConnKind();
        OpenBtn.Content = _busy ? "请稍候…" : OpenButtonText(kind, open);
        OpenBtn.Style = (Style)FindResource(open ? "DangerButton" : "AccentButton");
        OpenBtn.IsEnabled = !_busy;
        PortCombo.IsEnabled = !open && !_busy;
        ConnTypeCombo.IsEnabled = !open && !_busy;
        RemoteHostBox.IsEnabled = RemotePortBox.IsEnabled = LocalPortBox.IsEnabled = !open && !_busy;
        StateDot.Fill = open ? OkBrush : IdleBrush;
        Title = open ? $"SerialTool 串口调试助手 — {_conn!.Name} " + (kind == ConnectionKind.Serial ? "已打开" : "已连接") : "SerialTool 串口调试助手";
        RefreshServerClients();
    }

    private void UpdateCounters()
    {
        if (_rxTotal == _shownRx && _txTotal == _shownTx) return;
        _shownRx = _rxTotal;
        _shownTx = _txTotal;
        RxCountText.Text = $"RX: {_rxTotal:N0}";
        TxCountText.Text = $"TX: {_txTotal:N0}";
    }

    private void ResetCounters_Click(object sender, RoutedEventArgs e)
    {
        _rxTotal = 0;
        _txTotal = 0;
        _meter.Reset();
        _autoReplyCount = 0;
        AutoReplyCountText.Text = "";
        UpdateCounters();
        UpdateThroughputUi();
    }

    // ======================================================================
    // 串口列表 / 热插拔
    // ======================================================================

    private void RefreshBtn_Click(object sender, RoutedEventArgs e) => RefreshPorts(true);

    private async void RefreshPorts(bool userInitiated)
    {
        int version = ++_refreshVersion;
        string[] names = PortScanner.GetNames();
        _lastPortNames = names;

        // 先立即显示端口名，再异步补充设备描述
        ApplyPortList(names, null);
        Dictionary<string, string> desc;
        try
        {
            desc = await Task.Run(PortScanner.GetDescriptions);
        }
        catch
        {
            desc = new Dictionary<string, string>();
        }
        if (version != _refreshVersion || _closingWindow) return;
        ApplyPortList(names, desc);
        if (userInitiated) SetStatus(names.Length == 0 ? "未发现串口" : $"发现 {names.Length} 个串口");
    }

    private void ApplyPortList(string[] names, Dictionary<string, string>? desc)
    {
        var serial = SerialConn;
        string? keep = serial?.PortName ?? (PortCombo.SelectedItem as PortItem)?.Name ?? _settings.PortName;
        var items = names.Select(n => new PortItem(n, desc != null && desc.TryGetValue(n, out var d) ? d : "")).ToList();
        if (serial != null && !items.Any(i => i.Name.Equals(serial.PortName, StringComparison.OrdinalIgnoreCase)))
            items.Insert(0, new PortItem(serial.PortName, "(已打开)"));

        PortCombo.ItemsSource = items;
        PortCombo.SelectedItem = items.FirstOrDefault(i => i.Name.Equals(keep, StringComparison.OrdinalIgnoreCase))
                                 ?? items.FirstOrDefault();
        RefreshMonitorPortNames(names);
    }

    private void PollPorts()
    {
        string[] names = PortScanner.GetNames();
        bool changed = !names.SequenceEqual(_lastPortNames, StringComparer.OrdinalIgnoreCase);

        if (changed)
        {
            var removed = _lastPortNames.Except(names, StringComparer.OrdinalIgnoreCase).ToList();
            var added = names.Except(_lastPortNames, StringComparer.OrdinalIgnoreCase).ToList();
            if (SerialConn is { } sc && removed.Contains(sc.PortName, StringComparer.OrdinalIgnoreCase))
                HandleUnexpectedDisconnect($"串口 {sc.PortName} 已被移除");
            else if (added.Count > 0)
                SetStatus("检测到新串口：" + string.Join(", ", added));
            else if (removed.Count > 0)
                SetStatus("串口已移除：" + string.Join(", ", removed));
            RefreshPorts(false);
        }

        if (_reconnectPort != null && _conn == null && !_busy &&
            names.Contains(_reconnectPort, StringComparer.OrdinalIgnoreCase))
        {
            if (AutoReconnectChk.IsChecked == true)
                _ = OpenAsync(_reconnectPort, false);
            else
                _reconnectPort = null;
        }
    }

    // ======================================================================
    // 打开 / 关闭
    // ======================================================================

    private async void OpenBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_conn != null)
        {
            _reconnectPort = null;
            await CloseAsync(_conn.Kind == ConnectionKind.Serial ? "串口已关闭" : $"{_conn.Name} 已断开");
            return;
        }
        if (CurrentConnKind() != ConnectionKind.Serial)
        {
            await OpenNetworkAsync(CurrentConnKind());
            return;
        }
        if (PortCombo.SelectedItem is not PortItem item)
        {
            SetStatus("请先选择串口（没有串口时请检查驱动或点“刷新”）", true);
            return;
        }
        _reconnectPort = null;
        await OpenAsync(item.Name, true);
    }

    private bool TryGetBaud(string text, out int baud, out string? error)
    {
        error = null;
        if (!int.TryParse(text.Trim(), out baud) || baud <= 0 || baud > 100_000_000)
        {
            error = $"波特率无效：“{text}”";
            return false;
        }
        return true;
    }

    private Handshake CurrentHandshake() => (FlowCombo.SelectedItem as Opt<Handshake>)?.Value ?? Handshake.None;

    private static bool HandshakeUsesRts(Handshake h) =>
        h == Handshake.RequestToSend || h == Handshake.RequestToSendXOnXOff;

    private async Task OpenAsync(string portName, bool userInitiated)
    {
        if (_busy || _conn != null) return;
        if (!TryGetBaud(BaudCombo.Text, out int baud, out string? err))
        {
            SetStatus(err!, true);
            if (userInitiated) MessageBox.Show(this, err, "打开串口", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var handshake = CurrentHandshake();
        var port = new SerialPort(portName)
        {
            ReadTimeout = 100,
            WriteTimeout = 3000,
            ReadBufferSize = 1 << 20,
            WriteBufferSize = 1 << 16,
            ParityReplace = 0, // 不替换校验错误字节，保持原始数据
            DiscardNull = false,
        };

        _busy = true;
        UpdateOpenState();
        SetStatus($"正在打开 {portName} …");
        try
        {
            port.BaudRate = baud;
            port.DataBits = (DataBitsCombo.SelectedItem as Opt<int>)?.Value ?? 8;
            port.StopBits = (StopBitsCombo.SelectedItem as Opt<StopBits>)?.Value ?? StopBits.One;
            port.Parity = (ParityCombo.SelectedItem as Opt<Parity>)?.Value ?? Parity.None;
            port.Handshake = handshake;
            port.DtrEnable = DtrChk.IsChecked == true;
            if (!HandshakeUsesRts(handshake)) port.RtsEnable = RtsChk.IsChecked == true;

            await Task.Run(port.Open);
        }
        catch (Exception ex)
        {
            try { port.Dispose(); } catch { /* ignore */ }
            _busy = false;
            UpdateOpenState();
            string msg = DescribeOpenError(ex, portName);
            if (!userInitiated && _reconnectPort != null) msg += "（稍后自动重试）";
            SetStatus(msg, true);
            if (userInitiated) MessageBox.Show(this, msg, "打开串口失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_closingWindow)
        {
            try { port.Dispose(); } catch { /* ignore */ }
            return;
        }

        AttachConnection(new SerialSession(port, _queue));

        _reconnectPort = null;
        _busy = false;
        _settings.PortName = portName;
        UpdateOpenState();
        SetStatus($"已打开 {portName}，{DescribeParams(port)}" + (userInitiated ? "" : "（自动重连）"));
    }

    private static string DescribeParams(SerialPort p)
    {
        string parity = p.Parity switch
        {
            Parity.None => "N", Parity.Odd => "O", Parity.Even => "E", Parity.Mark => "M", _ => "S",
        };
        string stop = p.StopBits switch { StopBits.OnePointFive => "1.5", StopBits.Two => "2", _ => "1" };
        string flow = p.Handshake switch
        {
            Handshake.RequestToSend => "RTS/CTS", Handshake.XOnXOff => "XON/XOFF",
            Handshake.RequestToSendXOnXOff => "RTS/CTS+XON/XOFF", _ => "无流控",
        };
        return $"{p.BaudRate} {p.DataBits}{parity}{stop}，{flow}";
    }

    private static string DescribeOpenError(Exception ex, string portName) => ex switch
    {
        UnauthorizedAccessException => $"无法打开 {portName}：拒绝访问，串口可能已被其他程序占用。",
        FileNotFoundException => $"无法打开 {portName}：端口不存在，设备可能已拔出。",
        ArgumentOutOfRangeException or ArgumentException => $"无法打开 {portName}：参数无效（{ex.Message}）",
        IOException => $"无法打开 {portName}：{ex.Message}（请检查数据位/停止位组合是否被设备支持）",
        _ => $"无法打开 {portName}：{ex.Message}",
    };

    private async Task CloseAsync(string statusMessage, bool error = false)
    {
        var s = _conn;
        if (s == null) return;
        _conn = null;
        _mainChannel.Connection = null;
        s.Received -= OnConnReceived;
        _mbWaiter.Cancel();
        OnMainConnectionClosingV14();
        _busy = true;
        StopTimedSend(null);
        try { _msEngine.Stop(); } catch { /* ignore */ }
        UpdateOpenState();
        SetStatus("正在关闭串口…");

        var stopTask = Task.Run(s.Stop);
        await Task.WhenAny(stopTask, Task.Delay(4000));

        _busy = false;
        UpdateOpenState();
        SetStatus(statusMessage, error);
    }

    private void HandleUnexpectedDisconnect(string message)
    {
        var s = _conn;
        if (s == null) return;
        bool reconnect = AutoReconnectChk.IsChecked == true && s is SerialSession;
        _reconnectPort = reconnect ? ((SerialSession)s).PortName : null;
        _ = CloseAsync(message + (reconnect ? "，等待设备重新连接…" : ""), true);
    }

    // ======================================================================
    // 串口参数实时修改
    // ======================================================================

    private void ApplyLive(Action<SerialPort> apply, string what)
    {
        if (_loading) return;
        var s = SerialConn;
        if (s == null || _busy) return;
        try
        {
            apply(s.Port);
            SetStatus($"已更新{what}：{DescribeParams(s.Port)}");
        }
        catch (Exception ex)
        {
            SetStatus($"设置{what}失败：{ex.Message}", true);
        }
    }

    private void BaudCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is string text) ApplyBaud(text);
    }

    private void BaudCombo_LostFocus(object sender, RoutedEventArgs e) => ApplyBaud(BaudCombo.Text);

    private void BaudCombo_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !BaudCombo.IsDropDownOpen)
        {
            ApplyBaud(BaudCombo.Text);
            e.Handled = true;
        }
    }

    private void ApplyBaud(string text)
    {
        if (_loading || SerialConn == null) return;
        if (!TryGetBaud(text, out int baud, out string? err))
        {
            SetStatus(err!, true);
            return;
        }
        if (SerialConn.Port.BaudRate == baud) return;
        ApplyLive(p => p.BaudRate = baud, "波特率");
    }

    private void PortParam_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (sender == FlowCombo) UpdateRtsAvailability();
        if (_conn == null) return;

        if (sender == DataBitsCombo && DataBitsCombo.SelectedItem is Opt<int> db)
            ApplyLive(p => p.DataBits = db.Value, "数据位");
        else if (sender == StopBitsCombo && StopBitsCombo.SelectedItem is Opt<StopBits> sb)
            ApplyLive(p => p.StopBits = sb.Value, "停止位");
        else if (sender == ParityCombo && ParityCombo.SelectedItem is Opt<Parity> pa)
            ApplyLive(p => p.Parity = pa.Value, "校验位");
        else if (sender == FlowCombo && FlowCombo.SelectedItem is Opt<Handshake> h)
        {
            bool rts = RtsChk.IsChecked == true;
            ApplyLive(p =>
            {
                p.Handshake = h.Value;
                if (!HandshakeUsesRts(h.Value)) p.RtsEnable = rts;
            }, "流控");
        }
    }

    private void UpdateRtsAvailability()
    {
        bool rtsByFlow = HandshakeUsesRts(CurrentHandshake());
        RtsChk.IsEnabled = !rtsByFlow;
        RtsChk.ToolTip = rtsByFlow ? "RTS 由硬件流控自动控制" : "手动控制 RTS 信号";
    }

    private void DtrChk_Click(object sender, RoutedEventArgs e)
    {
        bool v = DtrChk.IsChecked == true;
        ApplyLive(p => p.DtrEnable = v, "DTR");
    }

    private void RtsChk_Click(object sender, RoutedEventArgs e)
    {
        bool v = RtsChk.IsChecked == true;
        if (HandshakeUsesRts(CurrentHandshake())) return;
        ApplyLive(p => p.RtsEnable = v, "RTS");
    }

    // ======================================================================
    // 接收显示
    // ======================================================================

    private Encoding CurrentEncoding() => EncodingUtil.Get(EncodingCombo.SelectedItem as string ?? "UTF-8");

    private void ApplyFormatterOptions()
    {
        _formatter.Hex = HexDisplayChk.IsChecked == true;
        _formatter.Timestamp = TimestampChk.IsChecked == true;
        _formatter.ShowTx = ShowTxChk.IsChecked == true;
        if (int.TryParse(FrameGapBox.Text.Trim(), out int gap) && gap >= 0 && gap <= 60000)
            _formatter.FrameGapMs = gap;
    }

    /// <summary>显示选项变化：先换行，避免新旧格式混在同一行。</summary>
    private void RxOption_Click(object sender, RoutedEventArgs e)
    {
        FlushQueue();
        var sb = new StringBuilder();
        _formatter.BreakLine(sb);
        if (sender == HexDisplayChk) _formatter.ResetDecoders();
        ApplyFormatterOptions();
        if (sb.Length > 0) EmitText(sb.ToString());
    }

    private void EncodingCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        FlushQueue();
        _formatter.Encoding = CurrentEncoding();
        SetStatus("字符编码：" + EncodingCombo.SelectedItem);
    }

    private void FrameGapBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(FrameGapBox.Text.Trim(), out int gap) && gap >= 0 && gap <= 60000)
        {
            _formatter.FrameGapMs = gap;
        }
        else
        {
            FrameGapBox.Text = _formatter.FrameGapMs.ToString();
            SetStatus("分帧间隔需为 0~60000 的整数", true);
        }
    }

    private void AutoScrollChk_Click(object sender, RoutedEventArgs e)
    {
        if (AutoScrollChk.IsChecked != true) return;
        RxBox.ScrollToEnd();
        ScrollLinesToEnd();
    }

    private void PauseChk_Click(object sender, RoutedEventArgs e)
    {
        bool paused = PauseChk.IsChecked == true;
        PausedBadge.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
        if (!paused && _pauseBuffer.Length > 0)
        {
            string text = _pauseBuffer.ToString();
            _pauseBuffer.Clear();
            AppendDisplay(text);
        }
        if (!paused && _pausedLines.Count > 0)
        {
            var lines = _pausedLines.ToList();
            _pausedLines.Clear();
            foreach (var l in lines) AddLineItem(l);
            ScrollLinesToEnd();
        }
    }

    /// <summary>UI 定时器：批量取出收发数据，格式化后一次性追加到界面。</summary>
    private void FlushQueue()
    {
        if (!_queue.IsEmpty)
        {
            var sb = new StringBuilder();
            long bytes = 0;
            bool waveDirty = false;
            string mode = ProtocolCombo.SelectedItem as string ?? "普通";
            while (bytes < MaxBytesPerTick && _queue.TryDequeue(out var c))
            {
                bytes += c.Data.Length;
                if (c.IsTx)
                {
                    _txTotal += c.Data.Length;
                    _meter.AddTx(c.Data.Length);
                }
                else
                {
                    _rxTotal += c.Data.Length;
                    _meter.AddRx(c.Data.Length);
                }

                if (!ProcessChunkV14(c)) continue; // 录制；文件传输期间不显示
                _waveBuf.Push(c);
                waveDirty = true;
                RecordChunk(c);

                if (!c.IsTx && !c.Replayed)
                {
                    // 脚本 WAITFOR 只看接收数据（v1.1 误把发送回显也算进去）
                    try { _script?.FeedRx(CurrentEncoding().GetString(c.Data)); }
                    catch { /* ignore */ }

                    if (AutoReplyChk.IsChecked == true)
                        foreach (var rule in _autoReply.Push(c.Data, DateTime.Now))
                            FireAutoReply(rule.Rule);
                }

                if (mode == "普通")
                {
                    _formatter.Format(c, sb);
                }
                else if (mode == "AT")
                {
                    if (_atParser == null) _atParser = new AtLineParser(CurrentEncoding());
                    foreach (var line in _atParser.Push(c, CurrentEncoding()))
                        AppendProtocolLine(line);
                }
                else if (mode == "Modbus RTU")
                {
                    foreach (var frame in _modbus.Push(c))
                    {
                        _meter.AddFrame(frame.CrcOk);
                        var line = new ProtocolLine
                        {
                            Text = frame.Summary + "  " + ModbusRtuParser.FormatHex(frame.Raw),
                            Kind = frame.CrcOk ? AtLineKind.Ok : AtLineKind.Error,
                            IsTx = frame.IsTx,
                            Time = frame.Time,
                            IsError = !frame.CrcOk,
                            Tone = frame.CrcOk ? LineTone.Ok : LineTone.Err,
                        };
                        AppendProtocolLine(line);
                    }
                }
            }
            if (sb.Length > 0) EmitText(sb.ToString());
            if (waveDirty) _waveElement?.InvalidateVisual();
            ScrollLinesToEnd();
        }
        FlushRecorders();
        FlushMonitors();
        FlushV14();
        UpdateCounters();
    }

    private void EmitText(string text)
    {
        WriteLog(text);
        if (PauseChk.IsChecked == true)
        {
            _pauseBuffer.Append(text);
            if (_pauseBuffer.Length > MaxDisplayChars)
                _pauseBuffer.Remove(0, _pauseBuffer.Length - TrimDisplayChars);
        }
        else
        {
            AppendDisplay(text);
        }
    }

    private void AppendDisplay(string text)
    {
        if (text.Length == 0) return;
        if (_lineViewPlain)
        {
            AppendPlainToLines(text);
            return;
        }
        if (_displayLen + text.Length > MaxDisplayChars)
        {
            // 超过上限：只保留末尾部分，并尽量从整行开始
            string combined = text.Length >= TrimDisplayChars ? text : RxBox.Text + text;
            int start = Math.Max(0, combined.Length - TrimDisplayChars);
            int nl = combined.IndexOf('\n', start);
            if (nl >= 0 && nl - start < 4096) start = nl + 1;
            string kept = combined.Substring(start);
            RxBox.Text = kept;
            _displayLen = kept.Length;
        }
        else
        {
            RxBox.AppendText(text);
            _displayLen += text.Length;
        }
        if (AutoScrollChk.IsChecked == true) RxBox.ScrollToEnd();
    }

    private void ClearRx_Click(object sender, RoutedEventArgs e)
    {
        RxBox.Clear();
        _displayLen = 0;
        _pauseBuffer.Clear();
        _formatter.ResetLine();
        _protocolLines.Clear();
        _pausedLines.Clear();
        ResetOpenLine();
        _modbus.Reset();
        _atParser = new AtLineParser(CurrentEncoding());
        ClearRecords();
        UpdateSearchCount();
    }

    private void SaveRx_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "保存接收内容",
            FileName = $"serial_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
            Filter = "文本文件 (*.txt)|*.txt|日志文件 (*.log)|*.log|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            string body = ProtocolList.Visibility == Visibility.Visible
                ? string.Join("\r\n", System.Linq.Enumerable.Select(_protocolLines, l => l.Text))
                : RxBox.Text + _pauseBuffer;
            File.WriteAllText(dlg.FileName, body, new UTF8Encoding(true));
            SetStatus("已保存到 " + dlg.FileName);
        }
        catch (Exception ex)
        {
            SetStatus("保存失败：" + ex.Message, true);
            MessageBox.Show(this, ex.Message, "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LogChk_Click(object sender, RoutedEventArgs e)
    {
        if (LogChk.IsChecked != true)
        {
            string? path = _logPath;
            CloseLog();
            if (path != null) SetStatus("已停止记录：" + path);
            return;
        }

        var dlg = new SaveFileDialog
        {
            Title = "选择日志文件（追加写入）",
            FileName = $"serial_{DateTime.Now:yyyyMMdd_HHmmss}.log",
            Filter = "日志文件 (*.log)|*.log|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            OverwritePrompt = false,
        };
        if (dlg.ShowDialog(this) != true)
        {
            LogChk.IsChecked = false;
            return;
        }
        try
        {
            var fs = new FileStream(dlg.FileName, FileMode.Append, FileAccess.Write, FileShare.Read);
            _logWriter = new StreamWriter(fs, new UTF8Encoding(false));
            _logPath = dlg.FileName;
            LogBadge.Visibility = Visibility.Visible;
            SetStatus("开始记录到 " + dlg.FileName);
        }
        catch (Exception ex)
        {
            LogChk.IsChecked = false;
            SetStatus("无法打开日志文件：" + ex.Message, true);
        }
    }

    private void WriteLog(string text)
    {
        if (_logWriter == null) return;
        try
        {
            _logWriter.Write(text);
            _logWriter.Flush();
        }
        catch (Exception ex)
        {
            CloseLog();
            SetStatus("写日志失败，已停止记录：" + ex.Message, true);
        }
    }

    private void CloseLog()
    {
        try { _logWriter?.Dispose(); } catch { /* ignore */ }
        _logWriter = null;
        _logPath = null;
        LogChk.IsChecked = false;
        LogBadge.Visibility = Visibility.Collapsed;
    }

    // ======================================================================
    // 发送
    // ======================================================================

    private bool TryBuildPayload(string content, bool hex, out byte[] data, out string? error) =>
        TryBuildPayload(content, hex, null, null, true, out data, out error);

    private ChecksumKind CurrentChecksum() =>
        (ChecksumCombo.SelectedItem as Opt<ChecksumKind>)?.Value ?? ChecksumKind.None;

    /// <summary>只解析内容本身（不含校验与行尾），供发送与校验预览共用。</summary>
    private bool TryBuildBody(string content, bool hex, bool? escape, out byte[] body, out string? error)
    {
        error = null;
        var enc = CurrentEncoding();
        if (hex) return HexUtil.TryParse(content, out body, out error);
        body = (escape ?? EscapeChk.IsChecked == true) ? EscapeUtil.Encode(content, enc) : enc.GetBytes(content);
        return true;
    }

    /// <summary>内容 → [校验] → [行尾]。checksum 为 null 时使用界面“校验”设置。</summary>
    private bool TryBuildPayload(string content, bool hex, bool? escape, ChecksumKind? checksum, bool appendLineEnding,
        out byte[] data, out string? error) =>
        TryBuildPayload(content, hex, escape, checksum, appendLineEnding, out data, out _, out _, out error);

    private bool TryBuildPayload(string content, bool hex, bool? escape, ChecksumKind? checksum, bool appendLineEnding,
        out byte[] data, out int markStart, out int markLength, out string? error)
    {
        data = Array.Empty<byte>();
        markStart = -1;
        markLength = 0;
        if (!TryBuildBody(content, hex, escape, out byte[] body, out error)) return false;
        if (body.Length > 0)
        {
            int plain = body.Length;
            body = Checksum.Append(checksum ?? CurrentChecksum(), body);
            if (body.Length > plain)
            {
                markStart = plain;
                markLength = body.Length - plain;
            }
        }

        string le = appendLineEnding ? (LineEndingCombo.SelectedItem as Opt<string>)?.Value ?? "" : "";
        if (le.Length == 0)
        {
            data = body;
        }
        else
        {
            byte[] tail = Encoding.ASCII.GetBytes(le);
            data = new byte[body.Length + tail.Length];
            Buffer.BlockCopy(body, 0, data, 0, body.Length);
            Buffer.BlockCopy(tail, 0, data, body.Length, tail.Length);
        }
        return true;
    }

    private bool SendNow(string content, bool hex, bool recordHistory)
    {
        var s = _conn;
        if (s == null)
        {
            SetStatus(NotOpenMessage, true);
            return false;
        }
        if (!TryBuildPayload(content, hex, null, null, true, out byte[] data, out int ms, out int ml, out string? error))
        {
            SetStatus(error ?? "发送内容无效", true);
            return false;
        }
        if (data.Length == 0)
        {
            SetStatus("发送内容为空", true);
            return false;
        }
        if (!s.Send(data, ms, ml))
        {
            SetStatus("发送队列已满或连接正在关闭，本次未发送", true);
            return false;
        }
        if (recordHistory) AddHistory(content, hex);
        return true;
    }

    private void SendBtn_Click(object sender, RoutedEventArgs e) => SendFromBox();

    private void SendFromBox()
    {
        if (SendNow(SendBox.Text, HexSendChk.IsChecked == true, true))
            SetStatus("已发送");
    }

    private void SendBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((e.Key == Key.Enter || e.Key == Key.Return) && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            SendFromBox();
            e.Handled = true;
        }
    }

    private void ClearSend_Click(object sender, RoutedEventArgs e)
    {
        SendBox.Clear();
        SendBox.Focus();
    }

    /// <summary>切换 HEX 发送时，自动把发送区内容在文本与 HEX 之间转换。</summary>
    private void HexSendChk_Click(object sender, RoutedEventArgs e)
    {
        bool toHex = HexSendChk.IsChecked == true;
        EscapeChk.IsEnabled = !toHex;
        string text = SendBox.Text;
        if (text.Length == 0) return;
        var enc = CurrentEncoding();
        if (toHex)
        {
            byte[] bytes = EscapeChk.IsChecked == true ? EscapeUtil.Encode(text, enc) : enc.GetBytes(text);
            SendBox.Text = HexUtil.ToHex(bytes);
        }
        else if (HexUtil.TryParse(text, out byte[] bytes, out _))
        {
            SendBox.Text = enc.GetString(bytes);
        }
        else
        {
            SetStatus("发送区内容不是合法 HEX，未做转换", true);
        }
    }

    private void SendFile_Click(object sender, RoutedEventArgs e)
    {
        var s = _conn;
        if (s == null)
        {
            SetStatus(NotOpenMessage, true);
            return;
        }
        var dlg = new OpenFileDialog { Title = "选择要发送的文件", Filter = "所有文件 (*.*)|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var info = new FileInfo(dlg.FileName);
            if (info.Length > MaxSendFileBytes)
            {
                SetStatus($"文件过大（{info.Length:N0} 字节），最大支持 16 MB", true);
                return;
            }
            byte[] all = File.ReadAllBytes(dlg.FileName);
            const int block = 4096;
            for (int off = 0; off < all.Length; off += block)
            {
                int n = Math.Min(block, all.Length - off);
                var part = new byte[n];
                Buffer.BlockCopy(all, off, part, 0, n);
                if (!s.Send(part))
                {
                    SetStatus($"发送队列已满，文件只发送了 {off:N0} 字节", true);
                    return;
                }
            }
            SetStatus($"文件已加入发送队列：{Path.GetFileName(dlg.FileName)}（{all.Length:N0} 字节）");
        }
        catch (Exception ex)
        {
            SetStatus("读取文件失败：" + ex.Message, true);
        }
    }

    // ---------- 定时发送 ----------

    private void TimedChk_Click(object sender, RoutedEventArgs e)
    {
        if (TimedChk.IsChecked != true)
        {
            StopTimedSend("定时发送已停止");
            return;
        }
        if (_conn == null)
        {
            TimedChk.IsChecked = false;
            SetStatus("请先打开串口 / 建立连接再启用定时发送", true);
            return;
        }
        if (!TryGetInterval(out int ms))
        {
            TimedChk.IsChecked = false;
            return;
        }
        if (!TryBuildPayload(SendBox.Text, HexSendChk.IsChecked == true, out byte[] data, out string? err) || data.Length == 0)
        {
            TimedChk.IsChecked = false;
            SetStatus(err ?? "发送内容为空", true);
            return;
        }
        _sendTimer.Interval = TimeSpan.FromMilliseconds(ms);
        _sendTimer.Start();
        SetStatus($"定时发送已启动，周期 {ms} ms");
    }

    private bool TryGetInterval(out int ms)
    {
        if (int.TryParse(IntervalBox.Text.Trim(), out ms) && ms >= 10 && ms <= 86_400_000) return true;
        SetStatus("定时周期需为 10 ~ 86400000 的整数（毫秒）", true);
        return false;
    }

    private void IntervalBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_sendTimer.IsEnabled) return;
        if (TryGetInterval(out int ms)) _sendTimer.Interval = TimeSpan.FromMilliseconds(ms);
    }

    private void TimedSendTick()
    {
        var s = _conn;
        if (s == null)
        {
            StopTimedSend("连接已关闭，定时发送停止");
            return;
        }
        if (s.PendingBytes > 64 * 1024) return; // 上一批还没写完（如被流控阻塞），跳过本次
        if (!SendNow(SendBox.Text, HexSendChk.IsChecked == true, false))
            StopTimedSend(null); // SendNow 已给出原因
    }

    private void StopTimedSend(string? message)
    {
        bool wasRunning = _sendTimer.IsEnabled;
        _sendTimer.Stop();
        TimedChk.IsChecked = false;
        if (wasRunning && message != null) SetStatus(message);
    }

    // ---------- 历史记录 ----------

    private void AddHistory(string text, bool hex)
    {
        if (string.IsNullOrEmpty(text)) return;
        _settings.History.RemoveAll(h => h.IsHex == hex && h.Text == text);
        _settings.History.Insert(0, new HistoryItem { Text = text, IsHex = hex });
        if (_settings.History.Count > MaxHistory)
            _settings.History.RemoveRange(MaxHistory, _settings.History.Count - MaxHistory);
        RenderHistory();
    }

    private void RenderHistory()
    {
        _historyUpdating = true;
        try
        {
            HistoryCombo.ItemsSource = null;
            HistoryCombo.ItemsSource = _settings.History.ToList();
            HistoryCombo.SelectedIndex = -1;
        }
        finally
        {
            _historyUpdating = false;
        }
    }

    private void HistoryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_historyUpdating || HistoryCombo.SelectedItem is not HistoryItem h) return;
        HexSendChk.IsChecked = h.IsHex; // 直接赋值不会触发 Click，因此不会做格式转换
        EscapeChk.IsEnabled = !h.IsHex;
        SendBox.Text = h.Text;
        SendBox.Focus();
        SendBox.CaretIndex = SendBox.Text.Length;
        Dispatcher.BeginInvoke(() =>
        {
            _historyUpdating = true;
            HistoryCombo.SelectedIndex = -1;
            _historyUpdating = false;
        });
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        _settings.History.Clear();
        RenderHistory();
        SetStatus("发送历史已清除");
    }

    // ---------- 快捷指令 ----------

    private void RenderQuickButtons()
    {
        QuickPanel.Children.Clear();
        var cmds = _settings.QuickCommands.Where(c => !string.IsNullOrEmpty(c.Content)).ToList();
        if (cmds.Count == 0)
        {
            QuickPanel.Children.Add(new TextBlock
            {
                Text = "（暂无，点击右侧“编辑快捷指令”添加）",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0),
            });
            ((TextBlock)QuickPanel.Children[0]).SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
            return;
        }
        foreach (var cmd in cmds)
        {
            var c = cmd;
            string label = string.IsNullOrWhiteSpace(c.Name) ? c.Content : c.Name;
            if (label.Length > 24) label = label.Substring(0, 24) + "…";
            var btn = new Button
            {
                Content = label,
                ToolTip = (c.IsHex ? "[HEX] " : "[文本] ") + c.Content,
                Margin = new Thickness(0, 0, 6, 4),
                Padding = new Thickness(10, 2, 10, 2),
                MinWidth = 56,
                Style = (Style)FindResource("SmallButton"),
            };
            btn.Click += (_, _) =>
            {
                if (SendNow(c.Content, c.IsHex, false)) SetStatus("已发送快捷指令：" + label);
            };
            QuickPanel.Children.Add(btn);
        }
    }

    private void EditQuick_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new QuickCommandsWindow(_settings.QuickCommands) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _settings.QuickCommands = dlg.Result;
        RenderQuickButtons();
        CollectSettings();
        SettingsStore.Save(_settings);
        SetStatus($"快捷指令已保存（{dlg.Result.Count} 条）");
    }
}
