using System;
using System.Collections.Generic;
using System.Text;

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

/// <summary>着色列表中的一行（AT / Modbus / 高亮普通视图共用）。颜色按 Tone 随主题解析。</summary>
public sealed class ProtocolLine
{
    public string Text { get; init; } = "";
    public AtLineKind Kind { get; init; }
    public bool IsTx { get; init; }
    public DateTime Time { get; init; }
    public bool IsError { get; init; }
    public LineTone Tone { get; init; } = LineTone.Plain;
    /// <summary>关键字高亮区间（规则变化时会重算）。</summary>
    public List<HighlightSpan>? Spans { get; set; }
    /// <summary>Spans 对应的规则版本；与 HighlightStyles.Version 不同时在显示时重算（只算可见行）。</summary>
    public int SpansVersion { get; set; } = -1;
}

/// <summary>按行拆分 AT 文本并分类。</summary>
public sealed class AtLineParser
{
    private readonly StringBuilder _pending = new();
    private readonly Decoder _rxDecoder;
    private readonly Decoder _txDecoder;
    private char[] _charBuf = new char[4096];

    public AtLineParser(Encoding encoding)
    {
        _rxDecoder = encoding.GetDecoder();
        _txDecoder = encoding.GetDecoder();
    }

    public IReadOnlyList<ProtocolLine> Push(DataChunk chunk, Encoding encoding)
    {
        var lines = new List<ProtocolLine>();
        Decoder dec = chunk.IsTx ? _txDecoder : _rxDecoder; // 有状态解码，跨块多字节字符不乱码
        int need = dec.GetCharCount(chunk.Data, 0, chunk.Data.Length, false);
        if (_charBuf.Length < need) _charBuf = new char[Math.Max(need, _charBuf.Length * 2)];
        int got = dec.GetChars(chunk.Data, 0, chunk.Data.Length, _charBuf, 0, false);
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
            if (line.Length == 0) continue;
            lines.Add(Classify(line, chunk.IsTx, chunk.Time));
        }
        return lines;
    }

    public static ProtocolLine Classify(string line, bool isTx, DateTime time)
    {
        string t = line.Trim();
        var kind = AtLineKind.Plain;
        var tone = LineTone.Plain;

        if (isTx)
        {
            kind = AtLineKind.Command;
            tone = LineTone.Tx;
        }
        else if (t.StartsWith("AT", StringComparison.OrdinalIgnoreCase))
        {
            kind = AtLineKind.Command;
            tone = LineTone.Cmd;
        }
        else if (string.Equals(t, "OK", StringComparison.OrdinalIgnoreCase))
        {
            kind = AtLineKind.Ok;
            tone = LineTone.Ok;
        }
        else if (t.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(t, "BUSY", StringComparison.OrdinalIgnoreCase) ||
                 t.StartsWith("busy", StringComparison.OrdinalIgnoreCase) ||
                 t.StartsWith("+CME ERROR", StringComparison.OrdinalIgnoreCase) ||
                 t.StartsWith("+CMS ERROR", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(t, "NO CARRIER", StringComparison.OrdinalIgnoreCase))
        {
            kind = AtLineKind.Error;
            tone = LineTone.Err;
        }
        else if (t.StartsWith("+") && t.Contains(':'))
        {
            kind = AtLineKind.Urc;
            tone = LineTone.Urc;
        }
        else if (t.StartsWith("+"))
        {
            kind = AtLineKind.Info;
            tone = LineTone.Info;
        }

        string prefix = $"[{time:HH:mm:ss.fff}] {(isTx ? "发→" : "收←")} ";
        return new ProtocolLine
        {
            Text = prefix + line,
            Kind = kind,
            IsTx = isTx,
            Time = time,
            IsError = kind == AtLineKind.Error,
            Tone = tone,
        };
    }
}
