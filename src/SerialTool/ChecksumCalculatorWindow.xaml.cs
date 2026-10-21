using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using SerialTool.Features;

namespace SerialTool;

public partial class ChecksumCalculatorWindow : Window
{
    private readonly Encoding _encoding;
    private readonly ChecksumKind _current;
    private bool _ready;

    public ChecksumCalculatorWindow(string content, bool hex, bool escape, Encoding encoding, ChecksumKind current)
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        _encoding = encoding;
        _current = current;
        HexChk.IsChecked = hex;
        EscapeChk.IsChecked = escape;
        EscapeChk.IsEnabled = !hex;
        InputBox.Text = content;
        _ready = true;
        Recalc();
    }

    private void Input_Changed(object sender, RoutedEventArgs e)
    {
        EscapeChk.IsEnabled = HexChk.IsChecked != true;
        Recalc();
    }

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e) => Recalc();

    private void Recalc()
    {
        if (!_ready) return;
        string text = InputBox.Text;
        byte[] data;
        if (HexChk.IsChecked == true)
        {
            if (!HexUtil.TryParse(text, out data, out string? err))
            {
                ResultBox.Text = err ?? "HEX 格式错误";
                InfoText.Text = "";
                return;
            }
        }
        else
        {
            data = EscapeChk.IsChecked == true ? EscapeUtil.Encode(text, _encoding) : _encoding.GetBytes(text);
        }

        var sb = new StringBuilder();
        ushort modbus = Checksum.ModbusCrc16(data);
        ushort xmodem = Checksum.Crc16Ccitt(data, 0x0000);
        ushort ccitt = Checksum.Crc16Ccitt(data, 0xFFFF);
        sb.AppendLine($"Modbus CRC16        追加: {Checksum.ToHex(Checksum.Compute(ChecksumKind.ModbusCrc16, data)),-6}  (值 0x{modbus:X4}，低字节在前)");
        sb.AppendLine($"CRC16 XModem        追加: {Checksum.ToHex(Checksum.Compute(ChecksumKind.Crc16XModem, data)),-6}  (值 0x{xmodem:X4}，poly 1021 初值 0000)");
        sb.AppendLine($"CRC16-CCITT-FALSE   追加: {Checksum.ToHex(Checksum.Compute(ChecksumKind.Crc16CcittFalse, data)),-6}  (值 0x{ccitt:X4}，poly 1021 初值 FFFF)");
        sb.AppendLine($"累加和 (8 位)        追加: {Checksum.ToHex(Checksum.Compute(ChecksumKind.Sum8, data)),-6}");
        sb.AppendLine($"XOR 异或             追加: {Checksum.ToHex(Checksum.Compute(ChecksumKind.Xor8, data)),-6}");
        sb.AppendLine();
        sb.AppendLine("数据 HEX: " + (data.Length <= 256 ? Checksum.ToHex(data) : Checksum.ToHex(data.AsSpan(0, 256)) + " …"));
        if (_current != ChecksumKind.None)
        {
            var full = Checksum.Append(_current, data);
            sb.AppendLine($"按当前设置（{Checksum.DisplayName(_current)}）的完整帧: "
                          + (full.Length <= 256 ? Checksum.ToHex(full) : "（过长，省略）"));
        }
        ResultBox.Text = sb.ToString();
        InfoText.Text = $"{data.Length} 字节";
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(ResultBox.Text);
            InfoText.Text = "已复制";
        }
        catch (Exception ex)
        {
            InfoText.Text = "复制失败：" + ex.Message;
        }
    }
}
