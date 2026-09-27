using System.Drawing.Drawing2D;
using System.Drawing.Text;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace ClaudeMeter;

/// <summary>
/// System tray icon — the percentage IS the icon: a rounded square in the
/// utilization colour with the number in white.
/// </summary>
public sealed class Tray : IDisposable
{
    readonly WF.NotifyIcon _icon = new();
    IntPtr _hIcon;

    public Tray(Action onOpen, Action onRefresh, Action onSettings, Action onQuit)
    {
        var menu = new WF.ContextMenuStrip();
        menu.Items.Add("Show widget", null, (_, _) => onOpen());
        menu.Items.Add("Refresh now", null, (_, _) => onRefresh());
        menu.Items.Add(new WF.ToolStripSeparator());
        var startup = new WF.ToolStripMenuItem("Run at startup") { CheckOnClick = true, Checked = Startup.IsEnabled() };
        startup.CheckedChanged += (_, _) =>
        {
            if (!(startup.Checked ? Startup.Enable() : Startup.Disable()))
                startup.Checked = Startup.IsEnabled();
        };
        menu.Items.Add(startup);
        menu.Items.Add("Settings…", null, (_, _) => onSettings());
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => onQuit());

        _icon.ContextMenuStrip = menu;
        _icon.MouseClick += (_, e) => { if (e.Button == WF.MouseButtons.Left) onOpen(); };
        SetState(0.0, error: false, "Claude Meter — fetching…");
        _icon.Visible = true;
    }

    public void SetState(double utilization, bool error, string tooltip)
    {
        var old = _hIcon;
        _hIcon = Render(WF.SystemInformation.SmallIconSize.Width, utilization, error);
        _icon.Icon = SD.Icon.FromHandle(_hIcon);
        if (old != IntPtr.Zero) Native.DestroyIcon(old);  // the shell copied it; free ours (GDI leak guard)
        _icon.Text = tooltip.Length <= 127 ? tooltip : tooltip[..126] + "…";  // NotifyIcon.Text limit
    }

    /// <summary>Native Win10/11 toast via the tray icon's balloon.</summary>
    public void ShowToast(string title, string body) => _icon.ShowBalloonTip(5000, title, body, WF.ToolTipIcon.None);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        if (_hIcon != IntPtr.Zero) Native.DestroyIcon(_hIcon);
    }

    static IntPtr Render(int size, double utilization, bool error)
    {
        var c = error ? Theme.BrandCoralDark : Theme.UtilColor(utilization);
        var pct = (int)Math.Round(utilization * 100, MidpointRounding.ToEven);
        var text = error ? "!" : pct >= 100 ? "99+" : pct.ToString();

        using var bmp = new SD.Bitmap(size, size);
        using (var g = SD.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var d = size * 0.44f;  // corner diameter (radius 0.22 × size)
            using var path = new GraphicsPath();
            path.AddArc(0, 0, d, d, 180, 90);
            path.AddArc(size - d - 1, 0, d, d, 270, 90);
            path.AddArc(size - d - 1, size - d - 1, d, d, 0, 90);
            path.AddArc(0, size - d - 1, d, d, 90, 90);
            path.CloseFigure();
            using var fill = new SD.SolidBrush(SD.Color.FromArgb(c.R, c.G, c.B));
            g.FillPath(fill, path);

            var pt = Math.Max(6, (int)(size * (text.Length <= 2 ? 0.48 : 0.36)));
            using var font = new SD.Font("Segoe UI", pt, SD.FontStyle.Bold, SD.GraphicsUnit.Point);
            using var fmt = new SD.StringFormat { Alignment = SD.StringAlignment.Center, LineAlignment = SD.StringAlignment.Center };
            g.DrawString(text, font, SD.Brushes.White, new SD.RectangleF(0, 0, size, size), fmt);
        }
        return bmp.GetHicon();
    }
}
