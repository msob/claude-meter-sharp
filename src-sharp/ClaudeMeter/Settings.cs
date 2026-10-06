using System.Text.Json;

namespace ClaudeMeter;

/// <summary>
/// Persisted user settings, <c>%APPDATA%\ClaudeMeter\settings.json</c>.
/// snake_case keys — same file format as the Python v0.1.x releases.
/// </summary>
public sealed class Settings
{
    // 0 = adaptive (7 / 20 min). A positive number forces a fixed interval.
    public int RefreshSeconds { get; set; }
    public string? ManualApiKey { get; set; }
    // Explicit credentials file — the escape hatch for Claude Code running only inside WSL.
    public string? CredentialsPath { get; set; }
    // Accept any server certificate (e.g. a TLS-inspecting firewall). Insecure: the OAuth
    // token then goes to whoever presents a certificate. Problems are still reported.
    public bool IgnoreTlsErrors { get; set; }
    public string Plan { get; set; } = "unknown";  // pro | max_5x | max_20x | api_only | unknown
    public List<double> NotifyAtThresholds { get; set; } = [0.75, 0.90, 0.95];
    public bool NotificationsEnabled { get; set; } = true;
    // Only true fullscreen (no title bar) — never maximized windows.
    public bool HideWhenFullscreen { get; set; }
    public string Theme { get; set; } = "auto";
    // Windows display number (\\.\DISPLAY2 → 2); 0 or a disconnected display = primary monitor.
    public int Monitor { get; set; }
    public int PosOffsetRight { get; set; } = 220;
    public int PosOffsetBottom { get; set; } = 6;
    public double Opacity { get; set; } = 0.92;
    // Kept so settings.json round-trips; the WPF build has no Acrylic backdrop (see CLAUDE.md).
    public bool EnableGlassBackdrop { get; set; } = true;
    public int SnoozeMinutes { get; set; } = 15;

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeMeter", "settings.json");

    public static Settings Load(string? path = null)
    {
        try
        {
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path ?? DefaultPath), Json) ?? new Settings();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Settings();
        }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }
}
