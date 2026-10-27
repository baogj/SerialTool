using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Win32;
using SerialTool.Features;

namespace SerialTool;

public partial class DataDiffWindow : Window
{
    /// <summary>每侧最多渲染的字节数 / 行数（统计仍覆盖全部数据）。</summary>
    private const int MaxRenderBytes = 64 * 1024;
    private const int MaxRenderLines = 5000;

    private readonly Func<byte[]>? _rxProvider;
    private DiffResult? _result;
    private int _current = -1;
    private bool _hexMode = true;
    private readonly Dictionary<int, List<Run>> _runsA = new(), _runsB = new();

    private static readonly Brush DelBrush = Frozen(Color.FromRgb(0xFE, 0xCA, 0xCA));
    private static readonly Brush InsBrush = Frozen(Color.FromRgb(0xBB, 0xF7, 0xD0));
    private static readonly Brush CurDelBrush = Frozen(Color.FromRgb(0xF8, 0x71, 0x71));
    private static readonly Brush CurInsBrush = Frozen(Color.FromRgb(0x4A, 0xDE, 0x80));
    private static readonly Brush MarkFg = Frozen(Color.FromRgb(0x11, 0x18, 0x27));
    private static readonly Brush MutedFg = Frozen(Color.FromRgb(0x94, 0xA3, 0xB8));

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    public DataDiffWindow(Func<byte[]>? rxProvider = null, string? encoding = null)
    {
        InitializeComponent();
        _rxProvider = rxProvider;
        ModeCombo.ItemsSource = new[] { "HEX 字节", "文本行" };
        ModeCombo.SelectedIndex = 0;
        AlignCombo.ItemsSource = new[] { "智能对齐（最少差异）", "按位置逐个比较" };
        AlignCombo.SelectedIndex = 0;
        EncCombo.ItemsSource = EncodingUtil.Names;
        EncCombo.SelectedItem = EncodingUtil.Names.Contains(encoding) ? encoding : "UTF-8";
        UpdateOptUi();
    }

    private Encoding Enc => EncodingUtil.Get(EncCombo.SelectedItem as string ?? "UTF-8");
    private bool HexMode => ModeCombo.SelectedIndex == 0;
    private TextBox Box(object sender) => (string)((FrameworkElement)sender).Tag == "A" ? InA : InB;

    private void Opt_Changed(object sender, SelectionChangedEventArgs e) { if (IsLoaded) UpdateOptUi(); }

    private void UpdateOptUi()
    {
        IgnoreCaseChk.IsEnabled = !HexMode;
        IgnoreWsChk.IsEnabled = !HexMode;
    }

    /// <summary>把字节放入输入框：HEX 模式显示为 HEX，文本模式按编码解码。</summary>
    private void SetData(TextBox box, byte[] data) =>
        box.Text = HexMode ? FormatHexLines(data) : Enc.GetString(data);

    private static string FormatHexLines(byte[] data)
    {
        var sb = new StringBuilder(data.Length * 3 + data.Length / 16 * 2);
        for (int i = 0; i < data.Length; i++)
        {
            sb.Append(data[i].ToString("X2"));
            if (i == data.Length - 1) break;
            sb.Append((i + 1) % 16 == 0 ? "\r\n" : " ");
        }
        return sb.ToString();
    }

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        try { if (Clipboard.ContainsText()) Box(sender).Text = Clipboard.GetText(); } catch { }
    }

    private void ClearIn_Click(object sender, RoutedEventArgs e) => Box(sender).Clear();

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "选择要对比的文件", Filter = "所有文件 (*.*)|*.*|二进制 (*.bin)|*.bin|文本 (*.txt;*.log)|*.txt;*.log" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var info = new FileInfo(dlg.FileName);
            if (info.Length > 16 * 1024 * 1024) { MessageBox.Show(this, "文件超过 16 MB，请先截取需要对比的部分。", "数据对比"); return; }
            SetData(Box(sender), File.ReadAllBytes(dlg.FileName));
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "打开失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void FromRx_Click(object sender, RoutedEventArgs e)
    {
        var data = _rxProvider?.Invoke() ?? Array.Empty<byte>();
        if (data.Length == 0) { SummaryText.Text = "接收缓冲区为空（保存最近 1 MB 收到的原始字节）。"; return; }
        SetData(Box(sender), data);
    }

    private void FromRec_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "选择录制文件（取其中的接收数据）", Filter = "录制文件 (*.jsonl)|*.jsonl|所有文件 (*.*)|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var entries = SessionReader.ReadFile(dlg.FileName, out int bad);
            var rx = entries.Where(x => !x.IsTx).SelectMany(x => x.Data).ToArray();
            if (rx.Length == 0) { SummaryText.Text = "录制文件中没有接收数据。"; return; }
            SetData(Box(sender), rx);
            SummaryText.Text = $"已载入录制接收数据 {rx.Length} 字节（{entries.Count(x => !x.IsTx)} 条记录" + (bad > 0 ? $"，{bad} 行无法解析" : "") + "）。";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "读取录制失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Swap_Click(object sender, RoutedEventArgs e)
    {
        (InA.Text, InB.Text) = (InB.Text, InA.Text);
        if (_result != null) Compare_Click(sender, e);
    }

    // ---------------- 对比 ----------------

    private byte[]? _bytesA, _bytesB;
    private string[]? _linesA, _linesB;

    private void Compare_Click(object sender, RoutedEventArgs e)
    {
        _hexMode = HexMode;
        bool align = AlignCombo.SelectedIndex == 0;
        try
        {
            if (_hexMode)
            {
                if (!DataConvert.TryParseHex(InA.Text, out var a, out var ea)) { SummaryText.Text = "A：" + ea; return; }
                if (!DataConvert.TryParseHex(InB.Text, out var b, out var eb)) { SummaryText.Text = "B：" + eb; return; }
                _bytesA = a; _bytesB = b;
                _result = DataDiff.CompareBytes(a, b, align);
            }
            else
            {
                _linesA = DataDiff.SplitLines(InA.Text);
                _linesB = DataDiff.SplitLines(InB.Text);
                _result = DataDiff.CompareLines(_linesA, _linesB, IgnoreCaseChk.IsChecked == true, IgnoreWsChk.IsChecked == true, align);
            }
        }
        catch (Exception ex)
        {
            SummaryText.Text = "对比失败：" + ex.Message;
            return;
        }
        Render();
        var r = _result;
        string unit = _hexMode ? "字节" : "行";
        if (r.Identical)
            SummaryText.Text = $"完全相同（A {r.LengthA} {unit}，B {r.LengthB} {unit}）。";
        else
            SummaryText.Text = $"共 {r.Hunks.Count} 处差异；差异{unit}数 {r.DifferingCount}（A 侧 {r.ChangedA}，B 侧 {r.ChangedB}）；A {r.LengthA} {unit}，B {r.LengthB} {unit}"
                + (r.Approximate ? "。差异过多，已退化为按位置比较。" : "。")
                + (Truncated() ? $"  仅渲染前 {(_hexMode ? MaxRenderBytes / 1024 + " KB" : MaxRenderLines + " 行")}。" : "");
        _current = -1;
        CurrentText.Text = "";
        if (!r.Identical) Go(true);
    }

    private bool Truncated() => _hexMode
        ? (_bytesA?.Length ?? 0) > MaxRenderBytes || (_bytesB?.Length ?? 0) > MaxRenderBytes
        : (_linesA?.Length ?? 0) > MaxRenderLines || (_linesB?.Length ?? 0) > MaxRenderLines;

    private void Render()
    {
        _runsA.Clear();
        _runsB.Clear();
        var r = _result!;
        int lenA = r.LengthA, lenB = r.LengthB;
        var hunkA = new int[lenA];
        var hunkB = new int[lenB];
        Array.Fill(hunkA, -1);
        Array.Fill(hunkB, -1);
        var markA = new Dictionary<int, int>();
        var markB = new Dictionary<int, int>();
        for (int h = 0; h < r.Hunks.Count; h++)
        {
            var hk = r.Hunks[h];
            for (int i = 0; i < hk.ALen; i++) hunkA[hk.AStart + i] = h;
            for (int i = 0; i < hk.BLen; i++) hunkB[hk.BStart + i] = h;
            if (hk.ALen == 0) markA.TryAdd(hk.AStart, h);
            if (hk.BLen == 0) markB.TryAdd(hk.BStart, h);
        }
        ViewA.Document = Build(lenA, hunkA, markA, _runsA, DelBrush, true);
        ViewB.Document = Build(lenB, hunkB, markB, _runsB, InsBrush, false);
    }

    private FlowDocument Build(int len, int[] hunkOf, Dictionary<int, int> marks, Dictionary<int, List<Run>> runs, Brush bg, bool isA)
    {
        var doc = new FlowDocument { PagePadding = new Thickness(4), FontFamily = ViewA.FontFamily, FontSize = 12.5, PageWidth = _hexMode ? 640 : 4000 };
        var para = new Paragraph { Margin = new Thickness(0), LineHeight = 17 };
        doc.Blocks.Add(para);
        int limit = _hexMode ? Math.Min(len, MaxRenderBytes) : Math.Min(len, MaxRenderLines);
        var sb = new StringBuilder();
        int state = -1;

        void Flush()
        {
            if (sb.Length == 0) return;
            var run = new Run(sb.ToString());
            if (state >= 0)
            {
                run.Background = bg;
                run.Foreground = MarkFg;
                if (!runs.TryGetValue(state, out var list)) runs[state] = list = new List<Run>();
                list.Add(run);
            }
            para.Inlines.Add(run);
            sb.Clear();
        }
        void Marker(int h)
        {
            Flush();
            var m = new Run("▌") { Foreground = isA ? CurDelBrush : CurInsBrush };
            if (!runs.TryGetValue(h, out var list)) runs[h] = list = new List<Run>();
            list.Add(m);
            para.Inlines.Add(m);
        }

        for (int i = 0; i < limit; i++)
        {
            bool lineStart = _hexMode ? i % 16 == 0 : true;
            if (lineStart)
            {
                Flush();
                if (i > 0) para.Inlines.Add(new LineBreak());
                para.Inlines.Add(new Run(_hexMode ? i.ToString("X6") + "  " : (i + 1).ToString().PadLeft(5) + "  ") { Foreground = MutedFg });
            }
            if (marks.TryGetValue(i, out int mh)) Marker(mh);
            int st = hunkOf[i];
            if (st != state) { Flush(); state = st; }
            if (_hexMode)
            {
                sb.Append(_bytesOf(isA)[i].ToString("X2"));
                if (i % 16 != 15 && i != limit - 1) sb.Append(' ');
            }
            else sb.Append(_linesOf(isA)[i].Length == 0 ? " " : _linesOf(isA)[i]);
            if (!_hexMode) { Flush(); state = -1; }
        }
        Flush();
        if (marks.TryGetValue(limit, out int endMark) && limit == len) Marker(endMark);
        if (limit < len)
        {
            para.Inlines.Add(new LineBreak());
            para.Inlines.Add(new Run($"…（其余 {len - limit} {(_hexMode ? "字节" : "行")}未显示）") { Foreground = MutedFg });
        }
        return doc;
    }

    private byte[] _bytesOf(bool a) => (a ? _bytesA : _bytesB) ?? Array.Empty<byte>();
    private string[] _linesOf(bool a) => (a ? _linesA : _linesB) ?? Array.Empty<string>();

    // ---------------- 导航 ----------------

    private void Prev_Click(object sender, RoutedEventArgs e) => Go(false);
    private void Next_Click(object sender, RoutedEventArgs e) => Go(true);

    private void Go(bool forward)
    {
        if (_result == null || _result.Hunks.Count == 0) return;
        SetCurrentStyle(_current, false);
        _current = DataDiff.Navigate(_result.Hunks.Count, _current, forward);
        SetCurrentStyle(_current, true);
        var h = _result.Hunks[_current];
        string Pos(int start, int lenx) => _hexMode ? $"0x{start:X}（{start}）起 {lenx} 字节" : $"第 {start + 1} 行起 {lenx} 行";
        CurrentText.Text = $"第 {_current + 1}/{_result.Hunks.Count} 处（{h.Kind}）：A {Pos(h.AStart, h.ALen)} ↔ B {Pos(h.BStart, h.BLen)}"
            + (_runsA.ContainsKey(_current) || _runsB.ContainsKey(_current) ? "" : $"（超出渲染范围，未高亮显示）");
        if (_runsA.TryGetValue(_current, out var la) && la.Count > 0) la[0].BringIntoView();
        if (_runsB.TryGetValue(_current, out var lb) && lb.Count > 0) lb[0].BringIntoView();
    }

    private void SetCurrentStyle(int h, bool current)
    {
        if (h < 0) return;
        if (_runsA.TryGetValue(h, out var la))
            foreach (var r in la) if (r.Text != "▌") r.Background = current ? CurDelBrush : DelBrush;
        if (_runsB.TryGetValue(h, out var lb))
            foreach (var r in lb) if (r.Text != "▌") r.Background = current ? CurInsBrush : InsBrush;
    }
}
