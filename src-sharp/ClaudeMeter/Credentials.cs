using System.Text.Json;

namespace ClaudeMeter;

/// <summary>A resolved credential ready to authenticate against api.anthropic.com.</summary>
public sealed record Credential(string Kind, string Token, string Source, long? ExpiresAtMs = null)
{
    // 30 s leeway — Claude Code refreshes the token itself, but a token read
    // seconds before expiry is treated as stale.
    public bool IsExpired =>
        ExpiresAtMs is long ms && ms <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 30_000;
}

/// <summary>
/// Credential discovery. Order of preference (first non-expired hit wins):
///   1. Settings → credentials_path (explicit file, e.g. a WSL path
///      <c>\\wsl.localhost\Ubuntu\home\me\.claude\.credentials.json</c> — read as a plain file)
///   2. %USERPROFILE%\.claude\.credentials.json (Claude Code on Windows)
///   3. Settings → manual_api_key
///   4. ANTHROPIC_API_KEY environment variable
/// </summary>
public static class Credentials
{
    public static Credential? Discover(string? manualApiKey = null, string? credentialsPath = null)
    {
        var all = Candidates(manualApiKey, credentialsPath).ToList();
        // Even an expired OAuth token is better than nothing — the call will
        // 401 and the UI tells the user to re-login.
        return all.FirstOrDefault(c => !c.IsExpired) ?? all.FirstOrDefault();
    }

    static IEnumerable<Credential> Candidates(string? manualApiKey, string? credentialsPath)
    {
        if (!string.IsNullOrWhiteSpace(credentialsPath)
            && ParseFile(credentialsPath.Trim(), $"File: {credentialsPath.Trim()}") is { } fromPath)
            yield return fromPath;

        var home = Environment.GetEnvironmentVariable("USERPROFILE");
        if (!string.IsNullOrEmpty(home))
        {
            var path = Path.Combine(home, ".claude", ".credentials.json");
            if (ParseFile(path, $"Windows: {path}") is { } win)
                yield return win;
        }

        if (!string.IsNullOrEmpty(manualApiKey))
            yield return new Credential("api_key", manualApiKey, "Settings (manual)");

        var env = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (!string.IsNullOrEmpty(env))
            yield return new Credential("api_key", env, "$ANTHROPIC_API_KEY");
    }

    /// <summary>Parse a Claude Code <c>.credentials.json</c>; null if missing, corrupt or tokenless.</summary>
    public static Credential? ParseFile(string path, string source)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth)
                || oauth.ValueKind != JsonValueKind.Object
                || !oauth.TryGetProperty("accessToken", out var tok)
                || tok.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(tok.GetString()))
                return null;
            long? expires = oauth.TryGetProperty("expiresAt", out var exp) && exp.ValueKind == JsonValueKind.Number && exp.TryGetInt64(out var ms) ? ms : null;
            return new Credential("oauth", tok.GetString()!, source, expires);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
