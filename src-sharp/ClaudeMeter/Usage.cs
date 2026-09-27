using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace ClaudeMeter;

/// <summary>One named percentage quota row.</summary>
public sealed record Quota(string Key, string Label, double Utilization, DateTimeOffset? ResetsAt)
{
    public double Percent => Utilization * 100.0;
}

/// <summary>A used / limit pair (e.g. daily routine runs 0 / 15).</summary>
public sealed record CountQuota(string Key, string Label, int Used, int Limit, DateTimeOffset? ResetsAt)
{
    public double Utilization => Limit <= 0 ? 0.0 : Math.Min(1.0, (double)Used / Limit);
}

/// <summary>Pay-as-you-go extra usage on top of the subscription.</summary>
public sealed record Overage(double CurrentUsd, double BudgetUsd)
{
    public double Utilization => BudgetUsd <= 0 ? 0.0 : Math.Min(1.0, CurrentUsd / BudgetUsd);
}

/// <summary>The result of one /api/oauth/usage call.</summary>
public sealed record UsageSnapshot
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public int? StatusCode { get; set; }
    public int? RetryAfterS { get; set; }
    public DateTimeOffset FetchedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? Plan { get; set; }
    public List<Quota> Quotas { get; set; } = [];
    public List<CountQuota> CountQuotas { get; set; } = [];
    public Overage? Overage { get; set; }
    public string? CredentialSource { get; set; }
    public string? CredentialKind { get; set; }

    public bool RateLimited => StatusCode == 429;
    public bool AuthFailed => StatusCode == 401;

    public Quota? ByKey(string key) => Quotas.FirstOrDefault(q => q.Key == key);
}

/// <summary>
/// Talks to <c>api.anthropic.com/api/oauth/usage</c> (the data behind claude.ai/settings/usage).
/// The endpoint rate-limits aggressively — see <see cref="Poller"/> for the schedule.
/// </summary>
public static class Usage
{
    public const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    const string AnthropicVersion = "2023-06-01";
    const string AnthropicBetaOAuth = "oauth-2025-04-20";
    const string UserAgent = "claude-code/1.0 (claude-usage-widget)";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    // "seven_day_omelette" is Anthropic's internal codename for the Claude Design quota.
    static readonly Dictionary<string, string> KnownLabels = new()
    {
        ["five_hour"] = "Current session",
        ["seven_day"] = "Weekly · All models",
        ["seven_day_sonnet"] = "Weekly · Sonnet only",
        ["seven_day_opus"] = "Weekly · Opus only",
        ["seven_day_haiku"] = "Weekly · Haiku only",
        ["seven_day_claude_design"] = "Weekly · Claude Design",
        ["seven_day_omelette"] = "Weekly · Claude Design",
        ["seven_day_skills"] = "Weekly · Skills",
        ["seven_day_agents"] = "Weekly · Agents",
    };

    // Display order — known keys first, then anything unknown in response order.
    static readonly string[] KnownOrder =
    [
        "five_hour", "seven_day", "seven_day_sonnet", "seven_day_opus", "seven_day_haiku",
        "seven_day_claude_design", "seven_day_omelette", "seven_day_skills", "seven_day_agents",
    ];

    static readonly string[] UtilKeys = ["utilization", "utilization_pct", "used_percentage"];
    static readonly string[] NonUtilSections = ["extra_usage", "daily_routine_runs", "plan"];

    /// <summary>Fetch the usage payload. Never throws — failures land in <see cref="UsageSnapshot.Error"/>.</summary>
    public static async Task<UsageSnapshot> ProbeAsync(Credential credential, CancellationToken ct = default)
    {
        var snap = new UsageSnapshot { CredentialSource = credential.Source, CredentialKind = credential.Kind };
        if (credential.Kind != "oauth")
        {
            snap.Error = "This endpoint requires an OAuth token (Claude Code credentials). "
                       + "Plain API keys can't see the per-model breakdown.";
            return snap;
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {credential.Token}");
        req.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
        req.Headers.TryAddWithoutValidation("anthropic-beta", AnthropicBetaOAuth);
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");

        string body;
        try
        {
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            snap.StatusCode = (int)resp.StatusCode;
            // Honour Retry-After even on 4xx so the poller can back off correctly.
            if (resp.Headers.RetryAfter?.Delta is TimeSpan ra)
                snap.RetryAfterS = (int)ra.TotalSeconds;
            body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            snap.Error = $"Network error: {e.GetType().Name}";
            return snap;
        }

        switch (snap.StatusCode)
        {
            case 401:
                snap.Error = "Auth failed — token may be expired. Re-login with `claude` CLI.";
                return snap;
            case 429:
                snap.Error = "Rate-limited by Anthropic. Slowing down…";
                return snap;
            case < 200 or >= 300:
                snap.Error = $"HTTP {snap.StatusCode}: {ShortBody(body)}";
                return snap;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            ParseInto(snap, doc.RootElement);
        }
        catch (JsonException)
        {
            snap.Error = "Response was not JSON";
            return snap;
        }
        snap.Ok = true;
        return snap;
    }

    // ------------------------------------------------------------------
    // Parsing — split out so tests can drive it without HTTP.
    // ------------------------------------------------------------------

    public static void ParseInto(UsageSnapshot snap, JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) return;

        // The API has been observed returning both 0–1 fractions and 0–100
        // percentages. If *any* utilization exceeds 1.5 the whole response is
        // treated as percentages — so "1%" is never misread as "100%".
        var scale = body.EnumerateObject().Any(p => FirstNumeric(p.Value, UtilKeys) > 1.5) ? 0.01 : 1.0;

        var quotas = new List<Quota>();
        var seen = new HashSet<string>();
        void Consider(string key, JsonElement value)
        {
            if (FirstNumeric(value, UtilKeys) is not double util) return;
            var resets = ParseIso(FirstString(value, "resets_at", "reset_at", "reset"));
            quotas.Add(new Quota(key, LabelFor(key), util * scale, resets));
            seen.Add(key);
        }

        foreach (var key in KnownOrder)
            if (body.TryGetProperty(key, out var v)) Consider(key, v);
        foreach (var p in body.EnumerateObject())
            if (!seen.Contains(p.Name) && !NonUtilSections.Contains(p.Name)) Consider(p.Name, p.Value);
        snap.Quotas = quotas;

        // Plan name — best-effort: a string, or an object with a name.
        foreach (var key in new[] { "plan", "subscription", "plan_name" })
        {
            if (!body.TryGetProperty(key, out var plan)) continue;
            var name = plan.ValueKind == JsonValueKind.Object ? FirstString(plan, "name", "display_name")
                     : plan.ValueKind == JsonValueKind.String ? plan.GetString() : null;
            if (!string.IsNullOrEmpty(name)) { snap.Plan = name; break; }
        }

        if (body.TryGetProperty("extra_usage", out var extra)
            && FirstNumeric(extra, "current_spending", "current_spend", "spent", "current_usd") is double cur
            && FirstNumeric(extra, "budget_limit", "budget", "limit", "budget_usd") is double bud)
            snap.Overage = new Overage(cur, bud);

        // Count rows: a used/limit pair and NO utilization (else it'd double-count).
        var counts = new List<CountQuota>();
        foreach (var p in body.EnumerateObject())
        {
            if (FirstNumeric(p.Value, "used", "count", "consumed") is not double used
                || FirstNumeric(p.Value, "limit", "cap", "total") is not double limit
                || limit <= 0
                || UtilKeys.Any(k => p.Value.TryGetProperty(k, out _)))
                continue;
            counts.Add(new CountQuota(p.Name, LabelFor(p.Name), (int)used, (int)limit,
                ParseIso(FirstString(p.Value, "resets_at", "reset_at"))));
        }
        snap.CountQuotas = counts;
    }

    static string LabelFor(string key)
    {
        if (KnownLabels.TryGetValue(key, out var label)) return label;
        // Humanise: "daily_routine_runs" → "Daily routine runs"
        var s = key.Replace('_', ' ').Trim().ToLowerInvariant();
        return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
    }

    internal static DateTimeOffset? ParseIso(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt)
            ? dt.ToUniversalTime() : null;

    static double? FirstNumeric(JsonElement obj, params string[] keys)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        foreach (var k in keys)
        {
            if (!obj.TryGetProperty(k, out var v)) continue;
            if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
            if (v.ValueKind == JsonValueKind.String
                && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                return d;
        }
        return null;
    }

    static string? FirstString(JsonElement obj, params string[] keys)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        foreach (var k in keys)
            if (obj.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(v.GetString()))
                return v.GetString();
        return null;
    }

    static string ShortBody(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var err)
                && FirstString(err, "message") is { } msg)
                return msg;
        }
        catch (JsonException) { }
        return body.Length <= 200 ? body : body[..200];
    }
}
