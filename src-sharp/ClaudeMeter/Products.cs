namespace ClaudeMeter;

public sealed record DetectedProduct(string Key, string Label, string Location, string Detail = "");

/// <summary>
/// Which Anthropic / Claude products are installed locally — informational only.
/// The API doesn't split usage by client; they all share one account quota.
/// </summary>
public static class Products
{
    public static List<DetectedProduct> DetectAll()
    {
        var home = Environment.GetEnvironmentVariable("USERPROFILE");
        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        var roaming = Environment.GetEnvironmentVariable("APPDATA");
        var found = new List<DetectedProduct>();

        if (!string.IsNullOrEmpty(home))
        {
            var creds = Path.Combine(home, ".claude", ".credentials.json");
            if (File.Exists(creds))
                found.Add(new("claude-code-win", "Claude Code (Windows)", Path.GetDirectoryName(creds)!, "OAuth credentials present"));
        }
        if (!string.IsNullOrEmpty(local))
        {
            var install = Path.Combine(local, "Programs", "Claude");
            if (Directory.Exists(install))
                found.Add(new("claude-desktop", "Claude desktop app", install,
                    File.Exists(Path.Combine(install, "claude.exe")) ? "found" : "install dir found"));
        }
        if (!string.IsNullOrEmpty(roaming))
        {
            var cowork = Path.Combine(roaming, "Claude", "local-agent-mode-sessions");
            if (Directory.Exists(cowork))
                found.Add(new("cowork", "Cowork (Claude desktop)", Path.GetDirectoryName(cowork)!, "session data present"));
        }
        if (!string.IsNullOrEmpty(home))
        {
            var auth = Path.Combine(home, ".codex", "auth.json");
            if (File.Exists(auth))
                found.Add(new("codex-cli", "OpenAI Codex CLI", Path.GetDirectoryName(auth)!,
                    "(not a Claude product — usage tracked separately)"));
        }
        if (!string.IsNullOrEmpty(local))
        {
            // The extension ID isn't stable; this is a hint only.
            var ext = Path.Combine(local, "Google", "Chrome", "User Data", "Default", "Extensions");
            if (Directory.Exists(ext))
                found.Add(new("chrome", "Chrome (extensions dir)", ext,
                    "Claude-in-Chrome may be installed; usage shares the same quota"));
        }
        return found;
    }
}
