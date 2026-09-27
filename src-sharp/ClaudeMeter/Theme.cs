using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace ClaudeMeter;

/// <summary>
/// Light "Claude brew" palette plus the drawing helpers shared by the widget
/// and the tooltip (text at a baseline, rounded bars, the Claude logo, glyphs).
/// Coordinates are WPF DIPs, which match the Qt logical pixels of v0.1.x.
/// </summary>
public static class Theme
{
    public static readonly Color BrandCoral = Color.FromRgb(0xD9, 0x77, 0x57);
    public static readonly Color BrandCoralDark = Color.FromRgb(0xB8, 0x58, 0x38);
    public static readonly Color SurfaceBg = Color.FromArgb(248, 0xFA, 0xF9, 0xF5);
    public static readonly Color Border = Color.FromRgb(0xE5, 0xE2, 0xDA);
    public static readonly Color BorderStrong = Color.FromRgb(0xC9, 0xC5, 0xBC);
    public static readonly Color TextPrimary = Color.FromRgb(0x1F, 0x1F, 0x1F);
    public static readonly Color TextSecondary = Color.FromRgb(0x5C, 0x5A, 0x56);
    public static readonly Color TextTertiary = Color.FromRgb(0x8A, 0x87, 0x80);
    public static readonly Color Track = Color.FromRgb(0xEC, 0xE9, 0xE1);
    public static readonly Color Blue = Color.FromRgb(0x25, 0x63, 0xEB);
    public static readonly Color Orange = Color.FromRgb(0xEA, 0x58, 0x0C);
    public static readonly Color Red = Color.FromRgb(0xDC, 0x26, 0x26);

    /// <summary>Status ramp matching the claude.ai usage page: blue &lt;50%, orange &lt;80%, red.</summary>
    public static Color UtilColor(double u) => u < 0.50 ? Blue : u < 0.80 ? Orange : Red;

    public static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    // ponytail: linear blend toward white approximates Qt's QColor.lighter(132) (HSV-based); close enough for a sheen.
    static Color Lighter(Color c) => Color.FromRgb(
        (byte)(c.R + (255 - c.R) * 0.3), (byte)(c.G + (255 - c.G) * 0.3), (byte)(c.B + (255 - c.B) * 0.3));

    static readonly FontFamily Segoe = new("Segoe UI");

    /// <summary>Draw text with its baseline at <paramref name="baseline"/> (Qt drawText semantics). Size in points.</summary>
    public static void Text(DrawingContext dc, Visual v, string text, double pt, Color color, double x, double baseline,
        FontWeight? weight = null, bool alignRight = false)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(Segoe, FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal),
            pt * 96.0 / 72.0, Brush(color), VisualTreeHelper.GetDpi(v).PixelsPerDip);
        dc.DrawText(ft, new Point(alignRight ? x - ft.WidthIncludingTrailingWhitespace : x, baseline - ft.Baseline));
    }

    /// <summary>Rounded track + fill. <paramref name="gradient"/> gives the "fleet look" left-to-right sheen.</summary>
    public static void Bar(DrawingContext dc, double x, double y, double w, double h, double u, Color color, bool gradient = false)
    {
        dc.DrawRoundedRectangle(Brush(Track), null, new Rect(x, y, w, h), h / 2, h / 2);
        u = Math.Clamp(u, 0, 1);
        if (u <= 0) return;
        var fw = Math.Max(2, Math.Floor(w * u));
        Brush fill = gradient ? new LinearGradientBrush(Lighter(color), color, 0) : Brush(color);
        dc.DrawRoundedRectangle(fill, null, new Rect(x, y, fw, h), Math.Min(h, fw) / 2, h / 2);
    }

    // The single path of assets/claude-logo.svg (viewBox 0 0 24 24, nonzero fill).
    static readonly Geometry Logo = Freeze(Geometry.Parse("F1 M4.709 15.955l4.72-2.647.08-.23-.08-.128H9.2l-.79-.048-2.698-.073-2.339-.097-2.266-.122-.571-.121L0 11.784l.055-.352.48-.321.686.06 1.52.103 2.278.158 1.652.097 2.449.255h.389l.055-.157-.134-.098-.103-.097-2.358-1.596-2.552-1.688-1.336-.972-.724-.491-.364-.462-.158-1.008.656-.722.881.06.225.061.893.686 1.908 1.476 2.491 1.833.365.304.145-.103.019-.073-.164-.274-1.355-2.446-1.446-2.49-.644-1.032-.17-.619a2.97 2.97 0 01-.104-.729L6.283.134 6.696 0l.996.134.42.364.62 1.414 1.002 2.229 1.555 3.03.456.898.243.832.091.255h.158V9.01l.128-1.706.237-2.095.23-2.695.08-.76.376-.91.747-.492.584.28.48.685-.067.444-.286 1.851-.559 2.903-.364 1.942h.212l.243-.242.985-1.306 1.652-2.064.73-.82.85-.904.547-.431h1.033l.76 1.129-.34 1.166-1.064 1.347-.881 1.142-1.264 1.7-.79 1.36.073.11.188-.02 2.856-.606 1.543-.28 1.841-.315.833.388.091.395-.328.807-1.969.486-2.309.462-3.439.813-.042.03.049.061 1.549.146.662.036h1.622l3.02.225.79.522.474.638-.079.485-1.215.62-1.64-.389-3.829-.91-1.312-.329h-.182v.11l1.093 1.068 2.006 1.81 2.509 2.33.127.578-.322.455-.34-.049-2.205-1.657-.851-.747-1.926-1.62h-.128v.17l.444.649 2.345 3.521.122 1.08-.17.353-.608.213-.668-.122-1.374-1.925-1.415-2.167-1.143-1.943-.14.08-.674 7.254-.316.37-.729.28-.607-.461-.322-.747.322-1.476.389-1.924.315-1.53.286-1.9.17-.632-.012-.042-.14.018-1.434 1.967-2.18 2.945-1.726 1.845-.414.164-.717-.37.067-.662.401-.589 2.388-3.036 1.44-1.882.93-1.086-.006-.158h-.055L4.132 18.56l-1.13.146-.487-.456.061-.746.231-.243 1.908-1.312-.006.006z"));

    /// <summary>The Claude logo centered at (cx, cy), fitted to a size×size square.</summary>
    public static void DrawLogo(DrawingContext dc, double cx, double cy, double size)
    {
        var s = size / 24.0;
        dc.PushTransform(new MatrixTransform(s, 0, 0, s, cx - size / 2, cy - size / 2));
        dc.DrawGeometry(Brush(BrandCoral), null, Logo);
        dc.Pop();
    }

    static Pen RoundPen(Color c, double w)
    {
        var p = new Pen(Brush(c), w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        p.Freeze();
        return p;
    }

    /// <summary>Clockwise ~300° circular arrow — the "refresh" glyph.</summary>
    public static void DrawRefresh(DrawingContext dc, double cx, double cy, double size, Color color, double strokeW)
    {
        var r = size / 2;
        Point At(double deg) => new(cx + r * Math.Cos(deg * Math.PI / 180), cy - r * Math.Sin(deg * Math.PI / 180));
        const double start = 60, end = 60 - 300;
        var arc = new StreamGeometry();
        using (var g = arc.Open())
        {
            g.BeginFigure(At(start), false, false);
            g.ArcTo(At(end), new Size(r, r), 0, true, SweepDirection.Clockwise, true, true);
        }
        arc.Freeze();
        dc.DrawGeometry(null, RoundPen(color, strokeW), arc);

        // Arrow head at the end of the arc: tangent (clockwise) + inward normal.
        var e = At(end);
        var rad = end * Math.PI / 180;
        double tx = Math.Sin(rad), ty = Math.Cos(rad), nx = -Math.Cos(rad), ny = Math.Sin(rad);
        var hh = size * 0.32 * 0.5;
        var head = new StreamGeometry();
        using (var g = head.Open())
        {
            g.BeginFigure(e, true, true);
            g.LineTo(new Point(e.X + tx * hh + nx * hh, e.Y - ty * hh + ny * hh), false, false);
            g.LineTo(new Point(e.X - tx * hh + nx * hh, e.Y + ty * hh + ny * hh), false, false);
        }
        head.Freeze();
        dc.DrawGeometry(Brush(color), null, head);
    }

    /// <summary>Minimal clock face — circle plus two hands.</summary>
    public static void DrawClock(DrawingContext dc, double cx, double cy, double size, Color color, double strokeW)
    {
        var r = size / 2;
        var pen = RoundPen(color, strokeW);
        dc.DrawEllipse(null, pen, new Point(cx, cy), r, r);
        dc.DrawLine(pen, new Point(cx, cy), new Point(cx, cy - r * 0.7));
        dc.DrawLine(pen, new Point(cx, cy), new Point(cx + r * 0.55, cy + r * 0.30));
    }

    static Geometry Freeze(Geometry g)
    {
        g.Freeze();
        return g;
    }
}

/// <summary>A bare element whose content is drawn by a callback — the WPF stand-in for a Qt paintEvent.</summary>
public sealed class Surface(Action<DrawingContext> render) : FrameworkElement
{
    protected override void OnRender(DrawingContext dc) => render(dc);
}
