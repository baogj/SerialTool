using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SerialTool.Features;
using TriggerAction = SerialTool.Features.TriggerAction;

namespace SerialTool;

public partial class TriggerRulesWindow : Window
{
    public sealed class RuleItem
    {
        public TriggerRule Rule { get; }
        public RuleItem(TriggerRule r) => Rule = r;
        public string Title => (Rule.Enabled ? "✓ " : "✗ ") + (string.IsNullOrWhiteSpace(Rule.Name) ? "(未命名)" : Rule.Name);
        public string Sub => $"{(Rule.IsHex ? "HEX" : Rule.IsRegex ? "正则" : "文本")}：{Rule.Pattern}  · 命中 {Rule.HitCount} · {Rule.ActionsSummary}";
    }

    private readonly ObservableCollection<RuleItem> _items;
    private readonly Encoding _encoding;
    private bool _loading;
    private static readonly string[] Kinds = { "文本", "正则", "HEX" };
    private static readonly TriggerActionKind[] ActKinds = (TriggerActionKind[])Enum.GetValues(typeof(TriggerActionKind));

    public List<TriggerRule> Result { get; private set; } = new();
    public bool GlobalEnabled => GlobalChk.IsChecked == true;

    public TriggerRulesWindow(IEnumerable<TriggerRule> rules, bool globalEnabled, Encoding encoding)
    {
        InitializeComponent();
        _encoding = encoding;
        _items = new ObservableCollection<RuleItem>(rules.Select(r => new RuleItem(r.Clone())));
        KindCombo.ItemsSource = Kinds;
        ActKindCombo.ItemsSource = ActKinds.Select(TriggerAction.Label).ToList();
        ActKindCombo.SelectedIndex = 0;
        RuleList.ItemsSource = _items;
        GlobalChk.IsChecked = globalEnabled;
        if (_items.Count > 0) RuleList.SelectedIndex = 0;
        else LoadRule(null);
    }

    private TriggerRule? Cur => (RuleList.SelectedItem as RuleItem)?.Rule;

    private void RefreshList()
    {
        int idx = RuleList.SelectedIndex;
        _loading = true;
        RuleList.Items.Refresh();
        RuleList.SelectedIndex = idx;
        _loading = false;
    }

    private void RuleList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        LoadRule(Cur);
    }

    private void LoadRule(TriggerRule? r)
    {
        _loading = true;
        try
        {
            EditPanel.IsEnabled = r != null;
            NameBox.Text = r?.Name ?? "";
            PatternBox.Text = r?.Pattern ?? "";
            KindCombo.SelectedIndex = r == null ? 0 : r.IsHex ? 2 : r.IsRegex ? 1 : 0;
            IgnoreCaseChk.IsChecked = r?.IgnoreCase == true;
            EnabledChk.IsChecked = r?.Enabled != false;
            CooldownBox.Text = (r?.CooldownMs ?? 2000).ToString();
            HitText.Text = r == null ? "" : $"{r.HitCount} 次" + (r.LastHit is DateTime t ? $"，最近 {t:yyyy-MM-dd HH:mm:ss}" : "");
            ActionList.ItemsSource = r?.Actions.ToList();
            Validate();
        }
        finally { _loading = false; }
    }

    private void SaveRule()
    {
        if (_loading || Cur is not TriggerRule r) return;
        r.Name = NameBox.Text.Trim();
        r.Pattern = PatternBox.Text;
        r.IsRegex = KindCombo.SelectedIndex == 1;
        r.IsHex = KindCombo.SelectedIndex == 2;
        r.IgnoreCase = IgnoreCaseChk.IsChecked == true;
        r.Enabled = EnabledChk.IsChecked == true;
        if (int.TryParse(CooldownBox.Text.Trim(), out int cd) && cd >= 0) r.CooldownMs = cd;
        Validate();
        RefreshList();
    }

    private void Validate()
    {
        var r = Cur;
        if (r == null) { PatternError.Text = ""; return; }
        var cr = new CompiledRule(r.ToMatcherRule(), 0, _encoding);
        PatternError.Text = string.IsNullOrEmpty(r.Pattern) ? "匹配内容为空，规则不会触发" :
            cr.Error != null ? "匹配内容无效：" + cr.Error :
            r.Actions.Count == 0 ? "尚未添加动作，规则不会触发" : "";
    }

    private void Field_Changed(object sender, TextChangedEventArgs e) => SaveRule();
    private void Field_SelChanged(object sender, SelectionChangedEventArgs e) => SaveRule();
    private void Field_Click(object sender, RoutedEventArgs e) => SaveRule();

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var r = new TriggerRule { Name = "规则 " + (_items.Count + 1), Pattern = "ERROR", Actions = { new TriggerAction { Kind = TriggerActionKind.Toast } } };
        _items.Add(new RuleItem(r));
        RuleList.SelectedIndex = _items.Count - 1;
    }

    private void Dup_Click(object sender, RoutedEventArgs e)
    {
        if (Cur is not TriggerRule r) return;
        var c = r.Clone();
        c.Name += " 副本";
        c.HitCount = 0;
        c.LastHit = null;
        _items.Insert(RuleList.SelectedIndex + 1, new RuleItem(c));
        RuleList.SelectedIndex++;
    }

    private void Del_Click(object sender, RoutedEventArgs e)
    {
        int i = RuleList.SelectedIndex;
        if (i < 0) return;
        _items.RemoveAt(i);
        if (_items.Count > 0) RuleList.SelectedIndex = Math.Min(i, _items.Count - 1);
        else LoadRule(null);
    }

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        int i = RuleList.SelectedIndex;
        if (i > 0) { _items.Move(i, i - 1); RuleList.SelectedIndex = i - 1; }
    }

    private void Down_Click(object sender, RoutedEventArgs e)
    {
        int i = RuleList.SelectedIndex;
        if (i >= 0 && i < _items.Count - 1) { _items.Move(i, i + 1); RuleList.SelectedIndex = i + 1; }
    }

    private void ResetHits_Click(object sender, RoutedEventArgs e)
    {
        foreach (var it in _items) { it.Rule.HitCount = 0; it.Rule.LastHit = null; }
        RefreshList();
        LoadRule(Cur);
    }

    // ---------------- 动作 ----------------

    private TriggerActionKind CurActKind => ActKinds[Math.Max(0, ActKindCombo.SelectedIndex)];

    private void ActKind_Changed(object sender, SelectionChangedEventArgs e)
    {
        var k = CurActKind;
        ParamHint.Text = k switch
        {
            TriggerActionKind.Toast => "通知文本，留空为“触发规则：{rule}（第 {count} 次）”。右下角弹出，不抢焦点，4 秒后消失。",
            TriggerActionKind.TrayBalloon => "托盘气泡文本，留空同上。由 Windows 通知区域显示（受系统“专注助手/勿扰”设置影响）。",
            TriggerActionKind.Sound => "Asterisk / Beep / Exclamation / Hand / Question，或 .wav 文件路径。留空为 Exclamation。",
            TriggerActionKind.StartLog => "日志文件路径（追加写入）；留空则写到设置目录下 logs\\trigger_时间.log。已在记录时忽略。",
            TriggerActionKind.StopLog => "无参数：停止当前的日志记录。",
            TriggerActionKind.Snapshot => "保存目录；留空则保存到设置目录下 snapshots\\。文件名含规则名与时间。",
            TriggerActionKind.PauseDisplay => "无参数：勾选「暂停显示」（数据仍在接收并缓存）。",
            TriggerActionKind.StopSend => "无参数：停止定时发送与多项发送。",
            TriggerActionKind.ClosePort => "无参数：关闭当前串口 / 网络连接（同时会停止自动重连）。",
            _ => ".lua 文件路径 → 以 Lua 运行该文件；其他文件 → 按 DSL 脚本运行；留空 → 运行「脚本」页当前脚本。已有脚本在运行时跳过。",
        };
        bool needsPath = k is TriggerActionKind.Sound or TriggerActionKind.StartLog or TriggerActionKind.Snapshot or TriggerActionKind.RunScript;
        BrowseBtn.IsEnabled = needsPath;
        ParamBox.IsEnabled = k is not (TriggerActionKind.StopLog or TriggerActionKind.PauseDisplay or TriggerActionKind.StopSend or TriggerActionKind.ClosePort);
    }

    private void ActionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ActionList.SelectedItem is TriggerAction a)
        {
            ActKindCombo.SelectedIndex = Array.IndexOf(ActKinds, a.Kind);
            ParamBox.Text = a.Param;
        }
    }

    private void RefreshActions(int select)
    {
        if (Cur is not TriggerRule r) return;
        ActionList.ItemsSource = r.Actions.ToList();
        ActionList.SelectedIndex = Math.Min(select, r.Actions.Count - 1);
        Validate();
        RefreshList();
    }

    private void AddAction_Click(object sender, RoutedEventArgs e)
    {
        if (Cur is not TriggerRule r) return;
        r.Actions.Add(new TriggerAction { Kind = CurActKind, Param = ParamBox.IsEnabled ? ParamBox.Text.Trim() : "" });
        RefreshActions(r.Actions.Count - 1);
    }

    private void UpdateAction_Click(object sender, RoutedEventArgs e)
    {
        if (Cur is not TriggerRule r || ActionList.SelectedIndex < 0) return;
        int i = ActionList.SelectedIndex;
        r.Actions[i] = new TriggerAction { Kind = CurActKind, Param = ParamBox.IsEnabled ? ParamBox.Text.Trim() : "" };
        RefreshActions(i);
    }

    private void DelAction_Click(object sender, RoutedEventArgs e)
    {
        if (Cur is not TriggerRule r || ActionList.SelectedIndex < 0) return;
        int i = ActionList.SelectedIndex;
        r.Actions.RemoveAt(i);
        RefreshActions(i);
    }

    private void ActUp_Click(object sender, RoutedEventArgs e)
    {
        if (Cur is not TriggerRule r) return;
        int i = ActionList.SelectedIndex;
        if (i <= 0) return;
        (r.Actions[i - 1], r.Actions[i]) = (r.Actions[i], r.Actions[i - 1]);
        RefreshActions(i - 1);
    }

    private void ActDown_Click(object sender, RoutedEventArgs e)
    {
        if (Cur is not TriggerRule r) return;
        int i = ActionList.SelectedIndex;
        if (i < 0 || i >= r.Actions.Count - 1) return;
        (r.Actions[i + 1], r.Actions[i]) = (r.Actions[i], r.Actions[i + 1]);
        RefreshActions(i + 1);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var k = CurActKind;
        if (k == TriggerActionKind.Snapshot)
        {
            var sd = new SaveFileDialog { Title = "选择快照保存目录（文件名无所谓，只取目录）", FileName = "snapshot.txt", Filter = "文本文件 (*.txt)|*.txt" };
            if (sd.ShowDialog(this) == true) ParamBox.Text = System.IO.Path.GetDirectoryName(sd.FileName) ?? "";
            return;
        }
        if (k == TriggerActionKind.StartLog)
        {
            var sd = new SaveFileDialog { Title = "日志文件（追加写入）", FileName = "trigger.log", OverwritePrompt = false, Filter = "日志文件 (*.log)|*.log|所有文件 (*.*)|*.*" };
            if (sd.ShowDialog(this) == true) ParamBox.Text = sd.FileName;
            return;
        }
        var od = new OpenFileDialog
        {
            Title = k == TriggerActionKind.Sound ? "选择 .wav 声音文件" : "选择脚本文件",
            Filter = k == TriggerActionKind.Sound ? "WAV 声音 (*.wav)|*.wav" : "Lua 脚本 (*.lua)|*.lua|DSL 脚本 (*.txt)|*.txt|所有文件 (*.*)|*.*",
        };
        if (od.ShowDialog(this) == true) ParamBox.Text = od.FileName;
    }

    // ---------------- 测试 ----------------

    private void Test_Click(object sender, RoutedEventArgs e)
    {
        byte[] data;
        if (TestHexChk.IsChecked == true)
        {
            if (!DataConvert.TryParseHex(TestBox.Text, out data, out var err)) { TestResult.Text = err; return; }
        }
        else data = EscapeUtil.Encode(TestBox.Text, _encoding);
        var clones = _items.Select(i => i.Rule.Clone()).ToList();
        var eng = new TriggerEngine();
        eng.SetRules(clones, _encoding);
        var hits = eng.Push(data, DateTime.Now);
        var names = hits.Select(h => string.IsNullOrWhiteSpace(h.Rule.Name) ? h.Rule.Pattern : h.Rule.Name).ToList();
        TestResult.Text = names.Count == 0 ? "没有规则匹配（注意：未启用或没有动作的规则不参与匹配）" : "会触发：" + string.Join("、", names);
        if (eng.Errors.Count > 0) TestResult.Text += $"\n{eng.Errors.Count} 条规则匹配内容无效。";
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        SaveRule();
        Result = _items.Select(i => i.Rule).ToList();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
