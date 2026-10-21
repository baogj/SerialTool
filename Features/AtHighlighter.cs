using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Media;

namespace SerialTool.Features;

public enum AtLineKind
{
    Plain,
    Command,
    Ok,
    Error,
    Urc,
    Info,
}

public sealed class ProtocolLine
{
    public string Text { get; init; } = "";
    public AtLineKind Kind { get; init; }
    public bool IsTx { get; init; }
    public DateTime Time { get; init; }
    public bool IsError { get; init; } // Modbus CRC fail etc.
    public Brush Foreground { get; init; } = Brushes.LightGray;
}

/// <summary>按行拆分 AT 文本并着色分类。</summary>
public sealed class AtLineParser
{
    private readonly StringBuilder _pending = new();
    private readonly Decoder _decoder;
    private char[] _charBuf = new char[4096];

    private static readonly Brush CmdBrush = Freeze(Color.FromRgb(0x7D, 0xD3, 0xFC));   // sky
    private static readonly Brush OkBrush = Freeze(Color.FromRgb(0x4A, 0xDE, 0x80));    // green
    private static readonly Brush ErrBrush = Freeze(Color.FromRgb(0xF8, 0x71, 0x71));   // red
    private static readonly Brush UrcBrush = Freeze(Color.FromRgb(0xFC, 0xE0, 0x22));   // yellow
    private static readonly Brush InfoBrush = Freeze(Color.FromRgb(0xC4, 0xB5, 0xFD));  // violet
    private static readonly Brush PlainBrush = Freeze(Color.FromRgb(0xD4, 0xD4, 0xD4));
    private static readonly Brush TxBrush = Freeze(Color.FromRgb(0x86, 0xEF, 0xAC));

    public AtLineParser(Encoding encoding)
    {
        _decoder = encoding.GetDecoder();
    }

    public void Reset(Encoding? encoding = null)
    {
        _pending.Clear();
        if (encoding != null)
        {
            // re-create via field not possible; caller replaces instance
        }
        else
        {
            _decoder.Reset();
        }
    }

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public IReadOnlyList<ProtocolLine> Push(DataChunk chunk, Encoding encoding)
    {
        var lines = new List<ProtocolLine>();
        Decoder dec = encoding.GetDecoder(); // 简单：每块独立（AT 多为 ASCII）
        int need = encoding.GetCharCount(chunk.Data);
        if (_charBuf.Length < need) _charBuf = new char[Math.Max(need, _charBuf.Length * 2)];
        int got = encoding.GetChars(chunk.Data, 0, chunk.Data.Length, _charBuf, 0);
        _pending.Append(_charBuf, 0, got);

        while (true)
        {
            string all = _pending.ToString();
            int nl = all.IndexOfAny(new[] { '\n', '\r' });
            if (nl < 0) break;
            string line = all.Substring(0, nl);
            int skip = 1;
            if (all[nl] == '\r' && nl + 1 < all.Length && all[nl + 1] == '\n') skip = 2;
            _pending.Remove(0, nl + skip);
            if (line.Length == 0 && skip > 0) continue;
            lines.Add(Classify(line, chunk.IsTx, chunk.Time));
        }
        return lines;
    }

    /// <summary>刷新未以换行结尾的残留。</summary>
    public ProtocolLine? FlushPending(bool isTx, DateTime time)
    {
        if (_pending.Length == 0) return null;
        string line = _pending.ToString();
        _pending.Clear();
        return Classify(line, isTx, time);
    }

    public static ProtocolLine Classify(string line, bool isTx, DateTime time)
    {
        string t = line.Trim();
        var kind = AtLineKind.Plain;
        Brush brush = PlainBrush;

        if (isTx)
        {
            kind = AtLineKind.Command;
            brush = TxBrush;
        }
        else if (t.StartsWith("AT", StringComparison.OrdinalIgnoreCase))
        {
            kind = AtLineKind.Command;
            brush = CmdBrush;
        }
        else if (string.Equals(t, "OK", StringComparison.OrdinalIgnoreCase))
        {
            kind = AtLineKind.Ok;
            brush = OkBrush;
        }
        else if (t.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(t, "BUSY", StringComparison.OrdinalIgnoreCase) ||
                 t.StartsWith("+CME ERROR", StringComparison.OrdinalIgnoreCase) ||
                 t.StartsWith("+CMS ERROR", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(t, "NO CARRIER", StringComparison.OrdinalIgnoreCase))
        {
            kind = AtLineKind.Error;
            brush = ErrBrush;
        }
        else if (t.StartsWith("+") && t.Contains(':'))
        {
            kind = AtLineKind.Urc;
            brush = UrcBrush;
        }
        else if (t.StartsWith("+"))
        {
            kind = AtLineKind.Info;
            brush = InfoBrush;
        }

        string prefix = $"[{time:HH:mm:ss.fff}] {(isTx ? "发→" : "收←")} ";
        return new ProtocolLine
        {
            Text = prefix + line,
            Kind = kind,
            IsTx = isTx,
            Time = time,
            IsError = kind == AtLineKind.Error,
            Foreground = brush,
        };
    }
}
