using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SerialTool.Features;

/// <summary>参数化 CRC（Rocksoft 模型：宽度、多项式、初值、输入/输出反射、结果异或）。</summary>
public sealed class CrcSpec
{
    public string Name { get; }
    public int Width { get; }
    public ulong Poly { get; }
    public ulong Init { get; }
    public bool RefIn { get; }
    public bool RefOut { get; }
    public ulong XorOut { get; }
    /// <summary>"123456789" 的标准校验值。</summary>
    public ulong Check { get; }

    public CrcSpec(string name, int width, ulong poly, ulong init, bool refIn, bool refOut, ulong xorOut, ulong check)
    {
        Name = name; Width = width; Poly = poly; Init = init; RefIn = refIn; RefOut = refOut; XorOut = xorOut; Check = check;
    }

    private ulong Mask => Width >= 64 ? ulong.MaxValue : (1UL << Width) - 1;

    public ulong Compute(byte[] data)
    {
        ulong mask = Mask;
        ulong top = 1UL << (Width - 1);
        ulong crc = Init & mask;
        if (RefIn)
        {
            ulong rpoly = Reflect(Poly, Width);
            crc = Reflect(crc, Width);
            foreach (byte b in data)
            {
                crc ^= b;
                for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ rpoly : crc >> 1;
            }
            if (!RefOut) crc = Reflect(crc, Width);
        }
        else
        {
            foreach (byte b in data)
            {
                if (Width >= 8) crc ^= (ulong)b << (Width - 8);
                for (int k = 0; k < 8; k++)
                {
                    if (Width < 8)
                    {
                        // 宽度小于 8 的 CRC：逐位送入
                        bool bit = ((b >> (7 - k)) & 1) != 0;
                        bool hi = (crc & top) != 0;
                        crc = (crc << 1) & mask;
                        if (hi ^ bit) crc ^= Poly;
                        continue;
                    }
                    crc = (crc & top) != 0 ? ((crc << 1) ^ Poly) & mask : (crc << 1) & mask;
                }
            }
            if (RefOut) crc = Reflect(crc, Width);
        }
        return (crc ^ XorOut) & mask;
    }

    /// <summary>CRC 值的大端字节（按宽度取 1/2/4 字节）。</summary>
    public byte[] ToBytes(ulong crc, bool littleEndian = false)
    {
        int n = (Width + 7) / 8;
        var r = new byte[n];
        for (int i = 0; i < n; i++) r[n - 1 - i] = (byte)(crc >> (8 * i));
        if (littleEndian) Array.Reverse(r);
        return r;
    }

    public string Format(ulong crc) => crc.ToString("X" + ((Width + 3) / 4));

    private static ulong Reflect(ulong v, int width)
    {
        ulong r = 0;
        for (int i = 0; i < width; i++) { r = (r << 1) | (v & 1); v >>= 1; }
        return r;
    }
}

public enum CipherMode2 { ECB, CBC, CTR }
public enum PadMode { PKCS7, Zero, None }

/// <summary>加密 / 编码计算器的核心算法。不依赖 WPF。</summary>
public static class CryptoCalc
{
    public static readonly CrcSpec[] Crcs =
    {
        new("CRC-8", 8, 0x07, 0x00, false, false, 0x00, 0xF4),
        new("CRC-8/ITU", 8, 0x07, 0x00, false, false, 0x55, 0xA1),
        new("CRC-8/ROHC", 8, 0x07, 0xFF, true, true, 0x00, 0xD0),
        new("CRC-8/MAXIM", 8, 0x31, 0x00, true, true, 0x00, 0xA1),
        new("CRC-16/MODBUS", 16, 0x8005, 0xFFFF, true, true, 0x0000, 0x4B37),
        new("CRC-16/IBM (ARC)", 16, 0x8005, 0x0000, true, true, 0x0000, 0xBB3D),
        new("CRC-16/USB", 16, 0x8005, 0xFFFF, true, true, 0xFFFF, 0xB4C8),
        new("CRC-16/MAXIM", 16, 0x8005, 0x0000, true, true, 0xFFFF, 0x44C2),
        new("CRC-16/CCITT-FALSE", 16, 0x1021, 0xFFFF, false, false, 0x0000, 0x29B1),
        new("CRC-16/XMODEM", 16, 0x1021, 0x0000, false, false, 0x0000, 0x31C3),
        new("CRC-16/KERMIT (CCITT)", 16, 0x1021, 0x0000, true, true, 0x0000, 0x2189),
        new("CRC-16/X25", 16, 0x1021, 0xFFFF, true, true, 0xFFFF, 0x906E),
        new("CRC-16/DNP", 16, 0x3D65, 0x0000, true, true, 0xFFFF, 0xEA82),
        new("CRC-32", 32, 0x04C11DB7, 0xFFFFFFFF, true, true, 0xFFFFFFFF, 0xCBF43926),
        new("CRC-32C (Castagnoli)", 32, 0x1EDC6F41, 0xFFFFFFFF, true, true, 0xFFFFFFFF, 0xE3069283),
        new("CRC-32/MPEG-2", 32, 0x04C11DB7, 0xFFFFFFFF, false, false, 0x00000000, 0x0376E6E7),
        new("CRC-32/BZIP2", 32, 0x04C11DB7, 0xFFFFFFFF, false, false, 0xFFFFFFFF, 0xFC891918),
    };

    public static CrcSpec? FindCrc(string name) => Crcs.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    // ---------------- 编码 ----------------

    public static string Base64Encode(byte[] data) => Convert.ToBase64String(data);

    /// <summary>Base64 解码：忽略空白，兼容 URL 安全字符（-_）与缺失的 '=' 填充。</summary>
    public static byte[] Base64Decode(string text)
    {
        var s = new string((text ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray()).Replace('-', '+').Replace('_', '/');
        int mod = s.Length % 4;
        if (mod == 1) throw new FormatException("Base64 长度无效");
        if (mod > 0) s += new string('=', 4 - mod);
        return Convert.FromBase64String(s);
    }

    /// <summary>URL 编码（RFC 3986，按 UTF-8 等指定编码逐字节 %XX）。</summary>
    public static string UrlEncode(string text, Encoding enc)
    {
        var sb = new StringBuilder();
        foreach (byte b in enc.GetBytes(text ?? ""))
        {
            char c = (char)b;
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.' || c == '~')
                sb.Append(c);
            else sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    /// <summary>URL 解码：%XX 按字节解码，'+' 视为空格。</summary>
    public static string UrlDecode(string text, Encoding enc)
    {
        var bytes = new List<byte>();
        text ??= "";
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '%' && i + 2 < text.Length && IsHex(text[i + 1]) && IsHex(text[i + 2]))
            {
                bytes.Add(Convert.ToByte(text.Substring(i + 1, 2), 16));
                i += 2;
            }
            else if (c == '+') bytes.Add((byte)' ');
            else bytes.AddRange(enc.GetBytes(c.ToString()));
        }
        return enc.GetString(bytes.ToArray());
    }

    private static bool IsHex(char c) => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    // ---------------- 摘要 ----------------

    public static byte[] Md5(byte[] data) { using var h = MD5.Create(); return h.ComputeHash(data); }
    public static byte[] Sha1(byte[] data) { using var h = SHA1.Create(); return h.ComputeHash(data); }
    public static byte[] Sha256(byte[] data) { using var h = SHA256.Create(); return h.ComputeHash(data); }
    public static byte[] Sha512(byte[] data) { using var h = SHA512.Create(); return h.ComputeHash(data); }

    // ---------------- AES ----------------

    public static void ValidateAes(byte[] key, byte[] iv, CipherMode2 mode)
    {
        if (key.Length != 16 && key.Length != 24 && key.Length != 32)
            throw new ArgumentException($"AES 密钥长度应为 16/24/32 字节（AES-128/192/256），当前 {key.Length} 字节");
        if (mode != CipherMode2.ECB && iv.Length != 16)
            throw new ArgumentException($"{mode} 模式需要 16 字节 IV（CTR 为初始计数器），当前 {iv.Length} 字节");
    }

    public static byte[] AesEncrypt(byte[] key, byte[] iv, byte[] data, CipherMode2 mode, PadMode pad)
    {
        ValidateAes(key, iv, mode);
        if (mode == CipherMode2.CTR) return AesCtr(key, iv, data); // 流模式，不填充
        byte[] padded = Pad(data, pad);
        using var aes = Aes.Create();
        aes.Key = key;
        return mode == CipherMode2.ECB
            ? aes.EncryptEcb(padded, PaddingMode.None)
            : aes.EncryptCbc(padded, iv, PaddingMode.None);
    }

    public static byte[] AesDecrypt(byte[] key, byte[] iv, byte[] data, CipherMode2 mode, PadMode pad)
    {
        ValidateAes(key, iv, mode);
        if (mode == CipherMode2.CTR) return AesCtr(key, iv, data);
        if (data.Length % 16 != 0) throw new ArgumentException($"密文长度必须是 16 的倍数，当前 {data.Length} 字节");
        using var aes = Aes.Create();
        aes.Key = key;
        byte[] plain = mode == CipherMode2.ECB
            ? aes.DecryptEcb(data, PaddingMode.None)
            : aes.DecryptCbc(data, iv, PaddingMode.None);
        return Unpad(plain, pad);
    }

    /// <summary>AES-CTR：计数器为 16 字节大端整数，每块加 1（与 NIST SP 800-38A 一致）。</summary>
    public static byte[] AesCtr(byte[] key, byte[] iv, byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        var counter = (byte[])iv.Clone();
        var output = new byte[data.Length];
        int blocks = (data.Length + 15) / 16;
        var ctrs = new byte[blocks * 16];
        for (int b = 0; b < blocks; b++)
        {
            Array.Copy(counter, 0, ctrs, b * 16, 16);
            for (int i = 15; i >= 0; i--) { if (++counter[i] != 0) break; }
        }
        var ks = aes.EncryptEcb(ctrs, PaddingMode.None);
        for (int i = 0; i < data.Length; i++) output[i] = (byte)(data[i] ^ ks[i]);
        return output;
    }

    public static byte[] Pad(byte[] data, PadMode pad)
    {
        switch (pad)
        {
            case PadMode.PKCS7:
            {
                int n = 16 - data.Length % 16;
                var r = new byte[data.Length + n];
                Array.Copy(data, r, data.Length);
                for (int i = data.Length; i < r.Length; i++) r[i] = (byte)n;
                return r;
            }
            case PadMode.Zero:
            {
                if (data.Length % 16 == 0 && data.Length > 0) return (byte[])data.Clone();
                var r = new byte[(data.Length / 16 + 1) * 16];
                Array.Copy(data, r, data.Length);
                return r;
            }
            default:
                if (data.Length % 16 != 0) throw new ArgumentException($"无填充时明文长度必须是 16 的倍数，当前 {data.Length} 字节");
                return (byte[])data.Clone();
        }
    }

    public static byte[] Unpad(byte[] data, PadMode pad)
    {
        switch (pad)
        {
            case PadMode.PKCS7:
            {
                if (data.Length == 0) throw new CryptographicException("PKCS7 填充无效（数据为空）");
                int n = data[^1];
                if (n < 1 || n > 16 || n > data.Length) throw new CryptographicException("PKCS7 填充无效（密钥/IV 可能不正确）");
                for (int i = data.Length - n; i < data.Length; i++)
                    if (data[i] != n) throw new CryptographicException("PKCS7 填充无效（密钥/IV 可能不正确）");
                return data.Take(data.Length - n).ToArray();
            }
            case PadMode.Zero:
            {
                int len = data.Length;
                while (len > 0 && data[len - 1] == 0) len--;
                return data.Take(len).ToArray();
            }
            default:
                return (byte[])data.Clone();
        }
    }

    // ---------------- XOR / BCD ----------------

    public static byte[] Xor(byte[] data, byte[] key)
    {
        if (key.Length == 0) throw new ArgumentException("XOR 密钥不能为空");
        var r = new byte[data.Length];
        for (int i = 0; i < data.Length; i++) r[i] = (byte)(data[i] ^ key[i % key.Length]);
        return r;
    }

    /// <summary>所有字节异或成一个字节（BCC 校验）。</summary>
    public static byte XorAll(byte[] data) { byte x = 0; foreach (var b in data) x ^= b; return x; }

    /// <summary>十进制数字串 → 压缩 BCD（奇数位时左侧补 0）。忽略空格。</summary>
    public static byte[] BcdEncode(string digits)
    {
        var s = new string((digits ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (s.Length == 0) return Array.Empty<byte>();
        if (s.Any(c => c < '0' || c > '9')) throw new FormatException("BCD 编码只接受数字 0~9");
        if (s.Length % 2 != 0) s = "0" + s;
        var r = new byte[s.Length / 2];
        for (int i = 0; i < r.Length; i++) r[i] = (byte)(((s[2 * i] - '0') << 4) | (s[2 * i + 1] - '0'));
        return r;
    }

    /// <summary>压缩 BCD → 十进制数字串；某半字节大于 9 时报错。</summary>
    public static string BcdDecode(byte[] data)
    {
        var sb = new StringBuilder(data.Length * 2);
        foreach (var b in data)
        {
            int hi = b >> 4, lo = b & 0xF;
            if (hi > 9 || lo > 9) throw new FormatException($"0x{b:X2} 不是有效的 BCD 字节");
            sb.Append((char)('0' + hi)).Append((char)('0' + lo));
        }
        return sb.ToString();
    }
}
