using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using SerialTool.Features;

namespace SerialTool;

/// <summary>批量测试表格下拉框的选项。</summary>
public static class TestUiOptions
{
    public static string[] ChecksumNames { get; } = Enum.GetNames(typeof(ChecksumKind));
    public static ExpectMode[] ExpectModes { get; } = (ExpectMode[])Enum.GetValues(typeof(ExpectMode));
}

/// <summary>批量测试表格的一行。</summary>
public sealed class TestStepRow : INotifyPropertyChanged
{
    private int _index;
    private string _state = "", _result = "", _duration = "", _message = "";

    public TestStepRow(TestStep step) { Step = step; }
    public TestStep Step { get; }

    public int Index { get => _index; set => Set(ref _index, value); }
    public bool Enabled { get => Step.Enabled; set { Step.Enabled = value; OnChanged(); } }
    public string Name { get => Step.Name; set { Step.Name = value ?? ""; OnChanged(); } }
    public string Send { get => Step.Send; set { Step.Send = value ?? ""; OnChanged(); } }
    public bool SendHex { get => Step.SendHex; set { Step.SendHex = value; OnChanged(); } }
    public string Checksum { get => Step.Checksum; set { Step.Checksum = value ?? "None"; OnChanged(); } }
    public ExpectMode Expect { get => Step.Expect; set { Step.Expect = value; OnChanged(); } }
    public string ExpectValue { get => Step.ExpectValue; set { Step.ExpectValue = value ?? ""; OnChanged(); } }
    public int TimeoutMs { get => Step.TimeoutMs; set { Step.TimeoutMs = Math.Clamp(value, 1, 600_000); OnChanged(); } }
    public int DelayMs { get => Step.DelayMs; set { Step.DelayMs = Math.Clamp(value, 0, 600_000); OnChanged(); } }
    public int Retries { get => Step.Retries; set { Step.Retries = Math.Clamp(value, 0, 100); OnChanged(); } }
    public bool ContinueOnFail { get => Step.ContinueOnFail; set { Step.ContinueOnFail = value; OnChanged(); } }

    /// <summary>"" / Running / Pass / Fail / Skipped</summary>
    public string State { get => _state; set => Set(ref _state, value); }
    public string ResultText { get => _result; set => Set(ref _result, value); }
    public string DurationText { get => _duration; set => Set(ref _duration, value); }
    public string Message { get => _message; set => Set(ref _message, value); }

    public void ClearResult()
    {
        State = ResultText = DurationText = Message = "";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    private void Set<T>(ref T field, T value, [CallerMemberName] string? n = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnChanged(n);
    }
}

public sealed class GwLogItem
{
    public string Text { get; init; } = "";
    public bool IsError { get; init; }
}

public partial class MainWindow
{
    // ---- 会话标签页 ----
    private SessionChannel _mainChannel = new("main", "会话 1");
    private readonly ObservableCollection<SessionChannel> _sessionChannels = new();
    private readonly List<SessionView> _sessionViews = new();
    private bool _sessionTabsLoading;

    // ---- 主会话 Lua ----
    private LuaScriptHost? _luaMain;
    private string _scriptLang = "DSL";
    private string _dslMainText = "", _luaMainText = "";

    // ---- 批量测试 ----
    private readonly ObservableCollection<TestStepRow> _testRows = new();
    private CancellationTokenSource? _testCts;
    private TestReport? _lastReport;

    // ---- Modbus 网关 ----
    private ModbusTcpToRtuGateway? _gwTcp;
    private ModbusRtuToTcpGateway? _gwRtu;
    private ChannelRtuTransport? _gwTransport;
    private SessionChannel? _gwChannel;
    private readonly ConcurrentQueue<GatewayLogEntry> _gwPending = new();
    private readonly ObservableCollection<GwLogItem> _gwLog = new();

    private static readonly List<Opt<string>> GwModeOptions = new()
    {
        new("TCP→RTU：Modbus TCP 服务器 → 串口 RTU 从站", "TcpToRtu"),
        new("RTU→TCP：串口 RTU 主站 → 远端 TCP 从站", "RtuToTcp"),
    };

    private string MainSessionName => _mainChannel.Name;

    /// <summary>构造函数里（InitControls）调用。</summary>
    private void InitControlsV15()
    {
        var s = _settings;
        AppSettings.MigrateSessions(s);
        var main = s.Sessions[0];
        _mainChannel = new SessionChannel(main.Id, string.IsNullOrWhiteSpace(main.Name) ? "会话 1" : main.Name);
        _sessionChannels.Add(_mainChannel);

        ScriptLangCombo.ItemsSource = new[] { "DSL", "Lua" };
        _scriptLang = s.ScriptLanguage == "Lua" ? "Lua" : "DSL";
        _dslMainText = s.ScriptText ?? "";
        _luaMainText = s.LuaScriptText ?? "";
        ScriptLangCombo.SelectedItem = _scriptLang;
        ApplyScriptLangUi(false);

        TestGrid.ItemsSource = _testRows;
        TestSessionCombo.ItemsSource = _sessionChannels;
        TestSessionCombo.SelectedIndex = 0;
        TestLoopsBox.Text = Math.Max(1, s.TestLoops).ToString();

        GwModeCombo.ItemsSource = GwModeOptions;
        GwModeCombo.SelectedItem = GwModeOptions.FirstOrDefault(o => o.Value == s.Gateway.Mode) ?? GwModeOptions[0];
        GwSessionCombo.ItemsSource = _sessionChannels;
        GwSessionCombo.SelectedIndex = 0;
        GwPortBox.Text = s.Gateway.TcpPort.ToString();
        GwHostBox.Text = s.Gateway.RemoteHost;
        GwRemotePortBox.Text = s.Gateway.RemotePort.ToString();
        GwTimeoutBox.Text = s.Gateway.TimeoutMs.ToString();
        GwUnitBox.Text = s.Gateway.UnitOverride.ToString();
        GwLogList.ItemsSource = _gwLog;
        ApplyGwModeUi();
    }

    /// <summary>Window_Loaded 末尾调用。</summary>
    private void InitV15()
    {
        var s = _settings;
        _sessionTabsLoading = true;
        try
        {
            MainSessionTab.Header = MakeSessionHeader(MainSessionTab, MainSessionName, false);
            foreach (var extra in s.Sessions.Skip(1).ToList()) AddSessionTab(extra.Clone(), false);
            int idx = Math.Clamp(s.ActiveSessionIndex, 0, _sessionViews.Count);
            SessionTabs.SelectedIndex = idx;
        }
        finally
        {
            _sessionTabsLoading = false;
        }
        // 网关会话按 Id 选择
        var gwSel = _sessionChannels.FirstOrDefault(c => c.Id == s.Gateway.SessionId);
        if (gwSel != null) GwSessionCombo.SelectedItem = gwSel;

        // 批量测试：上次的用例文件，否则示例
        TestSuite suite;
        try
        {
            suite = !string.IsNullOrEmpty(s.TestSuitePath) && File.Exists(s.TestSuitePath) ? TestSuite.Load(s.TestSuitePath) : TestSuite.Sample();
        }
        catch
        {
            suite = TestSuite.Sample();
        }
        LoadSuiteIntoGrid(suite);

        _statsTimer.Tick += (_, _) => UpdateGatewayUi();
        UpdateSessionRulesV15();
    }

    private void CollectSettingsV15()
    {
        var s = _settings;
        if (_scriptLang == "Lua") _luaMainText = ScriptBox.Text; else _dslMainText = ScriptBox.Text;
        s.ScriptText = _dslMainText;          // 兼容 v1.4：ScriptText 始终是 DSL 脚本
        s.LuaScriptText = _luaMainText;
        s.ScriptLanguage = _scriptLang;

        var main = SessionSettings.FromLegacy(s, MainSessionName, _mainChannel.Id);
        main.ScriptLanguage = _scriptLang;
        main.DslScriptText = _dslMainText;
        main.LuaScriptText = _luaMainText;
        var list = new List<SessionSettings> { main };
        foreach (var v in _sessionViews) list.Add(v.CollectSettings().Clone());
        s.Sessions = list;
        int sel = SessionTabs.SelectedIndex;
        s.ActiveSessionIndex = sel >= 0 && sel < list.Count ? sel : 0;

        if (int.TryParse(TestLoopsBox.Text.Trim(), out int loops) && loops > 0) s.TestLoops = loops;
        var g = s.Gateway;
        g.Mode = (GwModeCombo.SelectedItem as Opt<string>)?.Value ?? "TcpToRtu";
        if (int.TryParse(GwPortBox.Text.Trim(), out int gp) && gp is > 0 and <= 65535) g.TcpPort = gp;
        g.RemoteHost = GwHostBox.Text.Trim();
        if (int.TryParse(GwRemotePortBox.Text.Trim(), out int grp) && grp is > 0 and <= 65535) g.RemotePort = grp;
        if (int.TryParse(GwTimeoutBox.Text.Trim(), out int gt) && gt > 0) g.TimeoutMs = gt;
        if (byte.TryParse(GwUnitBox.Text.Trim(), out byte gu)) g.UnitOverride = gu;
        g.SessionId = (GwSessionCombo.SelectedItem as SessionChannel)?.Id ?? "";
    }

    private void ShutdownV15()
    {
        try { _testCts?.Cancel(); } catch { /* ignore */ }
        try { _luaMain?.Stop(); } catch { /* ignore */ }
        try { Task.Run(StopGatewayAsync).Wait(2000); } catch { /* ignore */ }
        foreach (var v in _sessionViews.ToList()) v.ShutdownSync();
    }

    /// <summary>规则变化时同步给其它会话的自动回复。</summary>
    private void UpdateSessionRulesV15()
    {
        var replyRules = _compiledRules
            .Where(c => c.Rule.IsUsable && c.IsValid && c.Rule.ReplyEnabled && !string.IsNullOrEmpty(c.Rule.ReplyContent))
            .ToList();
        foreach (var v in _sessionViews) v.SetAutoReplyRules(replyRules);
    }

    // ======================================================================
    // 会话标签页
    // ======================================================================

    private FrameworkElement MakeSessionHeader(TabItem tab, string name, bool closable)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Background = System.Windows.Media.Brushes.Transparent };
        var text = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(text);
        if (closable)
        {
            var close = new Button
            {
                Content = "×", Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(8, 0, -4, 0), MinWidth = 0, MinHeight = 0,
                Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0), Focusable = false,
                VerticalAlignment = VerticalAlignment.Center, ToolTip = "关闭此会话", Cursor = Cursors.Hand, FontSize = 13,
            };
            close.SetBinding(ForegroundProperty, new Binding("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(TabItem), 1) });
            close.Click += (_, e) =>
            {
                e.Handled = true;
                if (tab.Content is SessionView v) _ = CloseSessionTabAsync(v, tab);
            };
            panel.Children.Add(close);
        }
        panel.ToolTip = "双击重命名";
        panel.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                RenameSessionTab(tab);
                e.Handled = true;
            }
        };
        var menu = new ContextMenu();
        var rename = new MenuItem { Header = "重命名…" };
        rename.Click += (_, _) => RenameSessionTab(tab);
        menu.Items.Add(rename);
        if (closable)
        {
            var closeItem = new MenuItem { Header = "关闭会话" };
            closeItem.Click += (_, _) => { if (tab.Content is SessionView v) _ = CloseSessionTabAsync(v, tab); };
            menu.Items.Add(closeItem);
        }
        var add = new MenuItem { Header = "新建会话" };
        add.Click += (_, _) => AddSessionTab(null, true);
        menu.Items.Add(add);
        panel.ContextMenu = menu;
        return panel;
    }

    private string NextSessionName()
    {
        var used = _sessionChannels.Select(c => c.Name).ToHashSet();
        for (int n = 2; ; n++)
            if (!used.Contains($"会话 {n}")) return $"会话 {n}";
    }

    private SessionView AddSessionTab(SessionSettings? s, bool select)
    {
        if (s == null)
        {
            s = new SessionSettings
            {
                Name = NextSessionName(),
                BaudRate = _settings.BaudRate,
                Encoding = _settings.Encoding,
                ShowTx = true,
                TimerIntervalMs = 1000,
            };
        }
        s.Normalize();
        if (_sessionChannels.Any(c => c.Id == s.Id)) s.Id = Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(s.Name)) s.Name = NextSessionName();
        var view = new SessionView(s);
        view.StatusMessage += (m, err) => SetStatus(m, err);
        var tab = new TabItem { Content = view };
        tab.Header = MakeSessionHeader(tab, s.Name, true);
        _sessionTabsLoading = true;
        try
        {
            SessionTabs.Items.Insert(SessionTabs.Items.IndexOf(PlusTab), tab);
            if (select) SessionTabs.SelectedItem = tab;
        }
        finally
        {
            _sessionTabsLoading = false;
        }
        _sessionViews.Add(view);
        _sessionChannels.Add(view.Channel);
        if (!_loading) UpdateSessionRulesV15();
        return view;
    }

    private void SessionTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, SessionTabs) || _sessionTabsLoading || _loading) return;
        if (SessionTabs.SelectedItem == PlusTab)
        {
            var v = AddSessionTab(null, true);
            SetStatus($"已新建「{v.SessionName}」：在左侧选择连接后点击“打开”");
        }
    }

    private async Task CloseSessionTabAsync(SessionView view, TabItem tab)
    {
        if (view.Core.IsOpen &&
            MessageBox.Show(this, $"「{view.SessionName}」的连接仍然打开，确定关闭这个会话吗？", "关闭会话",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        if (_gwChannel == view.Channel) await StopGatewayAsync();
        if (_testCts != null && TestSessionCombo.SelectedItem == view.Channel) _testCts.Cancel();
        int idx = SessionTabs.Items.IndexOf(tab);
        _sessionTabsLoading = true;
        try
        {
            SessionTabs.SelectedIndex = Math.Max(0, idx - 1);
            SessionTabs.Items.Remove(tab);
        }
        finally
        {
            _sessionTabsLoading = false;
        }
        _sessionViews.Remove(view);
        _sessionChannels.Remove(view.Channel);
        if (TestSessionCombo.SelectedItem == null) TestSessionCombo.SelectedIndex = 0;
        if (GwSessionCombo.SelectedItem == null) GwSessionCombo.SelectedIndex = 0;
        await view.ShutdownAsync();
        SetStatus($"已关闭会话「{view.SessionName}」");
    }

    private void RenameSessionTab(TabItem tab)
    {
        string current = tab == MainSessionTab ? MainSessionName : (tab.Content as SessionView)?.SessionName ?? "";
        var name = InputDialog.Show(this, "重命名会话", "会话名称：", current);
        if (name == null) return;
        name = name.Trim();
        if (name.Length == 0 || name == current) return;
        if (_sessionChannels.Any(c => c.Name == name))
        {
            SetStatus($"已有名为「{name}」的会话", true);
            return;
        }
        if (tab == MainSessionTab)
        {
            _mainChannel.Name = name;
            tab.Header = MakeSessionHeader(tab, name, false);
        }
        else if (tab.Content is SessionView v)
        {
            v.SessionName = name;
            tab.Header = MakeSessionHeader(tab, name, true);
        }
        // 刷新下拉框显示
        TestSessionCombo.Items.Refresh();
        GwSessionCombo.Items.Refresh();
    }

    // ======================================================================
    // 主会话脚本：DSL / Lua
    // ======================================================================

    private void ScriptLangCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        string next = ScriptLangCombo.SelectedItem as string ?? "DSL";
        if (next == _scriptLang) return;
        if (_scriptLang == "Lua") _luaMainText = ScriptBox.Text; else _dslMainText = ScriptBox.Text;
        _scriptLang = next;
        ApplyScriptLangUi(true);
    }

    private void ApplyScriptLangUi(bool swapText)
    {
        bool lua = _scriptLang == "Lua";
        if (swapText || _loading) ScriptBox.Text = lua ? _luaMainText : _dslMainText;
        ScriptHelpText.Text = lua
            ? "Lua：send / sendhex / read / waitfor / waitregex / sleep / log / crc16 / checksum / on_receive / status / session"
            : "SEND / SENDHEX / SENDHEXRAW / CHECKSUM / SLEEP / WAITFOR / LOG";
        ScriptSampleEchoBtn.Visibility = lua ? Visibility.Visible : Visibility.Collapsed;
        ScriptStatusText.Text = "";
    }

    /// <summary>ScriptRun_Click 开头调用：Lua 模式时运行 Lua 并返回 true。</summary>
    private bool TryRunLuaMain()
    {
        if (_scriptLang != "Lua") return false;
        if (_luaMain?.IsRunning == true)
        {
            SetStatus("脚本已在运行", true);
            return true;
        }
        ScriptOutBox.Clear();
        ScriptStatusText.Text = "";
        var lua = new LuaScriptHost(_mainChannel, CurrentEncoding());
        _luaMain = lua;
        lua.Output += m => Dispatcher.BeginInvoke(() =>
        {
            ScriptOutBox.AppendText(m + "\r\n");
            if (ScriptOutBox.Text.Length > 200_000) ScriptOutBox.Text = ScriptOutBox.Text.Substring(ScriptOutBox.Text.Length - 150_000);
            ScriptOutBox.ScrollToEnd();
        });
        lua.StatusChanged += st => Dispatcher.BeginInvoke(() => ScriptStatusText.Text = string.IsNullOrEmpty(st) ? "" : "status = " + st);
        lua.Completed += r => Dispatcher.BeginInvoke(() =>
        {
            ScriptOutBox.AppendText(r + "\r\n");
            ScriptOutBox.ScrollToEnd();
            ScriptRunBtn.IsEnabled = true;
            ScriptStopBtn.IsEnabled = false;
            ScriptLangCombo.IsEnabled = true;
            SetStatus("[Lua] " + r, r.Status == LuaRunStatus.Error);
        });
        ScriptRunBtn.IsEnabled = false;
        ScriptStopBtn.IsEnabled = true;
        ScriptLangCombo.IsEnabled = false;
        if (_conn == null) ScriptOutBox.AppendText("提示：主连接未打开，send 会返回 false。\r\n");
        _ = lua.RunAsync(ScriptBox.Text);
        return true;
    }

    private void ScriptSampleEcho_Click(object sender, RoutedEventArgs e) => ScriptBox.Text = LuaSamples.Echo;

    // ======================================================================
    // 批量测试
    // ======================================================================

    private void LoadSuiteIntoGrid(TestSuite suite)
    {
        _testRows.Clear();
        foreach (var st in suite.Steps) _testRows.Add(new TestStepRow(st));
        Reindex();
        TestNameBox.Text = suite.Name;
        TestNameBox.Tag = suite;
    }

    private void Reindex()
    {
        for (int i = 0; i < _testRows.Count; i++) _testRows[i].Index = i + 1;
    }

    private TestSuite SuiteFromGrid()
    {
        TestGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        TestGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var old = TestNameBox.Tag as TestSuite;
        return new TestSuite
        {
            Name = string.IsNullOrWhiteSpace(TestNameBox.Text) ? "未命名测试" : TestNameBox.Text.Trim(),
            Description = old?.Description ?? "",
            Encoding = CurrentEncoding().WebName,
            Steps = _testRows.Select(r => r.Step.Clone()).ToList(),
        };
    }

    private void TestNew_Click(object sender, RoutedEventArgs e)
    {
        LoadSuiteIntoGrid(new TestSuite { Name = "新测试", Steps = { new TestStep { Name = "步骤 1", Send = "AT\\r\\n", ExpectValue = "OK" } } });
        _settings.TestSuitePath = "";
    }

    private void TestSample_Click(object sender, RoutedEventArgs e) => LoadSuiteIntoGrid(TestSuite.Sample());

    private void TestOpen_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "测试用例 (*.json)|*.json|所有文件|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            LoadSuiteIntoGrid(TestSuite.Load(dlg.FileName));
            _settings.TestSuitePath = dlg.FileName;
            SetStatus("已打开测试用例：" + dlg.FileName);
        }
        catch (Exception ex)
        {
            SetStatus("打开测试用例失败：" + ex.Message, true);
        }
    }

    private void TestSave_Click(object sender, RoutedEventArgs e)
    {
        var suite = SuiteFromGrid();
        var dlg = new SaveFileDialog
        {
            Filter = "测试用例 (*.json)|*.json",
            FileName = string.IsNullOrEmpty(_settings.TestSuitePath) ? MakeSafeFileName(suite.Name) + ".json" : Path.GetFileName(_settings.TestSuitePath),
        };
        if (!string.IsNullOrEmpty(_settings.TestSuitePath)) dlg.InitialDirectory = Path.GetDirectoryName(_settings.TestSuitePath);
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            suite.Save(dlg.FileName);
            _settings.TestSuitePath = dlg.FileName;
            SetStatus("测试用例已保存：" + dlg.FileName);
        }
        catch (Exception ex)
        {
            SetStatus("保存失败：" + ex.Message, true);
        }
    }

    private static string MakeSafeFileName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Length == 0 ? "test" : s;
    }

    private void TestAddStep_Click(object sender, RoutedEventArgs e)
    {
        var row = new TestStepRow(new TestStep { Name = $"步骤 {_testRows.Count + 1}", Send = "AT\\r\\n", ExpectValue = "OK" });
        int at = TestGrid.SelectedIndex >= 0 ? TestGrid.SelectedIndex + 1 : _testRows.Count;
        _testRows.Insert(at, row);
        Reindex();
        TestGrid.SelectedItem = row;
        TestGrid.ScrollIntoView(row);
    }

    private void TestRemoveStep_Click(object sender, RoutedEventArgs e)
    {
        if (TestGrid.SelectedItem is not TestStepRow r) return;
        int i = _testRows.IndexOf(r);
        _testRows.Remove(r);
        Reindex();
        if (_testRows.Count > 0) TestGrid.SelectedIndex = Math.Min(i, _testRows.Count - 1);
    }

    private void TestMoveUp_Click(object sender, RoutedEventArgs e) => MoveStep(-1);
    private void TestMoveDown_Click(object sender, RoutedEventArgs e) => MoveStep(1);

    private void MoveStep(int d)
    {
        if (TestGrid.SelectedItem is not TestStepRow r) return;
        int i = _testRows.IndexOf(r), j = i + d;
        if (j < 0 || j >= _testRows.Count) return;
        _testRows.Move(i, j);
        Reindex();
        TestGrid.SelectedItem = r;
    }

    private async void TestRun_Click(object sender, RoutedEventArgs e)
    {
        if (_testCts != null) return;
        if (TestSessionCombo.SelectedItem is not SessionChannel ch)
        {
            SetStatus("请选择要测试的会话", true);
            return;
        }
        if (!ch.IsOpen)
        {
            SetStatus($"会话「{ch.Name}」未连接，请先打开", true);
            return;
        }
        if (!int.TryParse(TestLoopsBox.Text.Trim(), out int loops) || loops < 1 || loops > 100000)
        {
            SetStatus("循环次数应为 1 ~ 100000", true);
            return;
        }
        var suite = SuiteFromGrid();
        if (suite.Steps.Count == 0)
        {
            SetStatus("测试用例没有步骤", true);
            return;
        }
        foreach (var r in _testRows) r.ClearResult();
        TestLogBox.Clear();
        _testCts = new CancellationTokenSource();
        TestRunBtn.IsEnabled = false;
        TestStopBtn.IsEnabled = true;
        TestHtmlBtn.IsEnabled = TestCsvBtn.IsEnabled = false;
        TestGrid.IsReadOnly = true;
        TestSummaryText.Text = $"正在运行「{suite.Name}」：会话 {ch.Name}，共 {loops} 轮 …";
        int passed = 0, failed = 0;
        var runner = new TestRunner(ch);
        runner.StepStarted += (loop, i) => Dispatcher.BeginInvoke(() =>
        {
            if (i < _testRows.Count)
            {
                var row = _testRows[i];
                row.State = "Running";
                row.ResultText = loops > 1 ? $"第 {loop} 轮…" : "运行中…";
                TestGrid.ScrollIntoView(row);
            }
        });
        runner.StepFinished += res => Dispatcher.BeginInvoke(() =>
        {
            if (res.Verdict == TestVerdict.Pass) passed++;
            else if (res.Verdict != TestVerdict.Skipped) failed++;
            if (res.StepIndex < _testRows.Count)
            {
                var row = _testRows[res.StepIndex];
                row.State = res.Verdict switch { TestVerdict.Pass => "Pass", TestVerdict.Skipped => "Skipped", _ => "Fail" };
                row.ResultText = res.VerdictText;
                row.DurationText = res.DurationMs.ToString("0");
                string got = res.ReceivedText.Length > 0 ? res.ReceivedText : res.ReceivedHex;
                if (got.Length > 120) got = got.Substring(0, 120) + "…";
                row.Message = res.Message + (got.Length > 0 && res.Verdict != TestVerdict.Skipped ? "  | 收到：" + got : "");
            }
            if (res.Verdict != TestVerdict.Skipped)
            {
                TestLogBox.AppendText($"[{res.Start:HH:mm:ss.fff}] 第 {res.Loop} 轮 #{res.StepIndex + 1} {res.StepName} → {res.VerdictText}（{res.DurationMs:0} ms，尝试 {res.Attempts} 次）{res.Message}\r\n");
                if (TestLogBox.Text.Length > 300_000) TestLogBox.Text = TestLogBox.Text.Substring(TestLogBox.Text.Length - 200_000);
                TestLogBox.ScrollToEnd();
            }
            int total = passed + failed;
            TestSummaryText.Text = $"运行中：通过 {passed}，失败 {failed}，通过率 {(total == 0 ? 0 : passed * 100.0 / total):0.0}%";
        });
        try
        {
            var report = await runner.RunAsync(suite, loops, _testCts.Token);
            _lastReport = report;
            TestSummaryText.Text = $"「{suite.Name}」完成：{report.Summary}，用时 {(report.End - report.Start).TotalSeconds:0.0} 秒";
            SetStatus("批量测试完成：" + report.Summary, report.Failed > 0);
            TestHtmlBtn.IsEnabled = TestCsvBtn.IsEnabled = true;
        }
        catch (Exception ex)
        {
            TestSummaryText.Text = "测试异常：" + ex.Message;
            App.LogCrash(ex);
        }
        finally
        {
            _testCts?.Dispose();
            _testCts = null;
            TestRunBtn.IsEnabled = true;
            TestStopBtn.IsEnabled = false;
            TestGrid.IsReadOnly = false;
            foreach (var r in _testRows) if (r.State == "Running") { r.State = ""; r.ResultText = "已停止"; }
        }
    }

    private void TestStop_Click(object sender, RoutedEventArgs e) => _testCts?.Cancel();

    private void TestExportHtml_Click(object sender, RoutedEventArgs e) => ExportReport("html");
    private void TestExportCsv_Click(object sender, RoutedEventArgs e) => ExportReport("csv");

    private void ExportReport(string kind)
    {
        var rep = _lastReport;
        if (rep == null) return;
        var dlg = new SaveFileDialog
        {
            Filter = kind == "html" ? "HTML 报告|*.html" : "CSV 表格|*.csv",
            FileName = $"{MakeSafeFileName(rep.SuiteName)}_{rep.Start:yyyyMMdd_HHmmss}.{kind}",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, kind == "html" ? rep.ToHtml() : rep.ToCsv(), new UTF8Encoding(kind == "csv"));
            SetStatus("报告已导出：" + dlg.FileName);
        }
        catch (Exception ex)
        {
            SetStatus("导出失败：" + ex.Message, true);
        }
    }

    // ======================================================================
    // Modbus 网关
    // ======================================================================

    private void GwModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyGwModeUi();

    private void ApplyGwModeUi()
    {
        bool tcp2rtu = ((GwModeCombo.SelectedItem as Opt<string>)?.Value ?? "TcpToRtu") == "TcpToRtu";
        GwServerPanel.Visibility = tcp2rtu ? Visibility.Visible : Visibility.Collapsed;
        GwClientPanel.Visibility = tcp2rtu ? Visibility.Collapsed : Visibility.Visible;
    }

    private bool GatewayRunning => _gwTcp != null || _gwRtu != null;

    private async void GwStart_Click(object sender, RoutedEventArgs e)
    {
        if (GatewayRunning)
        {
            await StopGatewayAsync();
            SetStatus("Modbus 网关已停止");
            return;
        }
        if (GwSessionCombo.SelectedItem is not SessionChannel ch)
        {
            SetStatus("请选择 RTU 会话", true);
            return;
        }
        if (!int.TryParse(GwTimeoutBox.Text.Trim(), out int timeout) || timeout < 10 || timeout > 60000)
        {
            SetStatus("网关超时应为 10 ~ 60000 ms", true);
            return;
        }
        if (!byte.TryParse(GwUnitBox.Text.Trim(), out byte unit)) unit = 0;
        string mode = (GwModeCombo.SelectedItem as Opt<string>)?.Value ?? "TcpToRtu";
        if (!ch.IsOpen) AddGwLog($"提示：会话「{ch.Name}」尚未连接，请求会返回异常 0A，直到会话打开。", true);
        try
        {
            if (mode == "TcpToRtu")
            {
                if (!int.TryParse(GwPortBox.Text.Trim(), out int port) || port < 1 || port > 65535)
                {
                    SetStatus("TCP 监听端口无效", true);
                    return;
                }
                _gwTransport = new ChannelRtuTransport(ch);
                var gw = new ModbusTcpToRtuGateway(_gwTransport) { TimeoutMs = timeout, UnitOverride = unit };
                gw.Log += le => _gwPending.Enqueue(le);
                gw.ClientsChanged += n => _gwPending.Enqueue(new GatewayLogEntry { Time = DateTime.Now, Direction = "TCP", Result = $"客户端数：{n}" });
                gw.Start(port);
                _gwTcp = gw;
                AddGwLog($"网关已启动：Modbus TCP 端口 {gw.Port} → 会话「{ch.Name}」（RTU），超时 {timeout} ms", false);
            }
            else
            {
                if (!int.TryParse(GwRemotePortBox.Text.Trim(), out int rport) || rport < 1 || rport > 65535 || string.IsNullOrWhiteSpace(GwHostBox.Text))
                {
                    SetStatus("远端从站地址 / 端口无效", true);
                    return;
                }
                var gw = new ModbusRtuToTcpGateway(ch, GwHostBox.Text.Trim(), rport) { TimeoutMs = timeout, UnitOverride = unit };
                gw.Log += le => _gwPending.Enqueue(le);
                gw.Start();
                _gwRtu = gw;
                AddGwLog($"网关已启动：会话「{ch.Name}」上的 RTU 请求 → {gw.RemoteHost}:{gw.RemotePort}（Modbus TCP），超时 {timeout} ms", false);
            }
            _gwChannel = ch;
            GwStartBtn.Content = "停止网关";
            GwStartBtn.Style = (Style)FindResource("DangerButton");
            GwModeCombo.IsEnabled = GwSessionCombo.IsEnabled = false;
            SetStatus("Modbus 网关已启动");
        }
        catch (Exception ex)
        {
            _gwTransport?.Dispose();
            _gwTransport = null;
            SetStatus("网关启动失败：" + NetUtil.Describe(ex), true);
            AddGwLog("启动失败：" + NetUtil.Describe(ex), true);
        }
    }

    private async Task StopGatewayAsync()
    {
        var t = _gwTcp;
        var r = _gwRtu;
        _gwTcp = null;
        _gwRtu = null;
        if (t != null) await t.StopAsync();
        if (r != null) await r.StopAsync();
        _gwTransport?.Dispose();
        _gwTransport = null;
        _gwChannel = null;
        if (Dispatcher.CheckAccess())
        {
            GwStartBtn.Content = "启动网关";
            GwStartBtn.Style = (Style)FindResource("AccentButton");
            GwModeCombo.IsEnabled = GwSessionCombo.IsEnabled = true;
        }
    }

    private void GwClear_Click(object sender, RoutedEventArgs e)
    {
        _gwLog.Clear();
        _gwTcp?.Stats.Reset();
        _gwRtu?.Stats.Reset();
    }

    private void AddGwLog(string text, bool error)
    {
        _gwLog.Add(new GwLogItem { Text = $"{DateTime.Now:HH:mm:ss.fff} {text}", IsError = error });
        TrimGwLog();
    }

    private void TrimGwLog()
    {
        while (_gwLog.Count > 3000) _gwLog.RemoveAt(0);
        if (_gwLog.Count > 0) GwLogList.ScrollIntoView(_gwLog[^1]);
    }

    private void UpdateGatewayUi()
    {
        bool any = false;
        while (_gwPending.TryDequeue(out var le))
        {
            _gwLog.Add(new GwLogItem { Text = le.ToString(), IsError = le.IsError });
            any = true;
        }
        if (any) TrimGwLog();
        if (_gwTcp != null)
            GwStatsText.Text = $"运行中（TCP→RTU，端口 {_gwTcp.Port}，客户端 {_gwTcp.ClientCount}）  {_gwTcp.Stats}";
        else if (_gwRtu != null)
            GwStatsText.Text = $"运行中（RTU→TCP，远端 {_gwRtu.RemoteHost}:{_gwRtu.RemotePort}，{(_gwRtu.RemoteConnected ? "已连接" : "未连接")}）  {_gwRtu.Stats}";
    }
}

/// <summary>简单的单行输入对话框。</summary>
public static class InputDialog
{
    public static string? Show(Window owner, string title, string prompt, string initial)
    {
        var win = new Window
        {
            Title = title, Owner = owner, Width = 360, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Background = (System.Windows.Media.Brush)owner.FindResource("WindowBgBrush"),
            Foreground = (System.Windows.Media.Brush)owner.FindResource("TextBrush"),
            FontFamily = owner.FontFamily, FontSize = 13,
        };
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 6) });
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(box);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "确定", IsDefault = true, MinWidth = 72, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 72 };
        if (owner.TryFindResource("AccentButton") is Style st) ok.Style = st;
        ok.Click += (_, _) => win.DialogResult = true;
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        win.Content = panel;
        win.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return win.ShowDialog() == true ? box.Text : null;
    }
}
