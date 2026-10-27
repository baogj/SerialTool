using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using SerialTool.Features;

namespace SerialTool;

public partial class DataConvertWindow : Window
{
    private bool _sync;
    private byte[] _bytes = Array.Empty<byte>();

    public sealed class InterpRow
    {
        public string Type { get; set; } = "";
        public string ABCD { get; set; } = "";
        public string DCBA { get; set; } = "";
        public string BADC { get; set; } = "";
        public string CDAB { get; set; } = "";
    }

    public DataConvertWindow(string? initialHex = null, string? encoding = null)
    {
        InitializeComponent();
        EncCombo.ItemsSource = EncodingUtil.Names;
        EncCombo.SelectedItem = EncodingUtil.Names.Contains(encoding) ? encoding : "UTF-8";
        TypeCombo.ItemsSource = DataConvert.AllTypes.Select(DataConvert.TypeLabel).ToList();
        TypeCombo.SelectedIndex = Array.IndexOf(DataConvert.AllTypes, NumType.Float);
        OrderCombo.ItemsSource = DataConvert.AllOrders.Select(DataConvert.OrderLabel).ToList();
        OrderCombo.SelectedIndex = 0;
        if (!string.IsNullOrWhiteSpace(initialHex)) HexBox.Text = initialHex;
        else UpdateInterp();
    }

    private Encoding Enc => EncodingUtil.Get(EncCombo.SelectedItem as string ?? "UTF-8");
    private NumType CurType => DataConvert.AllTypes[Math.Max(0, TypeCombo.SelectedIndex)];
    private ByteOrder CurOrder => DataConvert.AllOrders[Math.Max(0, OrderCombo.SelectedIndex)];

    private void ByteBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_sync || !IsLoaded && sender != HexBox) return;
        var box = (TextBox)sender;
        var fmt = box == HexBox ? ByteFormat.Hex : box == TextBox1 ? ByteFormat.Text : box == DecBox ? ByteFormat.Decimal : ByteFormat.Binary;
        if (!DataConvert.TryParse(box.Text, fmt, Enc, out var data, out var err))
        {
            ByteError.Text = err;
            return;
        }
        ByteError.Text = "";
        SetBytes(data, box);
    }

    private void SetBytes(byte[] data, TextBox? source)
    {
        _bytes = data;
        _sync = true;
        try
        {
            if (source != HexBox) HexBox.Text = DataConvert.Format(data, ByteFormat.Hex, Enc);
            if (source != TextBox1) TextBox1.Text = DataConvert.Format(data, ByteFormat.Text, Enc);
            if (source != DecBox) DecBox.Text = DataConvert.Format(data, ByteFormat.Decimal, Enc);
            if (source != BinBox) BinBox.Text = DataConvert.Format(data, ByteFormat.Binary, Enc);
        }
        finally { _sync = false; }
        ByteInfo.Text = $"{data.Length} 字节";
        UpdateInterp();
    }

    private void EncCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        // 以 HEX 为准，重新解码文本
        SetBytes(_bytes, null);
    }

    private void Swap_Click(object sender, RoutedEventArgs e)
    {
        int n = int.Parse((string)((Button)sender).Tag);
        var r = n == 0 ? _bytes.Reverse().ToArray() : DataConvert.EndianSwap(_bytes, n);
        ByteError.Text = n > 0 && _bytes.Length % n != 0 ? $"提示：长度 {_bytes.Length} 不是 {n} 的倍数，末尾 {_bytes.Length % n} 字节未交换" : "";
        SetBytes(r, null);
    }

    private void Clear_Click(object sender, RoutedEventArgs e) { ByteError.Text = ""; SetBytes(Array.Empty<byte>(), null); }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (FindName((string)((Button)sender).Tag) is TextBox tb)
        {
            try { Clipboard.SetText(tb.Text ?? ""); } catch { /* 剪贴板被占用 */ }
        }
    }

    // ---------------- 数值 ----------------

    private void NumOpt_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (!string.IsNullOrWhiteSpace(NumBox.Text)) NumBox_TextChanged(NumBox, null!);
        else if (!string.IsNullOrWhiteSpace(NumHexBox.Text)) NumHexBox_TextChanged(NumHexBox, null!);
    }

    private void NumBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_sync) return;
        if (string.IsNullOrWhiteSpace(NumBox.Text)) { NumError.Text = ""; return; }
        if (!DataConvert.TryEncode(CurType, NumBox.Text, CurOrder, out var b, out var err)) { NumError.Text = err; return; }
        NumError.Text = "";
        _sync = true;
        try { NumHexBox.Text = DataConvert.Format(b, ByteFormat.Hex, Enc); } finally { _sync = false; }
    }

    private void NumHexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_sync) return;
        if (string.IsNullOrWhiteSpace(NumHexBox.Text)) { NumError.Text = ""; return; }
        if (!DataConvert.TryParseHex(NumHexBox.Text, out var b, out var err)) { NumError.Text = err; return; }
        int size = DataConvert.SizeOf(CurType);
        if (b.Length != size) { NumError.Text = $"{DataConvert.TypeLabel(CurType)} 需要 {size} 字节，当前 {b.Length} 字节"; return; }
        DataConvert.TryDecode(CurType, b, CurOrder, out var v);
        NumError.Text = "";
        _sync = true;
        try { NumBox.Text = v; } finally { _sync = false; }
    }

    private void NumToTop_Click(object sender, RoutedEventArgs e)
    {
        if (DataConvert.TryParseHex(NumHexBox.Text, out var b, out _) && b.Length > 0) SetBytes(b, null);
    }

    // ---------------- 解读表 ----------------

    private void OffsetBox_TextChanged(object sender, TextChangedEventArgs e) { if (IsLoaded) UpdateInterp(); }

    private void UpdateInterp()
    {
        if (InterpGrid == null) return;
        int off = int.TryParse(OffsetBox?.Text, out var o) && o >= 0 ? o : 0;
        var rows = new List<InterpRow>();
        foreach (var t in DataConvert.AllTypes)
        {
            int size = DataConvert.SizeOf(t);
            var row = new InterpRow { Type = DataConvert.TypeLabel(t) };
            if (off + size <= _bytes.Length)
            {
                var chunk = _bytes.Skip(off).Take(size).ToArray();
                string D(ByteOrder bo) => DataConvert.TryDecode(t, chunk, bo, out var s) ? s : "";
                row.ABCD = D(ByteOrder.ABCD);
                row.DCBA = D(ByteOrder.DCBA);
                row.BADC = size >= 2 ? D(ByteOrder.BADC) : "";
                row.CDAB = size >= 4 ? D(ByteOrder.CDAB) : "";
            }
            rows.Add(row);
        }
        InterpGrid.ItemsSource = rows;
    }
}
