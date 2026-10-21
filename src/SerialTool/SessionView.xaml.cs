using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using SerialTool.Features;

namespace SerialTool;

/// <summary>
/// 一个额外的会话标签页：独立的连接、收发显示、发送区、选项、统计、录制和脚本（DSL / Lua）。
/// Modbus 主站 / 从站、波形、曲线、帧解析、文件传输、串口转发、多端口监视只在第 1 个标签页（主会话）提供。
/// </summary>
public partial class SessionView : UserControl, ScriptEngine.IHost
{
    private const int MaxDisplayChars = 400_000;
    private const int TrimDisplayChars = 300_000;
    private const int MaxLines = 5000;

    private readonly DispatcherTimer _ui;
    private readonly DispatcherTimer _sendTimer;
    private readonly DisplayFormatter _formatter = new();
    private AtLineParser? _at;
    private readonly ModbusRtuParser _modbus = new(50);
    private readonly ObservableCollection<ProtocolLine> _lines = new();
    private readonly AutoReplyEngine _autoReply = new();
    private readonly StringBuilder _pauseBuffer = new();
    private long _rx, _tx, _replies, _shownRx = -1, _shownTx = -1;
    private int _displayLen;
    private SessionRecorder? _rec;
    private ScriptEngine? _dsl;
    private LuaScriptHost? _lua;
    private string _dslText = "", _luaText = "";
    private string _lang = "DSL";
    private bool _loading = true;
    private bool _busy;
    private bool _disposed;

    public SessionView(SessionSettings settings)
    {
        InitializeComponent();
        Core = new SessionCore(settings);
        Core.Faulted += msg => Dispatcher.BeginInvoke(() => _ = CloseConnAsync("连接断开：" + msg, true));
        Core.Warning += msg => Dispatcher.BeginInvoke(() => RaiseStatus(msg, true));

        ConnTypeCombo.ItemsSource = MainWindow.ConnTypeOptions;
        BaudCombo.ItemsSource = MainWindow.BaudPresets;
        DataBitsCombo.ItemsSource = MainWindow.DataBitsOptions;
        StopBitsCombo.ItemsSource = MainWindow.StopBitsOptions;
        ParityCombo.ItemsSource = MainWindow.ParityOptions;
        FlowCombo.ItemsSource = MainWindow.FlowOptions;
        EncodingCombo.ItemsSource = EncodingUtil.Names;
        ProtocolCombo.ItemsSource = MainWindow.ProtocolModes;
        ChecksumCombo.ItemsSource = MainWindow.ChecksumOptions;
        LineEndingCombo.ItemsSource = MainWindow.LineEndingOptions;
        ScriptLangCombo.ItemsSource = new[] { "DSL", "Lua" };
        LineList.ItemsSource = _lines;
        LoadSettings(settings);
        RefreshPorts();

        _dsl = new ScriptEngine(this);
        _dsl.Output += m => Dispatcher.BeginInvoke(() => AppendScriptOut(m));
        _dsl.RunningChanged += r => Dispatcher.BeginInvoke(() => SetScriptRunning(r));

        _ui = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
        _ui.Tick += (_, _) => Flush();
        _ui.Start();
        _sendTimer = new DispatcherTimer(DispatcherPriority.Normal);
        _sendTimer.Tick += (_, _) =>
        {
            if (!Core.IsOpen) { StopTimer(); return; }
            if (!SendNow(SendBox.Text, HexSendChk.IsChecked == true)) StopTimer();
        };
        _loading = false;
        UpdateOpenState();
    }

    public SessionCore Core { get; }
    public SessionChannel Channel => Core.Channel;
    public string SessionName
    {
        get => Core.Settings.Name;
        set
        {
            Core.Settings.Name = value;
            Channel.Name = value;
        }
    }

    /// <summary>状态栏消息（文字，是否错误）。</summary>
    public event Action<string, bool>? StatusMessage;

    private void RaiseStatus(string msg, bool error = false) => StatusMessage?.Invoke($"[{SessionName}] {msg}", error);

    // ------------------------------------------------------------------
    // 设置
    // ------------------------------------------------------------------

    private void LoadSettings(SessionSettings s)
    {
        ConnTypeCombo.SelectedItem = MainWindow.ConnTypeOptions.FirstOrDefault(o => o.Value.ToString() == s.ConnType) ?? MainWindow.ConnTypeOptions[0];
        PortCombo.Text = s.PortName;
        BaudCombo.Text = s.BaudRate.ToString();
        DataBitsCombo.SelectedItem = MainWindow.DataBitsOptions.FirstOrDefault(o => o.Value == s.DataBits) ?? MainWindow.DataBitsOptions.Last();
        StopBitsCombo.SelectedItem = MainWindow.StopBitsOptions.FirstOrDefault(o => o.Value == s.StopBits) ?? MainWindow.StopBitsOptions[0];
        ParityCombo.SelectedItem = MainWindow.ParityOptions.FirstOrDefault(o => o.Value == s.Parity) ?? MainWindow.ParityOptions[0];
        FlowCombo.SelectedItem = MainWindow.FlowOptions.FirstOrDefault(o => o.Value == s.Handshake) ?? MainWindow.FlowOptions[0];
        DtrChk.IsChecked = s.Dtr;
        RtsChk.IsChecked = s.Rts;
        HostBox.Text = s.RemoteHost;
        RemotePortBox.Text = s.RemotePort.ToString();
        LocalPortBox.Text = s.LocalPort.ToString();
        HexDisplayChk.IsChecked = s.HexDisplay;
        TimestampChk.IsChecked = s.ShowTimestamp;
        ShowTxChk.IsChecked = s.ShowTx;
        EncodingCombo.SelectedItem = EncodingUtil.Names.Contains(s.Encoding) ? s.Encoding : "UTF-8";
        ProtocolCombo.SelectedItem = MainWindow.ProtocolModes.Contains(s.ProtocolMode) ? s.ProtocolMode : MainWindow.ProtocolModes[0];
        FrameGapBox.Text = s.FrameGapMs.ToString();
        AutoReplyChk.IsChecked = s.AutoReplyEnabled;
        HexSendChk.IsChecked = s.HexSend;
        EscapeChk.IsChecked = s.Escape;
        ChecksumCombo.SelectedItem = MainWindow.ChecksumOptions.FirstOrDefault(o => o.Value.ToString() == s.Checksum) ?? MainWindow.ChecksumOptions[0];
        LineEndingCombo.SelectedItem = MainWindow.LineEndingOptions.FirstOrDefault(o => o.Value == s.LineEnding) ?? MainWindow.LineEndingOptions[0];
        IntervalBox.Text = s.TimerIntervalMs.ToString();
        SendBox.Text = s.SendText;
        _lang = s.ScriptLanguage == "Lua" ? "Lua" : "DSL";
        _dslText = !string.IsNullOrEmpty(s.DslScriptText) ? s.DslScriptText : _lang == "DSL" ? s.ScriptText : "";
        _luaText = !string.IsNullOrEmpty(s.LuaScriptText) ? s.LuaScriptText : _lang == "Lua" ? s.ScriptText : "";
        ScriptLangCombo.SelectedItem = _lang;
        ScriptBox.Text = _lang == "Lua" ? _luaText : _dslText;
        RefreshSamples();
        ApplyConnTypeUi();
        ApplyDisplayOptions();
        ApplyProtocolUi();
    }

    /// <summary>把界面上的值写回 Core.Settings（发送、打开、保存前调用）。</summary>
    public SessionSettings CollectSettings()
    {
        var s = Core.Settings;
        if (ConnTypeCombo.SelectedItem is Opt<ConnectionKind> ck) s.ConnType = ck.Value.ToString();
        s.PortName = PortCombo.SelectedItem is PortItem pi ? pi.Name : ExtractPortName(PortCombo.Text);
        if (int.TryParse(BaudCombo.Text.Trim(), out int baud) && baud > 0) s.BaudRate = baud;
        if (DataBitsCombo.SelectedItem is Opt<int> db) s.DataBits = db.Value;
        if (StopBitsCombo.SelectedItem is Opt<StopBits> sb) s.StopBits = sb.Value;
        if (ParityCombo.SelectedItem is Opt<Parity> p) s.Parity = p.Value;
        if (FlowCombo.SelectedItem is Opt<Handshake> h) s.Handshake = h.Value;
        s.Dtr = DtrChk.IsChecked == true;
        s.Rts = RtsChk.IsChecked == true;
        s.RemoteHost = HostBox.Text.Trim();
        if (int.TryParse(RemotePortBox.Text.Trim(), out int rp) && rp is >= 0 and <= 65535) s.RemotePort = rp;
        if (int.TryParse(LocalPortBox.Text.Trim(), out int lp) && lp is >= 0 and <= 65535) s.LocalPort = lp;
        s.HexDisplay = HexDisplayChk.IsChecked == true;
        s.ShowTimestamp = TimestampChk.IsChecked == true;
        s.ShowTx = ShowTxChk.IsChecked == true;
        s.Encoding = EncodingCombo.SelectedItem as string ?? "UTF-8";
        s.ProtocolMode = ProtocolCombo.SelectedItem as string ?? "普通";
        if (int.TryParse(FrameGapBox.Text.Trim(), out int gap) && gap >= 0) s.FrameGapMs = gap;
        s.AutoReplyEnabled = AutoReplyChk.IsChecked == true;
        s.HexSend = HexSendChk.IsChecked == true;
        s.Escape = EscapeChk.IsChecked == true;
        s.Checksum = ((ChecksumCombo.SelectedItem as Opt<ChecksumKind>)?.Value ?? ChecksumKind.None).ToString();
        s.LineEnding = (LineEndingCombo.SelectedItem as Opt<string>)?.Value ?? "";
        if (int.TryParse(IntervalBox.Text.Trim(), out int iv) && iv >= 1) s.TimerIntervalMs = iv;
        s.SendText = SendBox.Text;
        if (_lang == "Lua") _luaText = ScriptBox.Text; else _dslText = ScriptBox.Text;
        s.ScriptLanguage = _lang;
        s.ScriptText = _lang == "Lua" ? _luaText : _dslText;
        s.LuaScriptText = _luaText;
        s.DslScriptText = _dslText;
        return s;
    }

    private static string ExtractPortName(string text)
    {
        text = (text ?? "").Trim();
        int sp = text.IndexOf(' ');
        return sp > 0 ? text.Substring(0, sp) : text;
    }

    private Encoding CurrentEncoding() => EncodingUtil.Get(EncodingCombo.SelectedItem as string ?? "UTF-8");

    public void SetAutoReplyRules(IReadOnlyList<CompiledRule> rules)
    {
        _autoReply.Encoding = CurrentEncoding();
        _autoReply.SetRules(rules);
    }

    // ------------------------------------------------------------------
    // 连接
    // ------------------------------------------------------------------

    private void RefreshPorts_Click(object sender, RoutedEventArgs e) => RefreshPorts();

    private void RefreshPorts()
    {
        string current = ExtractPortName(PortCombo.Text);
        try
        {
            var names = PortScanner.GetNames();
            PortCombo.ItemsSource = names.Select(n => new PortItem(n, "")).ToList();
            var match = ((List<PortItem>)PortCombo.ItemsSource).FirstOrDefault(p => p.Name == current);
            if (match != null) PortCombo.SelectedItem = match;
            else PortCombo.Text = current;
        }
        catch (Exception ex)
        {
            RaiseStatus("枚举串口失败：" + ex.Message, true);
        }
    }

    private void ConnTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyConnTypeUi();

    private void ApplyConnTypeUi()
    {
        var k = (ConnTypeCombo.SelectedItem as Opt<ConnectionKind>)?.Value ?? ConnectionKind.Serial;
        SerialPanel.Visibility = k == ConnectionKind.Serial ? Visibility.Visible : Visibility.Collapsed;
        NetPanel.Visibility = k == ConnectionKind.Serial ? Visibility.Collapsed : Visibility.Visible;
        bool host = k is ConnectionKind.TcpClient or ConnectionKind.Udp;
        HostLabel.Visibility = HostBox.Visibility = host ? Visibility.Visible : Visibility.Collapsed;
        RemotePortLabel.Visibility = RemotePortBox.Visibility = host ? Visibility.Visible : Visibility.Collapsed;
        bool local = k is ConnectionKind.TcpServer or ConnectionKind.Udp;
        LocalPortLabel.Visibility = LocalPortBox.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OpenBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (Core.IsOpen)
        {
            await CloseConnAsync("已关闭");
            return;
        }
        CollectSettings();
        _busy = true;
        UpdateOpenState();
        ConnStateText.Text = "正在连接…";
        try
        {
            await Core.OpenAsync();
            _formatter.ResetLine();
            _formatter.ResetDecoders();
            string desc = DescribeConnection();
            ConnStateText.Text = "已连接：" + desc;
            RaiseStatus("已打开 " + desc);
        }
        catch (Exception ex)
        {
            string msg = NetUtil.Describe(ex);
            ConnStateText.Text = "打开失败：" + msg;
            RaiseStatus("打开失败：" + msg, true);
        }
        finally
        {
            _busy = false;
            UpdateOpenState();
        }
    }

    private string DescribeConnection()
    {
        var s = Core.Settings;
        return Core.Connection switch
        {
            TcpServerConnection srv => $"TCP 服务器 端口 {srv.LocalPort}",
            UdpConnection udp => $"UDP 本地 {udp.LocalPort}" + (string.IsNullOrWhiteSpace(s.RemoteHost) ? "" : $" → {s.RemoteHost}:{s.RemotePort}"),
            TcpClientConnection => $"TCP {s.RemoteHost}:{s.RemotePort}",
            _ => $"{s.PortName} {s.BaudRate} {s.DataBits}{s.Parity.ToString()[0]}{(s.StopBits == StopBits.One ? "1" : s.StopBits == StopBits.Two ? "2" : "1.5")}",
        };
    }

    public async Task CloseConnAsync(string message, bool error = false)
    {
        if (!Core.IsOpen) return;
        StopTimer();
        _busy = true;
        UpdateOpenState();
        await Core.CloseAsync();
        _busy = false;
        ConnStateText.Text = error ? message : "未连接";
        UpdateOpenState();
        RaiseStatus(message, error);
    }

    private void UpdateOpenState()
    {
        bool open = Core.IsOpen;
        OpenBtn.Content = _busy ? "请稍候…" : open ? "关闭" : "打开";
        OpenBtn.IsEnabled = !_busy;
        OpenBtn.Style = (Style)FindResource(open ? "DangerButton" : "AccentButton");
        ConnTypeCombo.IsEnabled = SerialPanel.IsEnabled = NetPanel.IsEnabled = !open && !_busy;
        SendBtn.IsEnabled = open;
    }

    // ------------------------------------------------------------------
    // 显示
    // ------------------------------------------------------------------

    private void DisplayOption_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        ApplyDisplayOptions();
    }

    private void ApplyDisplayOptions()
    {
        var sb = new StringBuilder();
        _formatter.BreakLine(sb);
        if (sb.Length > 0) AppendText(sb.ToString());
        _formatter.Hex = HexDisplayChk.IsChecked == true;
        _formatter.Timestamp = TimestampChk.IsChecked == true;
        _formatter.ShowTx = ShowTxChk.IsChecked == true;
        if (int.TryParse(FrameGapBox.Text.Trim(), out int gap) && gap >= 0) _formatter.FrameGapMs = gap;
        _formatter.Encoding = CurrentEncoding();
    }

    private void EncodingCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _formatter.Encoding = CurrentEncoding();
        _formatter.ResetDecoders();
        _at = null;
        _autoReply.Encoding = CurrentEncoding();
    }

    private void ProtocolCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        ApplyProtocolUi();
    }

    private string Mode => ProtocolCombo.SelectedItem as string ?? "普通";

    private void ApplyProtocolUi()
    {
        bool plain = Mode == "普通";
        RxBox.Visibility = plain ? Visibility.Visible : Visibility.Collapsed;
        LineList.Visibility = plain ? Visibility.Collapsed : Visibility.Visible;
        RxTitle.Text = plain ? "接收区" : $"接收区（{Mode} 着色）";
        _at = null;
        _modbus.Reset();
    }

    private void Flush()
    {
        if (_disposed) return;
        var q = Core.Output;
        if (!q.IsEmpty)
        {
            var sb = new StringBuilder();
            string mode = Mode;
            long budget = 0;
            bool addedLines = false;
            while (budget < 256 * 1024 && q.TryDequeue(out var c))
            {
                budget += c.Data.Length;
                if (c.IsTx) _tx += c.Data.Length; else _rx += c.Data.Length;
                try { _rec?.Write(c.IsTx, c.Data, c.Time, c.Source); } catch (Exception ex) { StopRecording("录制写入失败：" + ex.Message); }
                if (!c.IsTx)
                {
                    try { _dsl?.FeedRxBytes(c.Data); } catch { /* ignore */ }
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
                    if (c.IsTx && ShowTxChk.IsChecked != true) continue;
                    _at ??= new AtLineParser(CurrentEncoding());
                    foreach (var line in _at.Push(c, CurrentEncoding())) { AddLine(line); addedLines = true; }
                }
                else
                {
                    if (c.IsTx && ShowTxChk.IsChecked != true) continue;
                    foreach (var frame in _modbus.Push(c))
                    {
                        AddLine(new ProtocolLine
                        {
                            Text = frame.Summary + "  " + ModbusRtuParser.FormatHex(frame.Raw),
                            Kind = frame.CrcOk ? AtLineKind.Ok : AtLineKind.Error,
                            IsTx = frame.IsTx, Time = frame.Time, IsError = !frame.CrcOk,
                            Tone = frame.CrcOk ? LineTone.Ok : LineTone.Err,
                        });
                        addedLines = true;
                    }
                }
            }
            if (sb.Length > 0)
            {
                if (PauseChk.IsChecked == true)
                {
                    _pauseBuffer.Append(sb);
                    if (_pauseBuffer.Length > MaxDisplayChars) _pauseBuffer.Remove(0, _pauseBuffer.Length - TrimDisplayChars);
                }
                else AppendText(sb.ToString());
            }
            if (addedLines && AutoScrollChk.IsChecked == true && _lines.Count > 0 && PauseChk.IsChecked != true)
                LineList.ScrollIntoView(_lines[^1]);
        }
        else if (PauseChk.IsChecked != true && _pauseBuffer.Length > 0)
        {
            AppendText(_pauseBuffer.ToString());
            _pauseBuffer.Clear();
        }
        if (_rx != _shownRx || _tx != _shownTx)
        {
            _shownRx = _rx;
            _shownTx = _tx;
            CountText.Text = $"RX: {_rx:N0}   TX: {_tx:N0}";
        }
    }

    private void AddLine(ProtocolLine line)
    {
        _lines.Add(line);
        if (_lines.Count > MaxLines)
            for (int i = 0; i < 500; i++) _lines.RemoveAt(0);
    }

    private void AppendText(string text)
    {
        if (text.Length == 0) return;
        if (_displayLen + text.Length > MaxDisplayChars)
        {
            string combined = RxBox.Text + text;
            string kept = combined.Substring(Math.Max(0, combined.Length - TrimDisplayChars));
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
        _lines.Clear();
    }

    private void SaveRx_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "文本文件|*.txt|所有文件|*.*", FileName = $"{SessionName}_{DateTime.Now:yyyyMMdd_HHmmss}.txt" };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            string text = Mode == "普通" ? RxBox.Text : string.Join(Environment.NewLine, _lines.Select(l => l.Text));
            File.WriteAllText(dlg.FileName, text, new UTF8Encoding(true));
            RaiseStatus("已保存：" + dlg.FileName);
        }
        catch (Exception ex)
        {
            RaiseStatus("保存失败：" + ex.Message, true);
        }
    }

    private void ResetCounters_Click(object sender, RoutedEventArgs e)
    {
        _rx = _tx = _replies = 0;
        Core.ResetCounters();
        ReplyCountText.Text = "";
    }

    // ------------------------------------------------------------------
    // 发送
    // ------------------------------------------------------------------

    private bool SendNow(string content, bool hex)
    {
        CollectSettings();
        if (!Core.IsOpen)
        {
            RaiseStatus("请先打开本会话的连接", true);
            return false;
        }
        if (!Core.Send(content, hex, out var err))
        {
            RaiseStatus(err ?? "发送失败", true);
            return false;
        }
        return true;
    }

    private void SendBtn_Click(object sender, RoutedEventArgs e) => SendNow(SendBox.Text, HexSendChk.IsChecked == true);

    private void SendBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((e.Key == Key.Enter || e.Key == Key.Return) && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            SendNow(SendBox.Text, HexSendChk.IsChecked == true);
            e.Handled = true;
        }
    }

    private void ClearSend_Click(object sender, RoutedEventArgs e) => SendBox.Clear();

    private void TimerChk_Click(object sender, RoutedEventArgs e)
    {
        if (TimerChk.IsChecked == true)
        {
            if (!Core.IsOpen || !int.TryParse(IntervalBox.Text.Trim(), out int iv) || iv < 1)
            {
                TimerChk.IsChecked = false;
                RaiseStatus(Core.IsOpen ? "定时间隔无效" : "请先打开本会话的连接", true);
                return;
            }
            _sendTimer.Interval = TimeSpan.FromMilliseconds(iv);
            _sendTimer.Start();
        }
        else StopTimer();
    }

    private void StopTimer()
    {
        _sendTimer.Stop();
        TimerChk.IsChecked = false;
    }

    private async void FireAutoReply(HighlightRule rule)
    {
        try
        {
            if (rule.ReplyDelayMs > 0) await Task.Delay(Math.Min(rule.ReplyDelayMs, 600_000));
            if (_disposed || !Core.IsOpen || AutoReplyChk.IsChecked != true) return;
            if (SendNow(rule.ReplyContent, rule.ReplyIsHex))
            {
                _replies++;
                ReplyCountText.Text = $"自动回复 {_replies} 次";
            }
        }
        catch (Exception ex)
        {
            App.LogCrash(ex);
        }
    }

    // ------------------------------------------------------------------
    // 录制
    // ------------------------------------------------------------------

    private void RecordBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_rec != null)
        {
            StopRecording("录制已停止");
            return;
        }
        var dlg = new SaveFileDialog { Filter = "SerialTool 会话录制|*.stsession|所有文件|*.*", FileName = $"{SessionName}_{DateTime.Now:yyyyMMdd_HHmmss}.stsession" };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            _rec = SessionRecorder.Create(dlg.FileName, $"{SessionName} {DescribeConnection()}");
            RecordBtn.Content = "停止录制";
            RaiseStatus("开始录制：" + dlg.FileName);
        }
        catch (Exception ex)
        {
            RaiseStatus("无法创建录制文件：" + ex.Message, true);
        }
    }

    private void StopRecording(string message)
    {
        var r = _rec;
        _rec = null;
        if (r == null) return;
        try { r.Dispose(); } catch { /* ignore */ }
        RecordBtn.Content = "开始录制…";
        RaiseStatus($"{message}（{r.Count} 条，{r.Bytes} 字节）");
    }

    // ------------------------------------------------------------------
    // 脚本
    // ------------------------------------------------------------------

    private void RefreshSamples()
    {
        var items = new List<string> { "插入示例…" };
        if (_lang == "Lua") items.AddRange(LuaSamples.All.Select(x => x.Name));
        else items.AddRange(new[] { "AT 轮询", "Modbus 读取" });
        ScriptSampleCombo.ItemsSource = items;
        ScriptSampleCombo.SelectedIndex = 0;
    }

    private void ScriptSample_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ScriptSampleCombo.SelectedIndex <= 0) return;
        int i = ScriptSampleCombo.SelectedIndex - 1;
        if (_lang == "Lua") ScriptBox.Text = LuaSamples.All[i].Code;
        else ScriptBox.Text = i == 0 ? ScriptEngine.SampleAtPoll : ScriptEngine.SampleModbusRead;
        ScriptSampleCombo.SelectedIndex = 0;
    }

    private void ScriptLang_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        string next = ScriptLangCombo.SelectedItem as string ?? "DSL";
        if (next == _lang) return;
        if (_lang == "Lua") _luaText = ScriptBox.Text; else _dslText = ScriptBox.Text;
        _lang = next;
        _loading = true;
        ScriptBox.Text = _lang == "Lua" ? _luaText : _dslText;
        RefreshSamples();
        _loading = false;
    }

    private async void ScriptRun_Click(object sender, RoutedEventArgs e)
    {
        ScriptOutBox.Clear();
        ScriptStatusText.Text = "";
        if (_lang == "Lua")
        {
            if (_lua?.IsRunning == true) return;
            var lua = new LuaScriptHost(Channel, CurrentEncoding());
            _lua = lua;
            lua.Output += m => Dispatcher.BeginInvoke(() => AppendScriptOut(m));
            lua.StatusChanged += st => Dispatcher.BeginInvoke(() => ScriptStatusText.Text = string.IsNullOrEmpty(st) ? "" : "status = " + st);
            SetScriptRunning(true);
            var r = await lua.RunAsync(ScriptBox.Text);
            AppendScriptOut(r.ToString());
            SetScriptRunning(false);
        }
        else
        {
            if (_dsl == null || _dsl.IsRunning) return;
            try { await _dsl.RunAsync(ScriptBox.Text); }
            catch (Exception ex) { AppendScriptOut("启动失败：" + ex.Message); }
        }
    }

    private void ScriptStop_Click(object sender, RoutedEventArgs e)
    {
        _dsl?.Stop();
        _lua?.Stop();
    }

    private void SetScriptRunning(bool running)
    {
        ScriptRunBtn.IsEnabled = !running;
        ScriptStopBtn.IsEnabled = running;
        ScriptLangCombo.IsEnabled = !running;
    }

    private void AppendScriptOut(string m)
    {
        ScriptOutBox.AppendText(m + "\r\n");
        if (ScriptOutBox.Text.Length > 200_000) ScriptOutBox.Text = ScriptOutBox.Text.Substring(ScriptOutBox.Text.Length - 150_000);
        ScriptOutBox.ScrollToEnd();
    }

    bool ScriptEngine.IHost.IsPortOpen => Core.IsOpen;
    string? ScriptEngine.IHost.PortName => Core.IsOpen ? SessionName : null;
    Encoding ScriptEngine.IHost.Encoding => Dispatcher.Invoke(CurrentEncoding);

    bool ScriptEngine.IHost.SendText(string text, bool escape, ChecksumKind? checksum, out string? error) =>
        ScriptSend(text, false, escape, checksum, true, out error);

    bool ScriptEngine.IHost.SendHex(string hex, ChecksumKind? checksum, bool raw, out string? error) =>
        ScriptSend(hex, true, false, raw ? ChecksumKind.None : checksum, false, out error);

    private bool ScriptSend(string content, bool hex, bool escape, ChecksumKind? checksum, bool lineEnding, out string? error)
    {
        string? err = null;
        bool ok = Dispatcher.Invoke(() =>
        {
            var s = CollectSettings();
            if (!Core.IsOpen) { err = "会话未连接"; return false; }
            var kind = checksum ?? SessionCore.ParseChecksum(s.Checksum);
            if (!SessionCore.BuildPayload(content, hex, escape, kind, lineEnding ? s.LineEnding : null, CurrentEncoding(),
                    out var data, out _, out _, out err)) return false;
            if (data.Length == 0) { err = "发送内容为空"; return false; }
            if (!Core.SendRaw(data)) { err = "发送队列已满"; return false; }
            return true;
        });
        error = err;
        return ok;
    }

    void ScriptEngine.IHost.Log(string message) => Dispatcher.BeginInvoke(() => RaiseStatus("[脚本] " + message));

    // ------------------------------------------------------------------
    // 关闭
    // ------------------------------------------------------------------

    private void StopUiParts()
    {
        _disposed = true;
        _ui.Stop();
        _sendTimer.Stop();
        try { _dsl?.Stop(); } catch { /* ignore */ }
        try { _lua?.Stop(); } catch { /* ignore */ }
        StopRecording("录制已停止");
    }

    public async Task ShutdownAsync()
    {
        if (_disposed) return;
        StopUiParts();
        await Core.CloseAsync();
    }

    /// <summary>窗口关闭时调用（界面线程）：停止界面部分，再最多等 2.5 秒关闭连接。</summary>
    public void ShutdownSync()
    {
        if (_disposed) return;
        StopUiParts();
        try { Task.Run(Core.CloseAsync).Wait(2500); } catch { /* ignore */ }
    }
}
