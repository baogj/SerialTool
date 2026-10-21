using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SerialTool.Features;

namespace SerialTool;

public partial class MainWindow
{
    internal static readonly List<Opt<ChecksumKind>> ChecksumOptions =
        Checksum.Options.Select(o => new Opt<ChecksumKind>(o.Name, o.Kind)).ToList();

    private static readonly List<Opt<ThemeMode>> ThemeOptions =
        ThemeManager.Options.Select(o => new Opt<ThemeMode>(o.Name, o.Mode)).ToList();

    private enum RxView { Text, PlainLines, Protocol }
    private RxView _rxView = RxView.Text;

    /// <summary>Window_Loaded 末尾调用：挂接 v1.2 需要的额外事件。</summary>
    private void InitV12()
    {
        HexSendChk.Click += (_, _) => UpdateChecksumPreview();
        LineEndingCombo.SelectionChanged += (_, _) => UpdateChecksumPreview();
        EncodingCombo.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            RebuildRules();
            UpdateChecksumPreview();
        };
        ThemeManager.Changed += OnThemeChanged;
    }

    // ======================================================================
    // ScriptEngine.IHost
    // ======================================================================

    bool ScriptEngine.IHost.IsPortOpen => Dispatcher.Invoke(() => _conn != null);
    string? ScriptEngine.IHost.PortName => Dispatcher.Invoke(() => _conn?.Name);
    Encoding ScriptEngine.IHost.Encoding => Dispatcher.Invoke(CurrentEncoding);

    bool ScriptEngine.IHost.SendText(string text, bool escape, ChecksumKind? checksum, out string? error) =>
        ScriptSend(text, false, escape, checksum, true, out error);

    bool ScriptEngine.IHost.SendHex(string hex, ChecksumKind? checksum, bool raw, out string? error) =>
        ScriptSend(hex, true, null, raw ? ChecksumKind.None : checksum, false, out error);

    private bool ScriptSend(string content, bool hex, bool? escape, ChecksumKind? checksum, bool lineEnding, out string? error)
    {
        string? err = null;
        bool ok = Dispatcher.Invoke(() =>
        {
            var s = _conn;
            if (s == null) { err = NotOpenMessage; return false; }
            if (!TryBuildPayload(content, hex, escape, checksum, lineEnding, out byte[] data, out int ms, out int ml, out err)) return false;
            if (data.Length == 0) { err = "发送内容为空"; return false; }
            if (!s.Send(data, ms, ml)) { err = "发送队列已满"; return false; }
            return true;
        });
        error = err;
        return ok;
    }

    void ScriptEngine.IHost.Log(string message)
    {
        Dispatcher.BeginInvoke(() => SetStatus("[脚本] " + message));
    }

    // ======================================================================
    // 协议模式（左侧与接收页顶部两个下拉框保持同步）
    // ======================================================================

    private string CurrentMode => ProtocolCombo.SelectedItem as string ?? "普通";

    private void ProtocolCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingProtocol) return;
        _syncingProtocol = true;
        try { ProtocolTopCombo.SelectedItem = ProtocolCombo.SelectedItem; }
        finally { _syncingProtocol = false; }
        if (_loading) return;
        FlushQueue();
        _modbus.Reset();
        _atParser = new AtLineParser(CurrentEncoding());
        ApplyProtocolModeUi();
        SetStatus("协议模式：" + CurrentMode);
    }

    private void ProtocolTopCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingProtocol || ProtocolTopCombo.SelectedItem == null) return;
        ProtocolCombo.SelectedItem = ProtocolTopCombo.SelectedItem; // 触发上面的处理
    }

    /// <summary>根据协议模式与高亮开关选择接收视图：原 TextBox / 着色行列表。</summary>
    private void ApplyProtocolModeUi()
    {
        string mode = CurrentMode;
        RxView target = mode != "普通" ? RxView.Protocol
            : _highlightRules.Count > 0 || _filterOn ? RxView.PlainLines
            : RxView.Text;

        if (target != _rxView)
        {
            if (_rxView == RxView.PlainLines) SyncLinesToRxBox();
            if (target == RxView.PlainLines) SeedLinesFromRxBox();
            _rxView = target;
        }
        _lineViewPlain = target == RxView.PlainLines;

        bool lines = target != RxView.Text;
        RxBox.Visibility = lines ? Visibility.Collapsed : Visibility.Visible;
        ProtocolList.Visibility = lines ? Visibility.Visible : Visibility.Collapsed;
        FrameText.Visibility = mode == "Modbus RTU" ? Visibility.Visible : Visibility.Collapsed;
        if (mode != "Modbus RTU") FrameText.Text = "";
        ViewHint.Text = target switch
        {
            RxView.PlainLines => _filterOn ? "行视图（过滤中）" : "高亮视图",
            RxView.Protocol => _highlightRules.Count > 0 ? "着色 + 高亮" : "着色视图",
            _ => "",
        };
        ScrollLinesToEnd();
    }

    /// <summary>离开普通着色行视图时，把行内容写回 TextBox，保证切回后内容不丢。</summary>
    private void SyncLinesToRxBox()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < _protocolLines.Count; i++)
        {
            sb.Append(_protocolLines[i].Text);
            if (i < _protocolLines.Count - 1 || !_hasOpenItem) sb.Append("\r\n");
        }
        string text = sb.Length > TrimDisplayChars ? sb.ToString(sb.Length - TrimDisplayChars, TrimDisplayChars) : sb.ToString();
        RxBox.Text = text;
        _displayLen = text.Length;
        _protocolLines.Clear();
        ResetOpenLine();
        if (AutoScrollChk.IsChecked == true) RxBox.ScrollToEnd();
    }

    /// <summary>进入普通着色行视图时，用 TextBox 现有内容（末尾部分）填充行列表。</summary>
    private void SeedLinesFromRxBox()
    {
        _protocolLines.Clear();
        ResetOpenLine();
        string text = RxBox.Text;
        if (text.Length > 200_000)
        {
            int start = text.IndexOf('\n', text.Length - 200_000);
            text = start >= 0 ? text.Substring(start + 1) : text.Substring(text.Length - 200_000);
        }
        if (text.Length > 0) AppendPlainToLines(text);
    }

    private void AppendProtocolLine(ProtocolLine line)
    {
        WriteLog(line.Text + "\r\n");
        if (PauseChk.IsChecked == true)
        {
            _pausedLines.Add(line);
            if (_pausedLines.Count > MaxProtocolLines) _pausedLines.RemoveRange(0, _pausedLines.Count - MaxProtocolLines);
            return;
        }
        AddLineItem(line);
    }

    private void AddLineItem(ProtocolLine line)
    {
        if (_hasOpenItem) ResetOpenLine(); // 协议行不会与普通未完成行混在一起
        _protocolLines.Add(line);
        TrimLines();
    }

    private void TrimLines()
    {
        int over = _protocolLines.Count - MaxProtocolLines;
        if (over <= 0) return;
        if (over > 500)
        {
            // 一次超出很多时整体重建，避免逐条 RemoveAt(0) 的 O(n²)
            _protocolLines = new ObservableCollection<ProtocolLine>(_protocolLines.Skip(over));
            ProtocolList.ItemsSource = _protocolLines;
            ApplyLineFilter();
        }
        else
        {
            while (over-- > 0) _protocolLines.RemoveAt(0);
        }
    }

    // ---------- 普通模式（有高亮规则时）的行拆分 ----------

    private void ResetOpenLine()
    {
        _openLine.Clear();
        _hasOpenItem = false;
        _pendingCr = false;
    }

    private void AppendPlainToLines(string text)
    {
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == '\n' && _pendingCr)
            {
                _pendingCr = false; // \r\n 中的 \n（\r 可能在上一块末尾）
                start = i + 1;
                continue;
            }
            _pendingCr = false;
            if (ch == '\r' || ch == '\n')
            {
                _openLine.Append(text, start, i - start);
                CommitOpenLine(true);
                _pendingCr = ch == '\r';
                start = i + 1;
            }
            else if (_openLine.Length + (i - start) >= MaxOpenLineChars)
            {
                _openLine.Append(text, start, i - start);
                CommitOpenLine(true); // 超长行（如无分帧的 HEX）强制折行
                start = i;
            }
        }
        if (start < text.Length)
        {
            _openLine.Append(text, start, text.Length - start);
            CommitOpenLine(false);
        }
    }

    private void CommitOpenLine(bool final)
    {
        var line = new ProtocolLine { Text = _openLine.ToString(), Tone = LineTone.Plain, Time = DateTime.Now };
        if (_hasOpenItem && _protocolLines.Count > 0)
            _protocolLines[_protocolLines.Count - 1] = line;
        else
            _protocolLines.Add(line);
        _hasOpenItem = !final;
        if (final) _openLine.Clear();
        TrimLines();
    }

    private void ProtocolList_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.C &&
            (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
        {
            CopySelectedLines_Click(sender, e);
            e.Handled = true;
        }
    }

    private void CopySelectedLines_Click(object sender, RoutedEventArgs e)
    {
        var sel = ProtocolList.SelectedItems.Cast<ProtocolLine>().OrderBy(l => _protocolLines.IndexOf(l)).ToList();
        if (sel.Count == 0) { SetStatus("请先选择要复制的行（可 Shift/Ctrl 多选）", true); return; }
        CopyLines(sel);
    }

    private void CopyAllLines_Click(object sender, RoutedEventArgs e) => CopyLines(_protocolLines.ToList());

    private void CopyLines(List<ProtocolLine> lines)
    {
        try
        {
            Clipboard.SetText(string.Join("\r\n", lines.Select(l => l.Text)));
            SetStatus($"已复制 {lines.Count} 行");
        }
        catch (Exception ex)
        {
            SetStatus("复制失败：" + ex.Message, true);
        }
    }

    private void ScrollLinesToEnd()
    {
        if (ProtocolList.Visibility != Visibility.Visible || AutoScrollChk.IsChecked != true) return;
        if (_searchHold) return; // 正在查看查找结果时不自动滚到底
        int n = ProtocolList.Items.Count;
        if (n > 0) ProtocolList.ScrollIntoView(ProtocolList.Items[n - 1]);
    }

    // ======================================================================
    // 关键字高亮 / 自动回复
    // ======================================================================

    /// <summary>规则或编码变化：重新编译，刷新高亮与自动回复。</summary>
    private void RebuildRules()
    {
        var enc = CurrentEncoding();
        var rules = _settings.HighlightRules;
        _compiledRules = rules.Select((r, i) => new CompiledRule(r, i, enc)).ToList();
        bool hlOn = HighlightChk.IsChecked == true;
        _highlightRules = hlOn ? _compiledRules.Where(c => c.Rule.IsUsable && c.IsValid).ToList() : new List<CompiledRule>();
        HighlightStyles.Set(rules, _highlightRules);

        _autoReply.Encoding = enc;
        _autoReply.SetRules(_compiledRules
            .Where(c => c.Rule.IsUsable && c.IsValid && c.Rule.ReplyEnabled && !string.IsNullOrEmpty(c.Rule.ReplyContent))
            .ToList());

        int total = rules.Count(r => r.IsUsable);
        int replies = rules.Count(r => r.IsUsable && r.ReplyEnabled && !string.IsNullOrEmpty(r.ReplyContent));
        int bad = _compiledRules.Count(c => c.Rule.IsUsable && !c.IsValid);
        RulesSummaryText.Text = $"已启用规则 {total} 条（含自动回复 {replies} 条）" + (bad > 0 ? $"，{bad} 条有错误已忽略" : "");

        if (!_loading)
        {
            ProtocolList.Items.Refresh(); // 可见行按新规则重新着色
            MonitorList.Items.Refresh();
            ApplyProtocolModeUi();
            UpdateSessionRulesV15();
        }
    }

    private void HighlightChk_Click(object sender, RoutedEventArgs e)
    {
        FlushQueue();
        RebuildRules();
        SetStatus(HighlightChk.IsChecked == true ? "关键字高亮已开启" : "关键字高亮已关闭");
    }

    private void AutoReplyChk_Click(object sender, RoutedEventArgs e)
    {
        bool on = AutoReplyChk.IsChecked == true;
        AutoReplyBadge.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        RebuildRules(); // 重置扫描位置：只对开启之后收到的数据回复
        SetStatus(on ? "自动回复已开启（每条规则有冷却时间，全局每秒最多 20 次）" : "自动回复已关闭");
    }

    private void EditRules_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new HighlightRulesWindow(_settings.HighlightRules, CurrentEncoding()) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _settings.HighlightRules = dlg.Result;
        FlushQueue();
        RebuildRules();
        CollectSettings();
        SettingsStore.Save(_settings);
        SetStatus($"规则已保存（{dlg.Result.Count} 条）");
    }

    private async void FireAutoReply(HighlightRule rule)
    {
        try
        {
            if (rule.ReplyDelayMs > 0) await Task.Delay(Math.Min(rule.ReplyDelayMs, 600_000));
            if (_closingWindow || _conn == null || AutoReplyChk.IsChecked != true) return;
            if (SendNow(rule.ReplyContent, rule.ReplyIsHex, false))
            {
                _autoReplyCount++;
                AutoReplyCountText.Text = $"自动回复 {_autoReplyCount}";
                string name = string.IsNullOrWhiteSpace(rule.Name) ? rule.Pattern : rule.Name;
                SetStatus($"自动回复「{name}」→ {(rule.ReplyIsHex ? "[HEX] " : "")}{rule.ReplyContent}");
            }
        }
        catch (Exception ex)
        {
            App.LogCrash(ex);
        }
    }

    // ======================================================================
    // 发送校验
    // ======================================================================

    private void ChecksumCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        UpdateChecksumPreview();
        SetStatus("发送校验：" + Checksum.DisplayName(CurrentChecksum()));
    }

    private void ChecksumInput_Changed(object sender, RoutedEventArgs e) => UpdateChecksumPreview();

    private void SendBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateChecksumPreview();

    /// <summary>发送设置下方的小标签：显示当前发送区内容按所选算法得到的校验字节。</summary>
    private void UpdateChecksumPreview()
    {
        if (ChecksumPreview == null || ChecksumCombo == null) return;
        var kind = CurrentChecksum();
        string text = SendBox.Text;
        if (kind == ChecksumKind.None)
        {
            ChecksumPreview.Text = "不追加校验";
            ChecksumPreview.ToolTip = null;
            return;
        }
        if (text.Length == 0)
        {
            ChecksumPreview.Text = "校验值：—（发送区为空）";
            return;
        }
        if (text.Length > 1_000_000)
        {
            ChecksumPreview.Text = "校验值：内容过大，发送时计算";
            return;
        }
        if (!TryBuildBody(text, HexSendChk.IsChecked == true, null, out byte[] body, out string? err))
        {
            ChecksumPreview.Text = "校验值：" + (err ?? "内容无效");
            return;
        }
        byte[] c = Checksum.Compute(kind, body);
        ChecksumPreview.Text = $"校验值：{Checksum.ToHex(c)}（{body.Length} 字节）";
        string le = (LineEndingCombo.SelectedItem as Opt<string>)?.Value ?? "";
        ChecksumPreview.ToolTip = "实际发送：" + Checksum.ToHex(Checksum.Append(kind, body).Concat(Encoding.ASCII.GetBytes(le)).Take(64).ToArray())
                                  + (body.Length + c.Length + le.Length > 64 ? " …" : "");
    }

    private void ChecksumCalc_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ChecksumCalculatorWindow(SendBox.Text, HexSendChk.IsChecked == true, EscapeChk.IsChecked == true,
            CurrentEncoding(), CurrentChecksum()) { Owner = this };
        dlg.ShowDialog();
    }

    // ======================================================================
    // 主题
    // ======================================================================

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeCombo.SelectedItem is not Opt<ThemeMode> o) return;
        ThemeManager.Apply(o.Value);
        _settings.Theme = o.Value.ToString();
        SetStatus("主题：" + o.Text + (o.Value == ThemeMode.System ? (ThemeManager.IsDark ? "（当前系统为深色）" : "（当前系统为浅色）") : ""));
    }

    private void OnThemeChanged()
    {
        ProtocolList.Items.Refresh();
        MonitorList.Items.Refresh();
        _waveElement?.InvalidateVisual();
        _curveElement?.InvalidateVisual();
        RefreshMonitorColors();
    }

    // ======================================================================
    // 波形
    // ======================================================================

    private void WaveOption_Click(object sender, RoutedEventArgs e)
    {
        _waveBuf.ShowTx = WaveShowTxChk.IsChecked == true;
        _waveElement?.InvalidateVisual();
    }

    private void WavePause_Click(object sender, RoutedEventArgs e)
    {
        _waveBuf.Paused = WavePauseChk.IsChecked == true;
        _waveElement?.InvalidateVisual();
    }

    private void WaveWindow_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (WaveWindowCombo.SelectedItem is int n)
        {
            _waveBuf.WindowLength = n;
            _waveElement?.InvalidateVisual();
        }
    }

    private void WaveClear_Click(object sender, RoutedEventArgs e)
    {
        _waveBuf.Clear();
        _waveElement?.InvalidateVisual();
    }

    // ======================================================================
    // 脚本
    // ======================================================================

    private async void ScriptRun_Click(object sender, RoutedEventArgs e)
    {
        if (TryRunLuaMain()) return;
        if (_script == null) return;
        if (_script.IsRunning)
        {
            SetStatus("脚本已在运行", true);
            return;
        }
        ScriptOutBox.Clear();
        try
        {
            await _script.RunAsync(ScriptBox.Text);
        }
        catch (Exception ex)
        {
            ScriptOutBox.AppendText("启动失败：" + ex.Message + "\r\n");
        }
    }

    private void ScriptStop_Click(object sender, RoutedEventArgs e)
    {
        _script?.Stop();
        _luaMain?.Stop();
    }

    private void ScriptSampleAt_Click(object sender, RoutedEventArgs e) => ScriptBox.Text = _scriptLang == "Lua" ? LuaSamples.AtTest : ScriptEngine.SampleAtPoll;

    private void ScriptSampleModbus_Click(object sender, RoutedEventArgs e) => ScriptBox.Text = _scriptLang == "Lua" ? LuaSamples.ModbusPoll : ScriptEngine.SampleModbusRead;

    // ======================================================================
    // 吞吐率
    // ======================================================================

    private void UpdateThroughputUi()
    {
        _meter.Tick();
        RateText.Text = $"RX: {ThroughputMeter.FormatRate(_meter.RxRateBps)}  TX: {ThroughputMeter.FormatRate(_meter.TxRateBps)}";
        PeakText.Text = $"峰值 RX {ThroughputMeter.FormatRate(_meter.RxPeakBps)} / TX {ThroughputMeter.FormatRate(_meter.TxPeakBps)}";
        var el = _meter.SessionElapsed;
        SessionText.Text = $"会话 {(int)el.TotalHours:00}:{el.Minutes:00}:{el.Seconds:00}";
        FrameText.Text = CurrentMode == "Modbus RTU" ? $"帧 OK {_meter.FrameOk} / ERR {_meter.FrameErr}" : "";
        RateText.ToolTip = $"平均 RX {ThroughputMeter.FormatRate(_meter.RxAvgBps)} / TX {ThroughputMeter.FormatRate(_meter.TxAvgBps)}";
    }
}
