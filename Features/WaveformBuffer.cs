using System;
using System.Windows;
using System.Windows.Media;

namespace SerialTool.Features;

/// <summary>
/// 滚动字节值条带图（Y=0..255，X=样本）。纯 WPF OnRender，无第三方依赖。
/// </summary>
public sealed class WaveformBuffer
{
    private const int MaxCap = 8192;
    private readonly byte[] _rx = new byte[MaxCap];
    private readonly byte[] _tx = new byte[MaxCap];
    private int _rxHead, _rxCount;
    private int _txHead, _txCount;
    private int _window = 1024;

    public int WindowLength
    {
        get => _window;
        set => _window = Math.Clamp(value, 64, MaxCap);
    }

    public int RxCount => Math.Min(_rxCount, _window);
    public int TxCount => Math.Min(_txCount, _window);
    public bool ShowTx { get; set; } = true;
    public bool Paused { get; set; }

    public void Clear()
    {
        _rxHead = _rxCount = 0;
        _txHead = _txCount = 0;
    }

    public void Push(DataChunk c)
    {
        if (Paused || c.Data.Length == 0) return;
        if (c.IsTx)
        {
            if (!ShowTx) return;
            foreach (byte b in c.Data) PushOne(_tx, ref _txHead, ref _txCount, b);
        }
        else
        {
            foreach (byte b in c.Data) PushOne(_rx, ref _rxHead, ref _rxCount, b);
        }
    }

    private static void PushOne(byte[] ring, ref int head, ref int count, byte b)
    {
        ring[head] = b;
        head = (head + 1) % ring.Length;
        if (count < ring.Length) count++;
    }

    public int CopyRx(Span<byte> dst) => CopyRing(_rx, _rxHead, _rxCount, dst, _window);
    public int CopyTx(Span<byte> dst) => CopyRing(_tx, _txHead, _txCount, dst, _window);

    private static int CopyRing(byte[] ring, int head, int count, Span<byte> dst, int window)
    {
        int n = Math.Min(Math.Min(count, window), dst.Length);
        int start = (head - n + ring.Length * 4) % ring.Length;
        for (int i = 0; i < n; i++)
            dst[i] = ring[(start + i) % ring.Length];
        return n;
    }
}

public sealed class WaveformElement : FrameworkElement
{
    private readonly WaveformBuffer _buf;
    private readonly byte[] _scratchRx = new byte[8192];
    private readonly byte[] _scratchTx = new byte[8192];

    private static readonly Pen RxPen = FreezePen(Color.FromRgb(0x60, 0xA5, 0xFA), 1.2);
    private static readonly Pen TxPen = FreezePen(Color.FromRgb(0x4A, 0xDE, 0x80), 1.0);
    private static readonly Pen GridPen = FreezePen(Color.FromRgb(0x37, 0x41, 0x51), 0.8);
    private static readonly Pen AxisPen = FreezePen(Color.FromRgb(0x6B, 0x72, 0x80), 1.0);
    private static readonly Typeface LabelFace = new("Consolas");

    public WaveformElement(WaveformBuffer buffer)
    {
        _buf = buffer;
        SnapsToDevicePixels = true;
    }

    private static Pen FreezePen(Color c, double thickness)
    {
        var p = new Pen(new SolidColorBrush(c), thickness);
        p.Freeze();
        return p;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w < 10 || h < 10) return;

        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27)), null, new Rect(0, 0, w, h));

        for (int v = 0; v <= 255; v += 64)
        {
            double y = h - 1 - (v / 255.0) * (h - 2);
            dc.DrawLine(v == 0 || v == 128 || v == 255 ? AxisPen : GridPen, new Point(0, y), new Point(w, y));
        }

        int rxN = _buf.CopyRx(_scratchRx);
        if (rxN > 1) DrawSeries(dc, _scratchRx.AsSpan(0, rxN), w, h, RxPen);

        if (_buf.ShowTx)
        {
            int txN = _buf.CopyTx(_scratchTx);
            if (txN > 1) DrawSeries(dc, _scratchTx.AsSpan(0, txN), w, h, TxPen);
        }

        var ft = new FormattedText(
            $"RX窗口={_buf.RxCount}/{_buf.WindowLength}" + (_buf.ShowTx ? $"  TX={_buf.TxCount}" : "") + (_buf.Paused ? "  [暂停]" : "") + "  蓝=RX 绿=TX",
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            LabelFace, 11, Brushes.LightGray,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, new Point(6, 4));
    }

    private static void DrawSeries(DrawingContext dc, Span<byte> data, double w, double h, Pen pen)
    {
        int n = data.Length;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            double y0 = h - 1 - (data[0] / 255.0) * (h - 2);
            ctx.BeginFigure(new Point(0, y0), false, false);
            for (int i = 1; i < n; i++)
            {
                double x = i * (w - 1) / Math.Max(1, n - 1);
                double y = h - 1 - (data[i] / 255.0) * (h - 2);
                ctx.LineTo(new Point(x, y), true, false);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }
}
