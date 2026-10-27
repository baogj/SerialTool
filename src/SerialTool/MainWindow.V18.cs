using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SerialTool.Features;
using TriggerAction = SerialTool.Features.TriggerAction;

namespace SerialTool;

/// <summary>v1.8：工具菜单（数据转换 / 加密计算 / 数据对比 / 接收触发 / 检查更新）、置顶、迷你模式、透明度。</summary>
public partial class MainWindow
{
    private readonly TriggerEngine _triggers = new();
    private List<TriggerRule> _triggerRules = new();
    private readonly List<TriggerHit> _pendingHits = new();
    private LuaScriptHost? _triggerLua;

    /// <summary>最近收到的原始字节（供数据对比「取接收区」），最多 1 MB。</summary>
    private readonly List<byte> _rxRaw = new();
    private const int RxRawMax = 1024 * 1024;

    private MiniWindow? _mini;
    private TrayNotifier? _tray;
    private DataConvertWindow? _convertWin;
    private CryptoCalcWindow? _cryptoWin;
    private DataDiffWindow? _diffWin;
    private UpdateWindow? _updateWin;
    private ReleaseInfo? _pendingRelease;

    private string TriggerPath => TriggerRuleStore.PathIn(Path.GetDirectoryName(SettingsStore.FilePath) ?? AppContext.BaseDirectory);
    private string DataDir => Path.GetDirectoryName(SettingsStore.FilePath) ?? AppContext.BaseDirectory;

    internal string AppVersion
    {
        get
        {
            var v = typeof(MainWindow).Assembly.GetName().Version;
            return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    internal bool IsConnectionOpen => _conn != null;

    private void InitControlsV18()
    {
        var s = _settings;
        _triggerRules = TriggerRuleStore.Load(TriggerPath);
        _triggers.SetRules(_triggerRules, CurrentEncoding());
        TopMostChk.IsChecked = s.AlwaysOnTop;
        Topmost = s.AlwaysOnTop;
        TriggerMenuEnabled.IsChecked = s.TriggersEnabled;
        UpdateTriggerBadge();
        SourceInitialized += (_, _) => ApplyOpacityTo(this);
        foreach (var mi in OpacityMenu.Items.OfType<MenuItem>())
            mi.IsChecked = Math.Abs(double.Parse((string)mi.Tag, System.Globalization.CultureInfo.InvariantCulture) - s.WindowOpacity) < 0.01;
    }

    private void CollectSettingsV18()
    {
        var s = _settings;
        s.AlwaysOnTop = TopMostChk.IsChecked == true;
        s.TriggersEnabled = TriggerMenuEnabled.IsChecked == true;
        SaveMiniBounds();
    }

    private void InitV18()
    {
        // 清理上次自动更新留下的 .old
        try { if (Environment.ProcessPath is string exe) UpdateChecker.CleanupOld(exe); } catch { }
        if (UpdateChecker.ShouldAutoCheck(_settings.CheckUpdateOnStartup, _settings.LastUpdateCheck, DateTime.Now))
            _ = AutoCheckUpdateAsync();
    }

    private void ShutdownV18()
    {
        try { if (_mini != null) { SaveMiniBounds(); _mini.CloseForRestore(); _mini = null; } } catch { }
        try { _triggerLua?.Stop(); } catch { }
        try { TriggerRuleStore.Save(TriggerPath, _triggerRules); } catch (Exception ex) { App.LogCrash(ex); }
        try { _tray?.Dispose(); _tray = null; } catch { }
        foreach (var w in new Window?[] { _convertWin, _cryptoWin, _diffWin, _updateWin })
            try { w?.Close(); } catch { }
    }

    // ======================================================================
    // FlushQueue 钩子
    // ======================================================================

    /// <summary>每个数据块（含回放）调用：喂迷你窗口与原始接收缓存。</summary>
    private void FeedV18(DataChunk c)
    {
        if (c.IsTx) return;
        _rxRaw.AddRange(c.Data);
        if (_rxRaw.Count > RxRawMax + RxRawMax / 4) _rxRaw.RemoveRange(0, _rxRaw.Count - RxRawMax);
        _mini?.Feed(c.Data, CurrentEncoding());
    }

    /// <summary>非回放的 RX 数据：接收触发匹配（动作延后到本次刷新结束再执行）。</summary>
    private void PushTriggersRx(byte[] data)
    {
        if (TriggerMenuEnabled.IsChecked != true || _triggerRules.Count == 0) return;
        var enc = CurrentEncoding();
        if (!ReferenceEquals(_triggers.Encoding, enc) && _triggers.Encoding.WebName != enc.WebName) _triggers.SetRules(_triggerRules, enc);
        var hits = _triggers.Push(data, DateTime.Now);
        if (hits.Count == 0) return;
        _pendingHits.AddRange(hits);
    }

    private void FlushV18()
    {
        if (_mini != null) _mini.Flush(_conn != null, MiniStateText());
        if (_pendingHits.Count == 0) return;
        var hits = _pendingHits.ToList();
        _pendingHits.Clear();
        // 动作可能关闭端口 / 改变界面，放到当前刷新之后执行
        Dispatcher.BeginInvoke(() =>
        {
            foreach (var h in hits)
            {
                foreach (var a in h.Rule.Actions)
                {
                    try { RunTriggerAction(h, a); }
                    catch (Exception ex) { SetStatus($"触发规则「{RuleName(h.Rule)}」动作 {TriggerAction.Label(a.Kind)} 失败：{ex.Message}", true); }
                }
            }
            UpdateTriggerBadge();
        });
    }

    private static string RuleName(TriggerRule r) => string.IsNullOrWhiteSpace(r.Name) ? r.Pattern : r.Name;

    private void RunTriggerAction(TriggerHit h, TriggerAction a)
    {
        string name = RuleName(h.Rule);
        switch (a.Kind)
        {
            case TriggerActionKind.Toast:
                Toast.Show("SerialTool · " + name, TriggerEngine.Expand(a.Param, h));
                break;
            case TriggerActionKind.TrayBalloon:
                _tray ??= new TrayNotifier(this);
                _tray.Show("SerialTool · " + name, TriggerEngine.Expand(a.Param, h));
                break;
            case TriggerActionKind.Sound:
                Win32Ui.PlaySoundSpec(a.Param);
                break;
            case TriggerActionKind.StartLog:
                if (_logWriter != null) break;
                {
                    string path = string.IsNullOrWhiteSpace(a.Param)
                        ? Path.Combine(DataDir, "logs", $"trigger_{DateTime.Now:yyyyMMdd_HHmmss}.log")
                        : Environment.ExpandEnvironmentVariables(a.Param.Trim());
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                    var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                    _logWriter = new StreamWriter(fs, new UTF8Encoding(false));
                    _logPath = path;
                    LogChk.IsChecked = true;
                    LogBadge.Visibility = Visibility.Visible;
                    SetStatus($"触发规则「{name}」：开始记录到 {path}");
                }
                break;
            case TriggerActionKind.StopLog:
                if (_logWriter == null) break;
                CloseLog();
                SetStatus($"触发规则「{name}」：已停止记录日志");
                break;
            case TriggerActionKind.Snapshot:
            {
                string dir = string.IsNullOrWhiteSpace(a.Param) ? Path.Combine(DataDir, "snapshots") : Environment.ExpandEnvironmentVariables(a.Param.Trim());
                Directory.CreateDirectory(dir);
                string safe = new string(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch).ToArray());
                if (safe.Length > 40) safe = safe.Substring(0, 40);
                string file = Path.Combine(dir, $"snapshot_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{safe}.txt");
                File.WriteAllText(file, ReceiveAreaText(), new UTF8Encoding(true));
                SetStatus($"触发规则「{name}」：接收区快照已保存 {file}");
                break;
            }
            case TriggerActionKind.PauseDisplay:
                if (PauseChk.IsChecked == true) break;
                PauseChk.IsChecked = true;
                PauseChk_Click(PauseChk, new RoutedEventArgs());
                SetStatus($"触发规则「{name}」：已暂停显示");
                break;
            case TriggerActionKind.StopSend:
                StopTimedSend($"触发规则「{name}」：已停止定时发送");
                try { _msEngine.Stop(); } catch { }
                break;
            case TriggerActionKind.ClosePort:
                if (_conn == null || _busy) break;
                _reconnectPort = null;
                _ = CloseAsync($"触发规则「{name}」：已关闭连接");
                break;
            case TriggerActionKind.RunScript:
                RunTriggerScript(a.Param, name);
                break;
        }
    }

    private void RunTriggerScript(string param, string name)
    {
        param = (param ?? "").Trim();
        if (param.Length == 0)
        {
            if (_script?.IsRunning == true || _luaMain?.IsRunning == true) return;
            ScriptRun_Click(this, new RoutedEventArgs());
            SetStatus($"触发规则「{name}」：运行脚本页脚本");
            return;
        }
        string path = Environment.ExpandEnvironmentVariables(param);
        string code = File.ReadAllText(path);
        if (path.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
        {
            if (_triggerLua?.IsRunning == true) return;
            var lua = new LuaScriptHost(_mainChannel, CurrentEncoding());
            _triggerLua = lua;
            lua.Output += m => Dispatcher.BeginInvoke(() => { ScriptOutBox.AppendText("[触发] " + m + "\r\n"); ScriptOutBox.ScrollToEnd(); });
            lua.Completed += r => Dispatcher.BeginInvoke(() => SetStatus($"触发脚本 {Path.GetFileName(path)}：{r}", r.Status == LuaRunStatus.Error));
            _ = lua.RunAsync(code);
        }
        else
        {
            if (_script == null || _script.IsRunning) return;
            _ = _script.RunAsync(code);
        }
        SetStatus($"触发规则「{name}」：运行脚本 {Path.GetFileName(path)}");
    }

    /// <summary>接收区当前内容（含暂停期间缓存的部分），与「保存」按钮一致。</summary>
    private string ReceiveAreaText() =>
        ProtocolList.Visibility == Visibility.Visible
            ? string.Join("\r\n", _protocolLines.Select(l => l.Text))
            : RxBox.Text + _pauseBuffer;

    private void UpdateTriggerBadge()
    {
        int enabled = _triggerRules.Count(r => r.IsUsable);
        long hits = _triggerRules.Sum(r => r.HitCount);
        TriggerMenuItem.Header = $"接收触发规则…（{enabled} 条启用，命中 {hits}）";
    }

    // ======================================================================
    // 顶栏：工具菜单 / 置顶 / 迷你模式 / 透明度
    // ======================================================================

    private void ToolsBtn_Click(object sender, RoutedEventArgs e)
    {
        UpdateTriggerBadge();
        ToolsBtn.ContextMenu.PlacementTarget = ToolsBtn;
        ToolsBtn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        ToolsBtn.ContextMenu.IsOpen = true;
    }

    private void TopMostChk_Click(object sender, RoutedEventArgs e)
    {
        Topmost = TopMostChk.IsChecked == true;
        _settings.AlwaysOnTop = Topmost;
        SetStatus(Topmost ? "窗口已置顶" : "已取消置顶");
    }

    private void Opacity_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi) return;
        double v = double.Parse((string)mi.Tag, System.Globalization.CultureInfo.InvariantCulture);
        _settings.WindowOpacity = v;
        foreach (var x in OpacityMenu.Items.OfType<MenuItem>()) x.IsChecked = ReferenceEquals(x, mi);
        ApplyOpacityTo(this);
        if (_mini != null) ApplyOpacityTo(_mini);
        SetStatus($"窗口不透明度 {v * 100:F0}%");
    }

    internal void ApplyOpacityTo(Window w)
    {
        if (!Win32Ui.SetOpacity(w, _settings.WindowOpacity) && _settings.WindowOpacity < 0.999)
            SetStatus("设置透明度失败（系统不支持分层窗口）", true);
    }

    private void MiniBtn_Click(object sender, RoutedEventArgs e) => EnterMiniMode();

    private void EnterMiniMode()
    {
        if (_mini != null) { _mini.Activate(); return; }
        var s = _settings;
        var m = new MiniWindow(this, s.MiniHex, s.MiniTopmost) { MaxLines = s.MiniLines, Width = s.MiniWidth, Height = s.MiniHeight };
        if (s.MiniLeft is double l && s.MiniTop is double t && OnScreen(l, t, m.Width, m.Height))
        {
            m.WindowStartupLocation = WindowStartupLocation.Manual;
            m.Left = l;
            m.Top = t;
        }
        else
        {
            var wa = SystemParameters.WorkArea;
            m.WindowStartupLocation = WindowStartupLocation.Manual;
            m.Left = wa.Right - m.Width - 20;
            m.Top = wa.Bottom - m.Height - 20;
        }
        _mini = m;
        // 预填最近的接收数据
        int take = Math.Min(_rxRaw.Count, 4096);
        if (take > 0) m.Feed(_rxRaw.GetRange(_rxRaw.Count - take, take).ToArray(), CurrentEncoding());
        m.Show();
        m.Flush(_conn != null, MiniStateText());
        Hide();
    }

    private static bool OnScreen(double l, double t, double w, double h)
    {
        double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;
        double vr = vl + SystemParameters.VirtualScreenWidth, vb = vt + SystemParameters.VirtualScreenHeight;
        return l + 60 < vr && l + w - 60 > vl && t >= vt - 10 && t + 30 < vb;
    }

    private void SaveMiniBounds()
    {
        var m = _mini;
        if (m == null) return;
        var s = _settings;
        var b = m.WindowState == WindowState.Normal ? new Rect(m.Left, m.Top, m.Width, m.Height) : m.RestoreBounds;
        if (b.IsEmpty) return;
        s.MiniLeft = b.Left;
        s.MiniTop = b.Top;
        s.MiniWidth = b.Width;
        s.MiniHeight = b.Height;
        s.MiniHex = m.HexMode;
        s.MiniTopmost = m.TopmostChecked;
    }

    internal void ExitMiniMode(bool fromMini)
    {
        var m = _mini;
        if (m == null) return;
        SaveMiniBounds();
        _mini = null;
        if (!fromMini) m.CloseForRestore();
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private string MiniStateText()
    {
        var c = _conn;
        if (c == null) return CurrentConnKind() == ConnectionKind.Serial ? $"{(PortCombo.SelectedItem as PortItem)?.Name ?? "未选择端口"} · 未打开" : "未连接";
        return $"● {c.Name}  RX {_rxTotal}  TX {_txTotal}";
    }

    internal void ToggleOpenFromMini() => OpenBtn_Click(OpenBtn, new RoutedEventArgs());

    internal bool SendFromMini(string text, bool hex)
    {
        bool ok = SendNow(text, hex, true);
        if (!ok && _mini != null) _mini.Flush(_conn != null, _conn == null ? NotOpenMessage : "发送失败：" + StatusText.Text);
        return ok;
    }

    // ---------------- 工具窗口 ----------------

    private string EncName => EncodingCombo.SelectedItem as string ?? "UTF-8";

    private static T ShowTool<T>(ref T? field, Func<T> create, Window owner) where T : Window
    {
        if (field != null && field.IsLoaded)
        {
            if (field.WindowState == WindowState.Minimized) field.WindowState = WindowState.Normal;
            field.Activate();
            return field;
        }
        var w = create();
        w.Owner = owner;
        field = w;
        w.Show();
        return w;
    }

    private void ToolConvert_Click(object sender, RoutedEventArgs e)
    {
        string? sel = RxBox.SelectedText;
        string? hex = !string.IsNullOrWhiteSpace(sel) && DataConvert.TryParseHex(sel, out var b, out _) && b.Length > 0 ? sel : null;
        var w = ShowTool(ref _convertWin, () => new DataConvertWindow(hex, EncName), this);
        w.Closed += (_, _) => _convertWin = null;
    }

    private void ToolCrypto_Click(object sender, RoutedEventArgs e)
    {
        var w = ShowTool(ref _cryptoWin, () => new CryptoCalcWindow(EncName), this);
        w.Closed += (_, _) => _cryptoWin = null;
    }

    private void ToolDiff_Click(object sender, RoutedEventArgs e)
    {
        var w = ShowTool(ref _diffWin, () => new DataDiffWindow(() => _rxRaw.ToArray(), EncName), this);
        w.Closed += (_, _) => _diffWin = null;
    }

    private void ToolTriggers_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new TriggerRulesWindow(_triggerRules, TriggerMenuEnabled.IsChecked == true, CurrentEncoding()) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _triggerRules = dlg.Result;
        TriggerMenuEnabled.IsChecked = dlg.GlobalEnabled;
        _settings.TriggersEnabled = dlg.GlobalEnabled;
        _triggers.SetRules(_triggerRules, CurrentEncoding());
        try { TriggerRuleStore.Save(TriggerPath, _triggerRules); }
        catch (Exception ex) { SetStatus("保存触发规则失败：" + ex.Message, true); return; }
        UpdateTriggerBadge();
        if (_triggers.Errors.Count > 0) SetStatus($"已保存触发规则；{_triggers.Errors.Count} 条匹配内容无效，不会触发", true);
        else SetStatus($"已保存 {_triggerRules.Count} 条接收触发规则" + (dlg.GlobalEnabled ? "" : "（总开关已关闭）"));
    }

    private void TriggerEnabled_Click(object sender, RoutedEventArgs e)
    {
        _settings.TriggersEnabled = TriggerMenuEnabled.IsChecked == true;
        SetStatus(_settings.TriggersEnabled ? "接收触发已启用" : "接收触发已关闭");
    }

    private void ToolUpdate_Click(object sender, RoutedEventArgs e)
    {
        var pre = _pendingRelease;
        var w = ShowTool(ref _updateWin, () => new UpdateWindow(_settings, AppVersion, pre), this);
        w.Closed += (_, _) => { _updateWin = null; UpdateBadge.Visibility = Visibility.Collapsed; };
    }

    /// <summary>启动时的自动检查：静默进行，失败（离线）不提示；发现新版本只在顶栏显示标记，不弹窗、不抢焦点。</summary>
    private async Task AutoCheckUpdateAsync()
    {
        try
        {
            await Task.Delay(3000);
            using var client = UpdateChecker.CreateClient(TimeSpan.FromSeconds(10));
            var r = await UpdateChecker.FetchLatestAsync(client);
            _settings.LastUpdateCheck = DateTime.Now;
            if (UpdateChecker.IsNewer(r.TagName, AppVersion) && !string.Equals(r.TagName, _settings.SkippedUpdateVersion, StringComparison.OrdinalIgnoreCase))
            {
                _pendingRelease = r;
                UpdateBadge.Visibility = Visibility.Visible;
                UpdateBadge.ToolTip = $"发现新版本 {r.TagName}，点「工具 → 检查更新」查看";
                SetStatus($"发现新版本 {r.TagName}：工具 → 检查更新");
            }
        }
        catch
        {
            // 离线 / GitHub 不可达：静默忽略，下次启动再试
        }
    }
}
