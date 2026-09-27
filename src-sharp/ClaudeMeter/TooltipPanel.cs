using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClaudeMeter;

/// <summary>Rich hover panel — every quota row, extras, detected products, 14-day sparkline.</summary>
public sealed class TooltipPanel : Window
{
    const double W = 400, Pad = 18, RowH = 48, RowHCompact = 24, HeaderH = 46;

    readonly Surface _surface;
    UsageSnapshot? _snap;
    List<Bucket> _buckets = [];
    List<DetectedProduct> _products = [];

    public TooltipPanel()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Width = W;
        Height = 240;
        Content = _surface = new Surface(Paint);
        SourceInitialized += (_, _) => Native.MakeNoActivateToolWindow(new WindowInteropHelper(this).Handle);

        // Keep "resets in Xm" fresh while the panel sits open.
        var tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        tick.Tick += (_, _) => _surface.InvalidateVisual();
        IsVisibleChanged += (_, _) => { if (IsVisible) { tick.Start(); _surface.InvalidateVisual(); } else tick.Stop(); };
    }

    public void UpdateData(UsageSnapshot? snap, List<Bucket> buckets, List<DetectedProduct> products)
    {
        _snap = snap;
        _buckets = buckets;
        _products = products;
        var h = HeaderH + (snap?.Quotas.Count > 0 ? RowH * snap.Quotas.Count : 40);
        if (snap?.CountQuotas.Count > 0) h += 20 + RowHCompact * snap.CountQuotas.Count;
        if (snap?.Overage is not null) h += 8 + RowHCompact;
        if (products.Count > 0) h += 26 + 18 * Math.Min(5, products.Count);
        Height = Math.Max(240, h + 60);  // + sparkline + footer
        _surface.InvalidateVisual();
    }

    /// <summary>
    /// Place the panel without overlapping the pinned widget: above the anchor,
    /// else left of the widget, else right of it — then clamp to the work area.
    /// </summary>
    public void ShowNear(Point anchor)
    {
        const double widgetHalfW = 120, gap = 12;
        var wa = SystemParameters.WorkArea;
        double h = Height, x = anchor.X - W / 2, y = anchor.Y - h - 10;
        if (y < wa.Top + 4)
        {
            x = anchor.X - widgetHalfW - W - gap;
            y = anchor.Y - h + 30;
            if (x < wa.Left + 4) x = anchor.X + widgetHalfW + gap;
        }
        Left = Math.Max(wa.Left + 4, Math.Min(x, wa.Right - W - 4));
        Top = Math.Max(wa.Top + 4, Math.Min(y, wa.Bottom - h - 4));
        Show();
    }

    void Paint(DrawingContext dc)
    {
        var h = Height;
        var w = W - 2 * Pad;
        dc.DrawRoundedRectangle(Theme.Brush(Theme.SurfaceBg), new Pen(Theme.Brush(Theme.BorderStrong), 1),
            new Rect(0.5, 0.5, W - 1, h - 1), 14, 14);

        // Header: logo + title, plan name on the right.
        Theme.DrawLogo(dc, Pad + 10, Pad + 12, 22);
        Theme.Text(dc, this, "Claude Meter", 12, Theme.TextPrimary, Pad + 28, Pad + 17, FontWeights.Bold);
        if (!string.IsNullOrEmpty(_snap?.Plan))
            Theme.Text(dc, this, _snap.Plan, 9, Theme.TextSecondary, W - Pad, Pad + 17, alignRight: true);
        dc.DrawLine(new Pen(Theme.Brush(Theme.Border), 1), new Point(Pad, Pad + 30.5), new Point(W - Pad, Pad + 30.5));

        var y = Pad + 40;
        var now = DateTimeOffset.UtcNow;
        if (_snap is null)
        {
            Theme.Text(dc, this, "Fetching usage…", 9, Theme.TextSecondary, Pad, y + 16);
            y += 40;
        }
        else if (!_snap.Ok && _snap.Quotas.Count == 0)
        {
            Theme.Text(dc, this, _snap.Error ?? "No data", 9, Theme.BrandCoralDark, Pad, y + 16);
            y += 40;
        }
        else
        {
            foreach (var q in _snap.Quotas)
            {
                var color = Theme.UtilColor(q.Utilization);
                Theme.Text(dc, this, q.Label, 9, Theme.TextPrimary, Pad, y + 12, FontWeights.Bold);
                Theme.Text(dc, this, q.Percent.ToString("0.0", CultureInfo.InvariantCulture) + "% used", 9, color, Pad + w, y + 12, alignRight: true);
                Theme.Bar(dc, Pad, y + 18, w, 8, q.Utilization, color);
                Theme.Text(dc, this, FormatReset(q.ResetsAt, now), 7, Theme.TextTertiary, Pad, y + 40);
                y += RowH;
            }
            if (_snap.CountQuotas.Count > 0)
            {
                y += 6;
                SectionLabel(dc, y, "Additional features");
                y += 14;
                foreach (var cq in _snap.CountQuotas)
                {
                    SmallRow(dc, y, w, cq.Label, $"{cq.Used} / {cq.Limit}", cq.Utilization);
                    y += RowHCompact;
                }
            }
            if (_snap.Overage is { } ov)
            {
                y += 4;
                SmallRow(dc, y, w, "Extra usage",
                    $"${ov.CurrentUsd.ToString("N2", CultureInfo.InvariantCulture)} / ${ov.BudgetUsd.ToString("N2", CultureInfo.InvariantCulture)}",
                    ov.Utilization);
                y += RowHCompact;
            }
        }

        if (_products.Count > 0)
        {
            y += 10;
            SectionLabel(dc, y, "Detected on this machine");
            y += 14;
            foreach (var p in _products.Take(5))
            {
                Theme.Text(dc, this, "•", 9, Theme.TextPrimary, Pad + 4, y + 4);
                Theme.Text(dc, this, p.Label, 9, Theme.TextPrimary, Pad + 16, y + 4);
                y += 16;
            }
        }

        var spark = History.SparklineSeries(_buckets, b => b.U7d);
        if (spark.Count > 2)
        {
            double sy = h - 38, sh = 20, step = w / (spark.Count - 1);
            var line = new StreamGeometry();
            using (var g = line.Open())
            {
                g.BeginFigure(new Point(Pad, sy + sh - Math.Clamp(spark[0], 0, 1) * sh), false, false);
                for (var i = 1; i < spark.Count; i++)
                    g.LineTo(new Point(Pad + i * step, sy + sh - Math.Clamp(spark[i], 0, 1) * sh), true, true);
            }
            dc.DrawGeometry(null, new Pen(Theme.Brush(Theme.BrandCoral), 1.3) { LineJoin = PenLineJoin.Round }, line);
        }

        var footer = _snap is null ? "Not yet fetched" : $"Updated {HumanizeAge((now - _snap.FetchedAt).TotalSeconds)} ago";
        Theme.Text(dc, this, footer, 7, Theme.TextTertiary, Pad, h - 10);
    }

    void SectionLabel(DrawingContext dc, double y, string text) =>
        Theme.Text(dc, this, text.ToUpperInvariant(), 8, Theme.TextSecondary, Pad, y, FontWeights.Bold);

    void SmallRow(DrawingContext dc, double y, double w, string label, string value, double u)
    {
        Theme.Text(dc, this, label, 9, Theme.TextPrimary, Pad, y + 12);
        Theme.Text(dc, this, value, 9, Theme.TextSecondary, Pad + w, y + 12, alignRight: true);
        Theme.Bar(dc, Pad, y + 16, w, 3, u, Theme.UtilColor(u));
    }

    // ------------------------------------------------------------------
    // Formatting helpers (shared with the widget's countdown).
    // ------------------------------------------------------------------

    internal static string Relative(TimeSpan delta)
    {
        var s = (long)delta.TotalSeconds;
        long days = s / 86400, hours = s % 86400 / 3600, mins = s % 3600 / 60;
        return days > 0 ? $"{days}d {hours}h" : hours > 0 ? $"{hours}h {mins}m" : $"{mins}m";
    }

    internal static string FormatReset(DateTimeOffset? resetAt, DateTimeOffset now)
    {
        if (resetAt is not { } r) return "resets — unknown";
        var local = r.ToLocalTime();
        return r < now
            ? $"reset at {local.ToString("HH:mm", CultureInfo.InvariantCulture)} (passed)"
            : $"resets in {Relative(r - now)} · {local.ToString("ddd HH:mm", CultureInfo.InvariantCulture)}";
    }

    internal static string HumanizeAge(double seconds)
    {
        var s = (long)seconds;
        return s < 60 ? $"{s}s" : s < 3600 ? $"{s / 60}m" : $"{s / 3600}h";
    }
}
