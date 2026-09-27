using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ClaudeMeter;

/// <summary>Win32 bits: fullscreen detection, no-activate tool windows, icon handles, Run-at-startup.</summary>
public static class Native
{
    const int GWL_STYLE = -16;
    const int GWL_EXSTYLE = -20;
    const long WS_CAPTION = 0x00C00000;
    const long WS_EX_TOOLWINDOW = 0x00000080;
    const long WS_EX_NOACTIVATE = 0x08000000;

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>
    /// True only when a real fullscreen app covers the monitor (physical pixels).
    /// Maximized windows also fill the screen but keep WS_CAPTION; fullscreen
    /// apps (games, video, presentations) drop it.
    /// </summary>
    public static bool IsForegroundFullscreen(System.Drawing.Rectangle monitor)
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero || hwnd == GetShellWindow() || hwnd == GetDesktopWindow()) return false;
        if (!GetWindowRect(hwnd, out var r)) return false;
        const int slop = 4;
        var covers = r.Left <= monitor.Left + slop && r.Top <= monitor.Top + slop
                  && r.Right >= monitor.Right - slop && r.Bottom >= monitor.Bottom - slop;
        return covers && (GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64() & WS_CAPTION) == 0;
    }

    /// <summary>Keep a window out of Alt-Tab and never let it take focus.</summary>
    public static void MakeNoActivateToolWindow(IntPtr hwnd)
    {
        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
    }
}

/// <summary>Per-user Run key (HKCU — no elevation). Same value name as v0.1.x, so autostart carries over.</summary>
public static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "ClaudeMeter";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string s && s.Length > 0;
    }

    public static bool Enable()
    {
        if (Environment.ProcessPath is not { } exe) return false;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            key.SetValue(ValueName, $"\"{exe}\"");
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }

    public static bool Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }
}
