using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SerialTool;

public static class HexUtil
{
    private const string Digits = "0123456789ABCDEF";

    public static string ToHex(byte[] data)
    {
        var sb = new StringBuilder(data.Length * 3);
        for (int i = 0; i < data.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(Digits[data[i] >> 4]).Append(Digits[data[i] & 0xF]);
        }
        return sb.ToString();
    }

    private static int HexVal(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        return -1;
    }

    /// <summary>
    /// 解析 HEX 字符串。支持空格、逗号、分号、横线、冒号、换行分隔，支持 0x 前缀，
    /// 单个数字的片段（如 "A"）视为 0A；连续的偶数位片段（如 "AA55"）按两位拆分。
    /// </summary>
    public static bool TryParse(string input, out byte[] data, out string? error)
    {
        var result = new List<byte>();
        data = Array.Empty<byte>();
        error = null;
        var tokens = input.Split(new[] { ' ', '\t', '\r', '\n', ',', ';', '-', ':' }, StringSplitOptions.RemoveEmptyEntries);
        for (int t = 0; t < tokens.Length; t++)
        {
            string tok = tokens[t];
            if (tok.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) tok = tok.Substring(2);
            if (tok.Length == 0)
            {
                error = $"HEX 格式错误：第 {t + 1} 个片段 “{tokens[t]}” 为空";
                return false;
            }
            foreach (char c in tok)
            {
                if (HexVal(c) < 0)
                {
                    error = $"HEX 格式错误：非法字符 “{c}”（片段 “{tokens[t]}”）";
                    return false;
                }
            }
            if (tok.Length == 1)
            {
                result.Add((byte)HexVal(tok[0]));
                continue;
            }
            if (tok.Length % 2 != 0)
            {
                error = $"HEX 格式错误：片段 “{tokens[t]}” 位数为奇数";
                return false;
            }
            for (int i = 0; i < tok.Length; i += 2)
                result.Add((byte)(HexVal(tok[i]) * 16 + HexVal(tok[i + 1])));
        }
        data = result.ToArray();
        return true;
    }
}

public static class EscapeUtil
{
    /// <summary>
    /// 把带转义符的文本编码为字节：支持 \r \n \t \0 \\ \xHH（\xHH 直接输出原始字节）。
    /// 未识别的转义保持原样。
    /// </summary>
    public static byte[] Encode(string input, Encoding enc)
    {
        using var ms = new MemoryStream();
        var text = new StringBuilder();

        void FlushText()
        {
            if (text.Length == 0) return;
            byte[] b = enc.GetBytes(text.ToString());
            ms.Write(b, 0, b.Length);
            text.Clear();
        }

        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if (c != '\\' || i + 1 >= input.Length)
            {
                text.Append(c);
                continue;
            }
            char n = input[i + 1];
            switch (n)
            {
                case 'r': text.Append('\r'); i++; break;
                case 'n': text.Append('\n'); i++; break;
                case 't': text.Append('\t'); i++; break;
                case '0': text.Append('\0'); i++; break;
                case '\\': text.Append('\\'); i++; break;
                case 'x':
                case 'X':
                    if (IsHex(input, i + 2) && IsHex(input, i + 3))
                    {
                        FlushText();
                        ms.WriteByte(Convert.ToByte(input.Substring(i + 2, 2), 16));
                        i += 3;
                    }
                    else
                    {
                        text.Append(c);
                    }
                    break;
                default:
                    text.Append(c);
                    break;
            }
        }
        FlushText();
        return ms.ToArray();
    }

    private static bool IsHex(string s, int idx)
    {
        if (idx >= s.Length) return false;
        char c = s[idx];
        return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
    }
}

public static class EncodingUtil
{
    public static readonly string[] Names = { "UTF-8", "GBK", "ASCII" };

    public static Encoding Get(string name)
    {
        try
        {
            return name switch
            {
                "GBK" => Encoding.GetEncoding(936),
                "ASCII" => Encoding.ASCII,
                _ => new UTF8Encoding(false),
            };
        }
        catch
        {
            return new UTF8Encoding(false);
        }
    }
}
