using System.Text;

namespace SerialTool;

/// <summary>
/// 把收发数据块格式化为显示文本（只在 UI 线程使用）。
/// 开启“时间戳”或“显示发送”时启用分帧：方向改变、间隔超过分帧时间、或遇到换行时另起一行并加前缀。
/// 文本模式使用有状态的 Decoder，跨数据块的多字节字符（UTF-8/GBK）也能正确拼接。
/// </summary>
public sealed class DisplayFormatter
{
    private const string HexDigits = "0123456789ABCDEF";

    private Encoding _encoding = new UTF8Encoding(false);
    private Decoder _rxDecoder;
    private Decoder _txDecoder;
    private char[] _charBuf = new char[8192];
    private bool _atLineStart = true;
    private bool? _lastIsTx;
    private System.DateTime _lastTime;

    public DisplayFormatter()
    {
        _rxDecoder = _encoding.GetDecoder();
        _txDecoder = _encoding.GetDecoder();
    }

    public bool Hex { get; set; }
    public bool Timestamp { get; set; }
    public bool ShowTx { get; set; }
    public int FrameGapMs { get; set; } = 20;

    public Encoding Encoding
    {
        get => _encoding;
        set
        {
            _encoding = value;
            ResetDecoders();
        }
    }

    public void ResetDecoders()
    {
        _rxDecoder = _encoding.GetDecoder();
        _txDecoder = _encoding.GetDecoder();
    }

    /// <summary>清屏后调用：下一块数据从新行开始。</summary>
    public void ResetLine()
    {
        _atLineStart = true;
        _lastIsTx = null;
    }

    /// <summary>如果当前不在行首则换行。</summary>
    public void BreakLine(StringBuilder sb)
    {
        if (_atLineStart) return;
        sb.Append("\r\n");
        _atLineStart = true;
    }

    public void Format(DataChunk c, StringBuilder sb)
    {
        if (c.IsTx && !ShowTx) return;

        bool framing = Timestamp || ShowTx;
        if (framing)
        {
            bool newFrame = _lastIsTx != c.IsTx || (c.Time - _lastTime).TotalMilliseconds > FrameGapMs;
            if (newFrame) BreakLine(sb);
        }
        _lastIsTx = c.IsTx;
        _lastTime = c.Time;

        if (Hex)
        {
            if (c.Data.Length == 0) return;
            if (framing && _atLineStart) AppendPrefix(sb, c);
            foreach (byte b in c.Data)
                sb.Append(HexDigits[b >> 4]).Append(HexDigits[b & 0xF]).Append(' ');
            _atLineStart = false;
            return;
        }

        if (c.IsTx && c.MarkLength > 0 && c.MarkStart >= 0 && c.MarkStart + c.MarkLength <= c.Data.Length)
        {
            // 自动追加的校验字节不当文本解码，而是以 [C5 CD] 形式显示
            AppendDecoded(c, 0, c.MarkStart, sb, framing);
            if (framing && _atLineStart) AppendPrefix(sb, c);
            sb.Append('[');
            for (int i = 0; i < c.MarkLength; i++)
            {
                if (i > 0) sb.Append(' ');
                byte b = c.Data[c.MarkStart + i];
                sb.Append(HexDigits[b >> 4]).Append(HexDigits[b & 0xF]);
            }
            sb.Append(']');
            _atLineStart = false;
            int rest = c.MarkStart + c.MarkLength;
            AppendDecoded(c, rest, c.Data.Length - rest, sb, framing);
            return;
        }
        AppendDecoded(c, 0, c.Data.Length, sb, framing);
    }

    private void AppendDecoded(DataChunk c, int offset, int count, StringBuilder sb, bool framing)
    {
        if (count <= 0) return;
        Decoder dec = c.IsTx ? _txDecoder : _rxDecoder;
        int need = dec.GetCharCount(c.Data, offset, count, false);
        if (_charBuf.Length < need) _charBuf = new char[System.Math.Max(need, _charBuf.Length * 2)];
        int got = dec.GetChars(c.Data, offset, count, _charBuf, 0, false);

        for (int i = 0; i < got; i++)
        {
            char ch = _charBuf[i];
            if (framing && _atLineStart) AppendPrefix(sb, c);

            if (ch == '\n' || ch == '\r' || ch == '\t')
                sb.Append(ch);
            else if (ch < 0x20 || ch == 0x7F)
                sb.Append('·'); // 不可见控制字符
            else
                sb.Append(ch);

            _atLineStart = ch == '\n';
        }
    }

    private void AppendPrefix(StringBuilder sb, DataChunk c)
    {
        if (Timestamp) sb.Append('[').Append(c.Time.ToString("HH:mm:ss.fff")).Append("] ");
        if (ShowTx) sb.Append(c.IsTx ? "发→ " : "收← ");
    }
}
