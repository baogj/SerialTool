using System;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using SerialTool.Features;

namespace SerialTool;

public partial class CryptoCalcWindow : Window
{
    private enum Op
    {
        Base64Enc, Base64Dec, UrlEnc, UrlDec, HexToText, TextToHex,
        Crc, CrcAll, Md5, Sha1, Sha256, Sha512,
        AesEnc, AesDec, Xor, Bcc, BcdEnc, BcdDec,
    }

    private static readonly (Op op, string label)[] Ops =
    {
        (Op.Base64Enc, "Base64 编码"), (Op.Base64Dec, "Base64 解码"),
        (Op.UrlEnc, "URL 编码"), (Op.UrlDec, "URL 解码"),
        (Op.HexToText, "HEX → 文本"), (Op.TextToHex, "文本 → HEX"),
        (Op.Crc, "CRC 校验"), (Op.CrcAll, "CRC（全部算法一览）"),
        (Op.Md5, "MD5"), (Op.Sha1, "SHA1"), (Op.Sha256, "SHA256"), (Op.Sha512, "SHA512"),
        (Op.AesEnc, "AES 加密"), (Op.AesDec, "AES 解密"),
        (Op.Xor, "XOR（密钥循环）"), (Op.Bcc, "异或和 BCC"),
        (Op.BcdEnc, "BCD 编码（数字 → BCD）"), (Op.BcdDec, "BCD 解码（BCD → 数字）"),
    };

    private static readonly string[] Fmts = { "文本", "HEX" };
    private bool _init = true;

    public CryptoCalcWindow(string? encoding = null)
    {
        InitializeComponent();
        OpCombo.ItemsSource = Ops.Select(o => o.label).ToList();
        EncCombo.ItemsSource = EncodingUtil.Names;
        EncCombo.SelectedItem = EncodingUtil.Names.Contains(encoding) ? encoding : "UTF-8";
        CrcCombo.ItemsSource = CryptoCalc.Crcs.Select(c => c.Name).ToList();
        CrcCombo.SelectedItem = "CRC-32";
        CrcOrderCombo.ItemsSource = new[] { "大端（高字节在前）", "小端（低字节在前）" };
        CrcOrderCombo.SelectedIndex = 0;
        foreach (var cb in new[] { InFmtCombo, OutFmtCombo, KeyFmtCombo, IvFmtCombo, XorFmtCombo }) cb.ItemsSource = Fmts;
        InFmtCombo.SelectedIndex = 0;
        OutFmtCombo.SelectedIndex = 1;
        KeyFmtCombo.SelectedIndex = 1;
        IvFmtCombo.SelectedIndex = 1;
        XorFmtCombo.SelectedIndex = 1;
        ModeCombo.ItemsSource = Enum.GetNames(typeof(CipherMode2));
        ModeCombo.SelectedIndex = 1;
        PadCombo.ItemsSource = new[] { "PKCS7", "Zero", "None" };
        PadCombo.SelectedIndex = 0;
        InBox.Text = "123456789";
        _init = false;
        OpCombo.SelectedIndex = Array.FindIndex(Ops, o => o.op == Op.Crc);
    }

    private Op CurOp => Ops[Math.Max(0, OpCombo.SelectedIndex)].op;
    private Encoding Enc => EncodingUtil.Get(EncCombo.SelectedItem as string ?? "UTF-8");
    private static bool IsHex(ComboBox cb) => cb.SelectedIndex == 1;

    private void Op_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_init) return;
        var op = CurOp;
        CrcPanel.Visibility = op == Op.Crc ? Visibility.Visible : Visibility.Collapsed;
        AesPanel.Visibility = op is Op.AesEnc or Op.AesDec ? Visibility.Visible : Visibility.Collapsed;
        XorPanel.Visibility = op == Op.Xor ? Visibility.Visible : Visibility.Collapsed;
        _init = true;
        try
        {
            // 每种运算的常用输入/输出格式
            switch (op)
            {
                case Op.Base64Enc: case Op.UrlEnc: OutFmtCombo.SelectedIndex = 0; break;
                case Op.Base64Dec: InFmtCombo.SelectedIndex = 0; break;
                case Op.UrlDec: InFmtCombo.SelectedIndex = 0; OutFmtCombo.SelectedIndex = 0; break;
                case Op.HexToText: InFmtCombo.SelectedIndex = 1; OutFmtCombo.SelectedIndex = 0; break;
                case Op.TextToHex: InFmtCombo.SelectedIndex = 0; OutFmtCombo.SelectedIndex = 1; break;
                case Op.Crc: case Op.Md5: case Op.Sha1: case Op.Sha256: case Op.Sha512: case Op.Bcc: case Op.BcdEnc: case Op.AesEnc: case Op.Xor:
                    OutFmtCombo.SelectedIndex = 1; break;
                case Op.AesDec: InFmtCombo.SelectedIndex = 1; OutFmtCombo.SelectedIndex = 0; break;
                case Op.BcdDec: InFmtCombo.SelectedIndex = 1; OutFmtCombo.SelectedIndex = 0; break;
            }
        }
        finally { _init = false; }
        Compute();
    }

    private void Any_Changed(object sender, SelectionChangedEventArgs e) { if (!_init) Compute(); }
    private void Any_TextChanged(object sender, TextChangedEventArgs e) { if (!_init) Compute(); }

    private byte[] ParseField(string text, ComboBox fmt, string what)
    {
        if (IsHex(fmt))
        {
            if (!DataConvert.TryParseHex(text, out var b, out var err)) throw new FormatException(what + "：" + err);
            return b;
        }
        return Enc.GetBytes(text ?? "");
    }

    /// <summary>需要字符串输入的运算：文本格式直接取原文；HEX 格式先解析为字节再按编码解码。</summary>
    private string InputString() => IsHex(InFmtCombo) ? Enc.GetString(ParseField(InBox.Text, InFmtCombo, "输入")) : InBox.Text ?? "";

    private void Compute()
    {
        if (OutBox == null) return;
        ErrorText.Text = "";
        OutInfo.Text = "";
        CrcInfo.Text = "";
        AesInfo.Text = "";
        try
        {
            byte[]? outBytes = null;
            string? outText = null; // 纯文本结果（不受输出格式影响）
            var op = CurOp;
            switch (op)
            {
                case Op.Base64Enc: outBytes = Encoding.ASCII.GetBytes(CryptoCalc.Base64Encode(ParseField(InBox.Text, InFmtCombo, "输入"))); break;
                case Op.Base64Dec: outBytes = CryptoCalc.Base64Decode(InputString()); break;
                case Op.UrlEnc: outBytes = Encoding.ASCII.GetBytes(CryptoCalc.UrlEncode(InputString(), Enc)); break;
                case Op.UrlDec: outBytes = Enc.GetBytes(CryptoCalc.UrlDecode(InputString(), Enc)); break;
                case Op.HexToText:
                case Op.TextToHex: outBytes = ParseField(InBox.Text, InFmtCombo, "输入"); break;
                case Op.Crc:
                {
                    var spec = CryptoCalc.FindCrc(CrcCombo.SelectedItem as string ?? "CRC-32") ?? CryptoCalc.Crcs[0];
                    var data = ParseField(InBox.Text, InFmtCombo, "输入");
                    ulong v = spec.Compute(data);
                    outBytes = spec.ToBytes(v, CrcOrderCombo.SelectedIndex == 1);
                    CrcInfo.Text = $"值 = 0x{spec.Format(v)}   多项式 0x{spec.Poly.ToString("X")}  初值 0x{spec.Init:X}  反射 {(spec.RefIn ? "是" : "否")}  异或 0x{spec.XorOut:X}";
                    break;
                }
                case Op.CrcAll:
                {
                    var data = ParseField(InBox.Text, InFmtCombo, "输入");
                    var sb = new StringBuilder();
                    foreach (var c in CryptoCalc.Crcs) sb.Append(c.Name.PadRight(24)).Append("0x").AppendLine(c.Format(c.Compute(data)));
                    sb.Append("XOR 异或和".PadRight(22)).Append("0x").AppendLine(CryptoCalc.XorAll(data).ToString("X2"));
                    sb.Append("累加和 (8 位)".PadRight(22)).Append("0x").AppendLine(((byte)data.Sum(b => b)).ToString("X2"));
                    outText = sb.ToString();
                    break;
                }
                case Op.Md5: outBytes = CryptoCalc.Md5(ParseField(InBox.Text, InFmtCombo, "输入")); break;
                case Op.Sha1: outBytes = CryptoCalc.Sha1(ParseField(InBox.Text, InFmtCombo, "输入")); break;
                case Op.Sha256: outBytes = CryptoCalc.Sha256(ParseField(InBox.Text, InFmtCombo, "输入")); break;
                case Op.Sha512: outBytes = CryptoCalc.Sha512(ParseField(InBox.Text, InFmtCombo, "输入")); break;
                case Op.AesEnc:
                case Op.AesDec:
                {
                    var key = ParseField(KeyBox.Text, KeyFmtCombo, "密钥");
                    var iv = ParseField(IvBox.Text, IvFmtCombo, "IV");
                    var mode = (CipherMode2)Math.Max(0, ModeCombo.SelectedIndex);
                    var pad = (PadMode)Math.Max(0, PadCombo.SelectedIndex);
                    AesInfo.Text = key.Length is 16 or 24 or 32 ? $"AES-{key.Length * 8}" + (mode == CipherMode2.CTR ? "（CTR 为流模式，不使用填充）" : "") : $"密钥 {key.Length} 字节";
                    var data = ParseField(InBox.Text, InFmtCombo, "输入");
                    outBytes = op == Op.AesEnc ? CryptoCalc.AesEncrypt(key, iv, data, mode, pad) : CryptoCalc.AesDecrypt(key, iv, data, mode, pad);
                    break;
                }
                case Op.Xor: outBytes = CryptoCalc.Xor(ParseField(InBox.Text, InFmtCombo, "输入"), ParseField(XorKeyBox.Text, XorFmtCombo, "XOR 密钥")); break;
                case Op.Bcc: outBytes = new[] { CryptoCalc.XorAll(ParseField(InBox.Text, InFmtCombo, "输入")) }; break;
                case Op.BcdEnc: outBytes = CryptoCalc.BcdEncode(InputString()); break;
                case Op.BcdDec: outBytes = Encoding.ASCII.GetBytes(CryptoCalc.BcdDecode(ParseField(InBox.Text, InFmtCombo, "输入"))); break;
            }
            if (outText != null)
            {
                OutBox.Text = outText;
                OutInfo.Text = "";
            }
            else if (outBytes != null)
            {
                OutBox.Text = IsHex(OutFmtCombo) ? DataConvert.Format(outBytes, ByteFormat.Hex, Enc) : Enc.GetString(outBytes);
                OutInfo.Text = $"{outBytes.Length} 字节";
            }
        }
        catch (Exception ex)
        {
            OutBox.Text = "";
            ErrorText.Text = ex.Message;
        }
    }

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        try { if (Clipboard.ContainsText()) InBox.Text = Clipboard.GetText(); } catch { }
    }

    private void ClearIn_Click(object sender, RoutedEventArgs e) => InBox.Clear();

    private void CopyOut_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(OutBox.Text ?? ""); } catch { }
    }

    private void OutToIn_Click(object sender, RoutedEventArgs e)
    {
        string t = OutBox.Text;
        _init = true;
        InFmtCombo.SelectedIndex = OutFmtCombo.SelectedIndex;
        _init = false;
        InBox.Text = t;
    }
}
