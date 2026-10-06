using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WF = System.Windows.Forms;

namespace ClaudeMeter;

/// <summary>
/// The pinned strip above the taskbar:
/// <code>
///  ✶   Session ·····················  40%   ← data zone
///      Weekly  ·····················  18%
///  ─────────────────────────────────────    ← hairline divider
///  ⏱  Resets in 1h 35m · Sat 02:50 AM   ↻   ← footer
/// </code>
/// </summary>
public sealed class UsageWidget : Window
{
    const double W = 264, H = 92, LogoArea = 34, PadX = 14, PadY = 11, DataZoneH = 40;
    const double DivY = PadY + DataZoneH + 2;
    // Footer: two text lines (baselines Line1Y / Line2Y) spanning ~y 58–82 = 24 px.
    // The clock and refresh icons span both lines with a margin of IconMarginRatio × icon size
    // above, below, right of the clock and left of the refresh icon: 24 / (1 + 2 × 0.4) ≈ 13.3.
    const double Line1Y = DivY + 16, Line2Y = DivY + 28, FooterBlockH = 24;
    const double IconMarginRatio = 0.4;
    const double IconSize = FooterBlockH / (1 + 2 * IconMarginRatio), IconMargin = IconSize * IconMarginRatio;
    const double IconCy = DivY + 17;
    const double RefreshX = W - PadX - IconSize / 2;
    const double TextX = PadX + IconSize + IconMargin;  // text start when the clock is shown
    const double LogoCx = PadX + LogoArea / 2 - 4, LogoCy = PadY + DataZoneH / 2, LogoSize = 24;
    const long RefreshCooldownMs = 15_000, RefreshingLabelMs = 2_000;

    readonly Settings _settings;
    readonly Surface _surface;
    readonly TooltipPanel _tooltip = new();
    readonly WF.ContextMenuStrip _menu = new();
    readonly WF.ToolStripItem _snoozeItem;
    UsageSnapshot? _snap;
    string? _error;
    bool _refreshHover;
    long _lastRefreshClick = long.MinValue / 2;
    long _refreshPendingUntil;
    // User dismissed the widget; the visibility tick respects this until ManualShow().
    bool _manuallyHidden;
    DispatcherTimer? _snooze;

    public event Action? RefreshRequested;
    public event Action? SettingsRequested;
    public event Action? QuitRequested;

    public UsageWidget(Settings settings)
    {
        _settings = settings;
        Title = "Claude Meter";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Width = W;
        Height = H;
        Content = _surface = new Surface(Paint);
        SourceInitialized += (_, _) => Native.MakeNoActivateToolWindow(new WindowInteropHelper(this).Handle);

        MouseLeave += (_, _) =>
        {
            SetRefreshHover(false);
            After(120, HideTooltipUnlessHovered);
        };
        _tooltip.MouseLeave += (_, _) => After(120, HideTooltipUnlessHovered);
        MouseMove += (_, e) =>
        {
            var p = e.GetPosition(this);
            SetRefreshHover(RefreshHit(p) && !InCooldown);
            Cursor = _refreshHover || LogoHit(p) ? Cursors.Hand : null;
        };
        MouseLeftButtonDown += (_, e) =>
        {
            var p = e.GetPosition(this);
            if (e.ClickCount == 1) OnLeftClick(p);
            // Double-click toggles the detail panel — except on the logo / refresh button.
            else if (e.ClickCount == 2 && !LogoHit(p) && !RefreshHit(p))
            {
                if (_tooltip.IsVisible) _tooltip.Hide();
                else _tooltip.ShowBeside(this);
            }
        };
        MouseRightButtonUp += (_, _) => { _tooltip.Hide(); _menu.Show(WF.Cursor.Position); };

        _menu.Items.Add("Hide widget", null, (_, _) => ManualHide());
        _snoozeItem = _menu.Items.Add("", null, (_, _) => ManualSnooze(Math.Max(1, _settings.SnoozeMinutes)));
        _menu.Items.Add("Refresh now", null, (_, _) => RefreshRequested?.Invoke());
        _menu.Items.Add(new WF.ToolStripSeparator());
        _menu.Items.Add("Settings…", null, (_, _) => SettingsRequested?.Invoke());
        _menu.Items.Add(new WF.ToolStripSeparator());
        _menu.Items.Add("Quit Claude Meter", null, (_, _) => QuitRequested?.Invoke());
        _menu.Opening += (_, _) => _snoozeItem.Text = $"Hide for {Math.Max(1, _settings.SnoozeMinutes)} min";

        Every(TimeSpan.FromSeconds(10), _surface.InvalidateVisual);  // countdown + "Updated … ago"
        Every(TimeSpan.FromSeconds(1), UpdateVisibility);
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.WorkArea)) Reposition();
        };
        // Monitors plugged / unplugged / rearranged — re-place on the chosen one (or the primary).
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(Reposition);
        ApplyVisualSettings();
        Reposition();
    }

    public void UpdateData(UsageSnapshot? snap, List<Bucket> buckets, List<DetectedProduct> products)
    {
        _snap = snap;
        _error = snap is { Ok: false } ? snap.Error : null;
        _refreshPendingUntil = 0;  // new data — clear "Refreshing…"
        _tooltip.UpdateData(snap, buckets, products);
        _surface.InvalidateVisual();
    }

    public void Reposition()
    {
        var screen = Monitors.Pick(_settings.Monitor);
        var wa = screen.WorkingArea;
        var scale = Monitors.Scale(screen);
        Monitors.Move(this,
            Math.Max(wa.Left, wa.Right - (W + _settings.PosOffsetRight) * scale),
            Math.Max(wa.Top, wa.Bottom - (H + _settings.PosOffsetBottom) * scale));
    }

    public void ApplyVisualSettings() => Opacity = _tooltip.Opacity = Math.Clamp(_settings.Opacity, 0.30, 1.0);

    public void Toggle()
    {
        if (IsVisible) ManualHide();
        else ManualShow();
    }

    public void ManualHide()
    {
        _manuallyHidden = true;
        _tooltip.Hide();
        Hide();
    }

    public void ManualShow()
    {
        _manuallyHidden = false;
        _snooze?.Stop();
        Show();
        Reposition();
    }

    void ManualSnooze(int minutes)
    {
        ManualHide();
        _snooze?.Stop();
        _snooze = new DispatcherTimer { Interval = TimeSpan.FromMinutes(minutes) };
        _snooze.Tick += (_, _) => ManualShow();
        _snooze.Start();
    }

    void UpdateVisibility()
    {
        if (_manuallyHidden) return;
        var fullscreen = _settings.HideWhenFullscreen
            && Native.IsForegroundFullscreen(WF.Screen.FromHandle(new WindowInteropHelper(this).Handle).Bounds);
        if (fullscreen && IsVisible)
        {
            Hide();
            _tooltip.Hide();
        }
        else if (!fullscreen && !IsVisible)
        {
            Show();
        }
    }

    void HideTooltipUnlessHovered()
    {
        if (!IsMouseOver && !_tooltip.IsMouseOver) _tooltip.Hide();
    }

    // ------------------------------------------------------------------
    // Refresh button
    // ------------------------------------------------------------------

    static long Now => Environment.TickCount64;
    bool InCooldown => Now - _lastRefreshClick < RefreshCooldownMs;
    bool Refreshing => Now < _refreshPendingUntil;

    // Hit target slightly larger than the icon.
    static bool RefreshHit(Point p) => Math.Pow(p.X - RefreshX, 2) + Math.Pow(p.Y - IconCy, 2) <= Math.Pow(IconSize / 2 + 3, 2);

    void SetRefreshHover(bool hover)
    {
        if (hover == _refreshHover) return;
        _refreshHover = hover;
        Cursor = hover ? Cursors.Hand : null;
        _surface.InvalidateVisual();
    }

    static bool LogoHit(Point p) => Math.Pow(p.X - LogoCx, 2) + Math.Pow(p.Y - LogoCy, 2) <= Math.Pow(LogoSize / 2 + 3, 2);

    void OnLeftClick(Point p)
    {
        if (LogoHit(p))
        {
            _tooltip.Hide();
            ClaudeDesktop.Open();
            return;
        }
        if (!RefreshHit(p) || InCooldown) return;
        _lastRefreshClick = Now;
        _refreshPendingUntil = Now + RefreshingLabelMs;
        RefreshRequested?.Invoke();
        // Drop the "Refreshing…" label even if no snapshot arrives in time.
        After((int)RefreshingLabelMs + 50, _surface.InvalidateVisual);
        _surface.InvalidateVisual();
    }

    // ------------------------------------------------------------------
    // Painting
    // ------------------------------------------------------------------

    void Paint(DrawingContext dc)
    {
        var borderPen = new Pen(Theme.Brush(Theme.Border), 1);
        dc.DrawRoundedRectangle(Theme.Brush(Theme.SurfaceBg), borderPen, new Rect(0.5, 0.5, W - 1, H - 1), 12, 12);
        Theme.DrawLogo(dc, LogoCx, LogoCy, LogoSize);

        const double barX = PadX + LogoArea, barW = W - barX - PadX, barH = 6;
        const double topY = PadY + 10, botY = topY + barH + 6 + 10;

        var snap = _snap;
        var hasData = snap is { Quotas.Count: > 0 };
        if (_error is not null && !hasData)
        {
            Theme.Bar(dc, barX, topY, barW, barH, 0, Theme.BrandCoralDark);
            Theme.Bar(dc, barX, botY, barW, barH, 0, Theme.BrandCoralDark);
            Theme.Text(dc, this, ShortError(_error), 7, Theme.BrandCoralDark, barX, H - PadY - 2);
            return;
        }

        Quota? primary = null, secondary = null;
        if (hasData)
        {
            primary = snap!.ByKey("five_hour") ?? snap.Quotas[0];
            secondary = snap.ByKey("seven_day") ?? snap.Quotas.Where(q => q != primary).MaxBy(q => q.Utilization);
        }
        DrawRow(dc, barX, topY, barW, barH, primary, "Session");
        DrawRow(dc, barX, botY, barW, barH, secondary, "Weekly");

        dc.DrawLine(borderPen, new Point(PadX, DivY + 0.5), new Point(W - PadX, DivY + 0.5));

        // Stale = showing last-good data after a failed refresh (the controller
        // injects the error into a copy of it). Auth-expired is the fixable kind.
        var refreshing = Refreshing;
        var stale = hasData && !snap!.Ok && snap.Error is not null;
        var authExpired = stale && snap!.AuthFailed;

        if (_refreshHover && !refreshing)
            dc.DrawEllipse(Theme.Brush(Theme.Border), null, new Point(RefreshX, IconCy), IconSize / 2 + 2, IconSize / 2 + 2);
        var iconColor = InCooldown && !refreshing
            ? Color.FromArgb(130, Theme.TextTertiary.R, Theme.TextTertiary.G, Theme.TextTertiary.B)
            : Theme.TextSecondary;
        Theme.DrawRefresh(dc, RefreshX, IconCy, IconSize, iconColor, 1.7);

        if (stale)
            dc.DrawEllipse(Theme.Brush(authExpired ? Theme.Red : Theme.Orange), null,
                new Point(RefreshX - IconSize / 2 - IconMargin - 3, IconCy), 3, 3);  // just outside the margin

        var (text, color, clock) =
            refreshing ? ("Refreshing…", Theme.TextSecondary, false)
            : authExpired ? ("Session expired — run `claude` to refresh", Theme.Red, false)
            : stale ? ("Couldn't refresh · showing last update", Theme.TextSecondary, false)
            : (Countdown(snap, DateTimeOffset.UtcNow), Theme.TextSecondary, true);
        if (clock) Theme.DrawClock(dc, PadX + 8 + IconSize / 2, IconCy, IconSize, Theme.TextTertiary, 1.5);
        var textX = clock ? TextX : PadX;  // the long status texts need the full width
        Theme.Text(dc, this, text, 8, color, textX + 15, Line1Y);
        // FetchedAt of a stale snapshot is the last *good* fetch — exactly the age worth showing.
        if (hasData)
            Theme.Text(dc, this, $"Updated {TooltipPanel.HumanizeAge((DateTimeOffset.UtcNow - snap!.FetchedAt).TotalSeconds)} ago",
                7, Theme.TextTertiary, textX + 15, Line2Y);
    }

    void DrawRow(DrawingContext dc, double x, double y, double w, double h, Quota? q, string label)
    {
        Theme.Text(dc, this, label, 8, Theme.TextPrimary, x, y - 2, FontWeights.SemiBold);
        if (q is null)
        {
            Theme.Bar(dc, x, y, w, h, 0, Theme.Blue);
            Theme.Text(dc, this, "—", 8, Theme.TextTertiary, x + w, y - 2, alignRight: true);
            return;
        }
        var color = Theme.UtilColor(q.Utilization);
        Theme.Bar(dc, x, y, w, h, q.Utilization, color, gradient: true);
        Theme.Text(dc, this, $"{Math.Round(q.Percent, MidpointRounding.ToEven):0}%", 8, color, x + w, y - 2, alignRight: true);
    }

    /// <summary>"Resets in 4h 48m · Sat 02:50 AM" for the most imminent reset.</summary>
    internal static string Countdown(UsageSnapshot? snap, DateTimeOffset now)
    {
        var imminent = snap?.Quotas.Where(q => q.ResetsAt is not null).MinBy(q => q.ResetsAt);
        if (imminent?.ResetsAt is not { } r) return "—";
        if (r < now) return "Resetting…";
        return $"Resets in {TooltipPanel.Relative(r - now)} · {r.ToLocalTime().ToString("ddd hh:mm tt", CultureInfo.InvariantCulture)}";
    }

    internal static string ShortError(string err) =>
        err.StartsWith("TLS") ? "TLS/SSL error"
        : err.Contains("Rate-limited") ? "API rate-limited"
        : err.Contains("Auth") ? "Auth — re-login"
        : err.Contains("Network") ? "Network error"
        : err.Length <= 18 ? err : err[..18];

    void Every(TimeSpan interval, Action action)
    {
        var t = new DispatcherTimer(interval, DispatcherPriority.Normal, (_, _) => action(), Dispatcher);
        t.Start();
    }

    static async void After(int ms, Action action)
    {
        await Task.Delay(ms);  // resumes on the UI thread
        action();
    }
}
