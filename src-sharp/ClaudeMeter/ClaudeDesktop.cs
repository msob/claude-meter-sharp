using System.ComponentModel;
using System.Diagnostics;
using System.Windows;

namespace ClaudeMeter;

/// <summary>Launches (or brings to front) the Claude desktop app.</summary>
public static class ClaudeDesktop
{
    /// <summary>
    /// Tries the <c>claude://</c> protocol the desktop app registers (works for the MSIX and
    /// the Squirrel install), then the Squirrel install's exe.
    /// </summary>
    public static void Open()
    {
        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
        string[] targets = ["claude://", Path.Combine(local, "AnthropicClaude", "claude.exe")];
        foreach (var target in targets)
        {
            if (target.EndsWith(".exe") && !File.Exists(target)) continue;
            try
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
                return;
            }
            catch (Win32Exception) { }  // no protocol handler / can't start — try the next one
        }
        MessageBox.Show("Couldn't find the Claude desktop app.\nInstall it from claude.ai/download.",
                        "Claude Meter", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
