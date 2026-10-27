using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Input;

namespace SerialTool;

/// <summary>迷你模式：只显示最近 N 行接收、发送框、打开/关闭与还原按钮。</summary>
public partial class MiniWindow : Window
{
    private readonly MainWindow _main;
    private readonly LinkedList<string> _lines = new();
    private readonly StringBuilder _partial = new();
    private Decoder? _decoder;
    private Encoding? _decEnc;
    private bool _dirty;
    private bool _restoring;

    public int MaxLines { get; set; } = 30;
    public bool HexMode => HexChk.IsChecked == true;

    public MiniWindow(MainWindow main, bool hex, bool topmost)
    {
        InitializeComponent();
        _main = main;
        HexChk.IsChecked = hex;
        TopChk.IsChecked = topmost;
        Topmost = topmost;
        SourceInitialized += (_, _) => _main.ApplyOpacityTo(this);
        Closing += (_, _) => { if (!_restoring) _main.ExitMiniMode(fromMini: true); };
    }

    /// <summary>送入一段接收数据。</summary>
    public void Feed(byte[] data, Encoding enc)
    {
        if (HexMode)
        {
            AddLine(string.Join(" ", data.Select(b => b.ToString("X2"))));
            return;
        }
        if (_decoder == null || _decEnc != enc) { _decoder = enc.GetDecoder(); _decEnc = enc; }
        var chars = new char[enc.GetMaxCharCount(data.Length)];
        int n = _decoder.GetChars(data, 0, data.Length, chars, 0);
        for (int i = 0; i < n; i++)
        {
            char c = chars[i];
            if (c == '\n') { AddLine(_partial.ToString()); _partial.Clear(); }
            else if (c != '\r') _partial.Append(c);
        }
        if (_partial.Length > 4096) { AddLine(_partial.ToString()); _partial.Clear(); }
        _dirty = true;
    }

    private void AddLine(string line)
    {
        _lines.AddLast(line);
        while (_lines.Count > Math.Max(1, MaxLines)) _lines.RemoveFirst();
        _dirty = true;
    }

    /// <summary>由主窗口在每次刷新时调用：更新显示与连接状态。</summary>
    public void Flush(bool open, string state)
    {
        OpenBtn.Content = open ? "关闭" : "打开";
        StateText.Text = state;
        if (!_dirty) return;
        _dirty = false;
        var sb = new StringBuilder();
        foreach (var l in _lines) sb.Append(l).Append("\r\n");
        sb.Append(_partial);
        RxView.Text = sb.ToString();
        RxView.ScrollToEnd();
    }

    public void CloseForRestore()
    {
        _restoring = true;
        Close();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _lines.Clear();
        _partial.Clear();
        _dirty = true;
        Flush(_main.IsConnectionOpen, StateText.Text);
    }

    private void HexChk_Click(object sender, RoutedEventArgs e)
    {
        if (_partial.Length > 0) { AddLine(_partial.ToString()); _partial.Clear(); }
        _decoder = null;
    }

    private void TopChk_Click(object sender, RoutedEventArgs e) => Topmost = TopChk.IsChecked == true;
    private void Restore_Click(object sender, RoutedEventArgs e) => _main.ExitMiniMode(fromMini: false);
    private void Open_Click(object sender, RoutedEventArgs e) => _main.ToggleOpenFromMini();

    private void Send_Click(object sender, RoutedEventArgs e)
    {
        if (_main.SendFromMini(SendBox.Text, SendHexChk.IsChecked == true)) SendBox.SelectAll();
    }

    private void SendBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Return)
        {
            Send_Click(sender, e);
            e.Handled = true;
        }
    }

    public bool TopmostChecked => TopChk.IsChecked == true;
}
