using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace SerialTool.Features;

/// <summary>多通道数值曲线：滚动显示最近 WindowSize 行，Y 轴自动缩放，带图例。</summary>
public sealed class CurveElement : FrameworkElement
{
    private static readonly string[] LightPalette =
        { "#2563EB", "#DC2626", "#16A34A", "#D97706", "#7C3AED", "#0891B2", "#DB2777", "#4D7C0F",
          "#9333EA", "#EA580C", "#0F766E", "#BE123C", "#1D4ED8", "#65A30D", "#C026D3", "#475569" };
    private static readonly string[] DarkPalette =
        { "#60A5FA", "#F87171", "#4ADE80", "#FBBF24", "#A78BFA", "#22D3EE", "#F472B6", "#A3E635",
          "#C084FC", "#FB923C", "#2DD4BF", "#FB7185", "#93C5FD", "#BEF264", "#E879F9", "#CBD5E1" };

    private static readonly Typeface Face = new("Consolas");
    private readonly CurveData _data;
    private int _penVersion = -1;
    private Pen[] _pens = Array.Empty<Pen>();
    private Brush[] _brushes = Array.Empty<Brush>();
    private Pen _grid = null!, _axis = null!;
    private Brush _bg = null!, _text = null!;

    public CurveElement(CurveData data)
    {
        _data = data;
        SnapsToDevicePixels = true;
        ClipToBounds = true;
    }

    public int WindowSize { get; set; } = 500;

    public static Color ChannelColor(int i) =>
        (Color)ColorConverter.ConvertFromString((ThemeManager.IsDark ? DarkPalette : LightPalette)[i % LightPalette.Length]);

    private void EnsurePens()
    {
        if (_penVersion == ThemeManager.Version) return;
        _penVersion = ThemeManager.Version;
        _pens = new Pen[LightPalette.Length];
        _brushes = new Brush[LightPalette.Length];
        for (int i = 0; i < _pens.Length; i++)
        {
            var b = new SolidColorBrush(ChannelColor(i));
            b.Freeze();
            _brushes[i] = b;
            var p = new Pen(b, 1.4);
            p.Freeze();
            _pens[i] = p;
        }
        _grid = MakePen(ThemeManager.GetColor("WaveGrid"), 0.8);
        _axis = MakePen(ThemeManager.GetColor("WaveAxis"), 1.0);
        _bg = ThemeManager.Get("WaveBg");
        _text = ThemeManager.Get("WaveText");
    }

    private static Pen MakePen(Color c, double t)
    {
        var p = new Pen(new SolidColorBrush(c), t);
        p.Freeze();
        return p;
    }

    private FormattedText Text(string s, Brush b, double size = 11) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, size, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 40 || h < 40) return;
        EnsurePens();
        dc.DrawRectangle(_bg, null, new Rect(0, 0, w, h));

        var rows = _data.Rows;
        int channels = _data.Channels.Count;
        int n = Math.Min(rows.Count, Math.Max(2, WindowSize));
        int first = rows.Count - n;

        const double left = 64, right = 10, top = 30, bottom = 20;
        double pw = w - left - right, ph = h - top - bottom;
        if (pw < 10 || ph < 10) return;

        if (channels == 0 || n == 0)
        {
            dc.DrawText(Text("等待数据：接收到形如 “temp=23.5,hum=40” 或 “1.2,3.4,5” 的文本行后自动生成曲线", _text, 12), new Point(left, top));
            return;
        }

        double min = double.MaxValue, max = double.MinValue;
        for (int r = first; r < rows.Count; r++)
            foreach (double v in rows[r].Values)
            {
                if (double.IsNaN(v) || double.IsInfinity(v)) continue;
                if (v < min) min = v;
                if (v > max) max = v;
            }
        if (min == double.MaxValue) { min = 0; max = 1; }
        if (max - min < 1e-9) { min -= 1; max += 1; }
        double pad = (max - min) * 0.06;
        min -= pad;
        max += pad;

        // 网格 + Y 标签
        for (int k = 0; k <= 4; k++)
        {
            double y = top + ph * k / 4.0;
            double val = max - (max - min) * k / 4.0;
            dc.DrawLine(k == 4 ? _axis : _grid, new Point(left, y), new Point(left + pw, y));
            var ft = Text(FormatNum(val), _text);
            dc.DrawText(ft, new Point(left - 6 - ft.Width, y - ft.Height / 2));
        }
        dc.DrawLine(_axis, new Point(left, top), new Point(left, top + ph));
        var xl = Text($"样本 {_data.TotalRows - n + 1} … {_data.TotalRows}（窗口 {WindowSize}）" + (_data.Paused ? "  [暂停]" : ""), _text);
        dc.DrawText(xl, new Point(left, top + ph + 3));

        int stride = Math.Max(1, (int)Math.Ceiling(n / (pw * 2)));
        for (int c = 0; c < channels; c++)
        {
            var geo = new StreamGeometry();
            bool any = false;
            using (var ctx = geo.Open())
            {
                bool open = false;
                for (int i = 0; i < n; i += stride)
                {
                    double v = CurveData.ValueAt(rows[first + i], c);
                    if (double.IsNaN(v) || double.IsInfinity(v)) { open = false; continue; }
                    double x = left + (n <= 1 ? 0 : i * pw / (n - 1));
                    double y = top + (max - v) / (max - min) * ph;
                    if (!open) { ctx.BeginFigure(new Point(x, y), false, false); open = true; }
                    else ctx.LineTo(new Point(x, y), true, false);
                    any = true;
                }
            }
            if (!any) continue;
            geo.Freeze();
            dc.DrawGeometry(null, _pens[c % _pens.Length], geo);
        }

        // 图例（名称 + 最新值）
        double lx = left;
        var last = rows[rows.Count - 1];
        for (int c = 0; c < channels; c++)
        {
            double v = double.NaN;
            for (int r = rows.Count - 1; r >= first && double.IsNaN(v); r--) v = CurveData.ValueAt(rows[r], c);
            var ft = Text($"{_data.Channels[c]} = {(double.IsNaN(v) ? "—" : FormatNum(v))}", _brushes[c % _brushes.Length], 12);
            if (lx + ft.Width + 20 > w) break;
            dc.DrawRectangle(_brushes[c % _brushes.Length], null, new Rect(lx, 11, 10, 4));
            dc.DrawText(ft, new Point(lx + 14, 5));
            lx += ft.Width + 30;
        }
        _ = last;
    }

    private static string FormatNum(double v)
    {
        double a = Math.Abs(v);
        if (a != 0 && (a >= 1e6 || a < 1e-3)) return v.ToString("0.###E+0", CultureInfo.InvariantCulture);
        return v.ToString(a >= 100 ? "0.#" : "0.###", CultureInfo.InvariantCulture);
    }
}
