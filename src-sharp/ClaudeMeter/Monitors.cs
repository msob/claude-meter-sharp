using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using WF = System.Windows.Forms;

namespace ClaudeMeter;

/// <summary>
/// Monitor selection and window placement in physical pixels. The app is PerMonitorV2, where
/// WPF's DIP <c>Left</c>/<c>Top</c> don't map cleanly onto monitors with a different DPI.
/// </summary>
public static class Monitors
{
    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    const uint MONITOR_DEFAULTTONEAREST = 2;
    const int MDT_EFFECTIVE_DPI = 0;
    const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    /// <summary>The Windows display number — <c>\\.\DISPLAY2</c> → 2; 0 if it has none.</summary>
    public static int Number(WF.Screen s) =>
        int.TryParse(string.Concat(s.DeviceName.Reverse().TakeWhile(char.IsDigit).Reverse()), out var n) ? n : 0;

    public static IReadOnlyList<WF.Screen> All => WF.Screen.AllScreens.OrderBy(Number).ToList();

    /// <summary>Display <paramref name="number"/>; the primary monitor for 0 or a disconnected display.</summary>
    public static WF.Screen Pick(int number) =>
        number > 0 && WF.Screen.AllScreens.FirstOrDefault(s => Number(s) == number) is { } s ? s : WF.Screen.PrimaryScreen!;

    /// <summary>Physical pixels per DIP on that monitor (1.0 = 96 DPI).</summary>
    public static double Scale(WF.Screen s)
    {
        var b = s.Bounds;
        var hmon = MonitorFromPoint(new POINT { X = b.Left + b.Width / 2, Y = b.Top + b.Height / 2 }, MONITOR_DEFAULTTONEAREST);
        return GetDpiForMonitor(hmon, MDT_EFFECTIVE_DPI, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;
    }

    public static System.Drawing.Rectangle WindowRect(Window w)
    {
        Native.GetWindowRect(new WindowInteropHelper(w).Handle, out var r);
        return System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    /// <summary>
    /// Move the window's top-left to a physical point. Done twice: landing on a monitor with
    /// another DPI makes WPF resize and re-place the window (WM_DPICHANGED).
    /// </summary>
    public static void Move(Window w, double x, double y)
    {
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        for (var i = 0; i < 2; i++)
            SetWindowPos(hwnd, IntPtr.Zero, (int)Math.Round(x), (int)Math.Round(y), 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }
}
