using System.Text.Json;
using ClaudeMeter;

namespace ClaudeMeter.Tests;

sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("claude-meter-").FullName;
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose() => Directory.Delete(Path, recursive: true);
}

public class CredentialsTests
{
    [Fact]
    public void ParsesValidFile()
    {
        using var tmp = new TempDir();
        var f = tmp.File(".credentials.json");
        File.WriteAllText(f, """{"claudeAiOauth":{"accessToken":"sk-ant-oat01-test","refreshToken":"x","expiresAt":99999999999999,"scopes":["user:inference"]}}""");
        var c = Credentials.ParseFile(f, f)!;
        Assert.Equal("oauth", c.Kind);
        Assert.Equal("sk-ant-oat01-test", c.Token);
        Assert.False(c.IsExpired);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json at all")]
    [InlineData("[1,2]")]
    [InlineData("""{"claudeAiOauth":{"expiresAt":1}}""")]
    public void RejectsMissingOrCorrupt(string content)
    {
        using var tmp = new TempDir();
        var f = tmp.File(".credentials.json");
        File.WriteAllText(f, content);
        Assert.Null(Credentials.ParseFile(f, f));
    }

    [Fact]
    public void DetectsExpiredToken()
    {
        using var tmp = new TempDir();
        var f = tmp.File(".credentials.json");
        File.WriteAllText(f, """{"claudeAiOauth":{"accessToken":"sk-ant-oat01-expired","expiresAt":0}}""");
        Assert.True(Credentials.ParseFile(f, f)!.IsExpired);
    }

    [Fact]
    public void FallsBackToEnvVarAndHonoursExplicitPath()
    {
        var home = Environment.GetEnvironmentVariable("USERPROFILE");
        var key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        using var tmp = new TempDir();
        try
        {
            Environment.SetEnvironmentVariable("USERPROFILE", null);
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-ant-api01-test");
            var env = Credentials.Discover();
            Assert.Equal(("api_key", "sk-ant-api01-test"), (env!.Kind, env.Token));

            // credentials_path (the WSL escape hatch) beats every other source.
            var f = tmp.File("wsl-creds.json");
            File.WriteAllText(f, """{"claudeAiOauth":{"accessToken":"from-path","expiresAt":99999999999999}}""");
            Assert.Equal("from-path", Credentials.Discover("manual", f)!.Token);
            // A missing explicit file falls through to the next candidate.
            Assert.Equal("manual", Credentials.Discover("manual", tmp.File("nope.json"))!.Token);
        }
        finally
        {
            Environment.SetEnvironmentVariable("USERPROFILE", home);
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", key);
        }
    }

    [Fact]
    public void NoWslOrProcessCodeInTheApp()
    {
        // T02 acceptance: the app never spawns processes (the old wsl.exe path).
        var src = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ClaudeMeter"), "*.cs");
        Assert.NotEmpty(src);
        foreach (var file in src)
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("Process.Start", text);
            Assert.DoesNotContain("wsl.exe", text);
        }
    }
}

public class UsageParserTests
{
    const string Fixture = """
    {
      "plan": "Max 5x",
      "five_hour":               {"utilization": 30, "resets_at": "2026-05-14T18:30:00Z"},
      "seven_day":               {"utilization":  7, "resets_at": "2026-05-19T13:30:00Z"},
      "seven_day_sonnet":        {"utilization":  8, "resets_at": "2026-05-19T13:30:00Z"},
      "seven_day_opus":          {"utilization":  0, "resets_at": "2026-05-19T13:30:00Z"},
      "seven_day_claude_design": {"utilization":  0, "resets_at": "2026-05-19T13:30:00Z"},
      "daily_routine_runs":      {"used": 0, "limit": 15, "resets_at": "2026-05-15T00:00:00Z"},
      "extra_usage":             {"current_spending": 3.5, "budget_limit": 50.0}
    }
    """;

    static UsageSnapshot Parse(string json)
    {
        var snap = new UsageSnapshot();
        using var doc = JsonDocument.Parse(json);
        Usage.ParseInto(snap, doc.RootElement);
        return snap;
    }

    [Fact]
    public void QuotasInKnownOrderWithFriendlyLabels()
    {
        var snap = Parse(Fixture);
        Assert.Equal(["five_hour", "seven_day", "seven_day_sonnet", "seven_day_opus", "seven_day_claude_design"],
            snap.Quotas.Select(q => q.Key));
        Assert.Equal("Current session", snap.ByKey("five_hour")!.Label);
        Assert.Equal("Weekly · All models", snap.ByKey("seven_day")!.Label);
        Assert.Equal("Weekly · Sonnet only", snap.ByKey("seven_day_sonnet")!.Label);
        Assert.Equal("Weekly · Claude Design", snap.ByKey("seven_day_claude_design")!.Label);
        Assert.Equal("Weekly · Claude Design", Parse("""{"seven_day_omelette":{"utilization":5}}""").Quotas[0].Label);
    }

    [Fact]
    public void NormalizesPercentScale()
    {
        var snap = Parse(Fixture);
        Assert.Equal(0.30, snap.ByKey("five_hour")!.Utilization, 6);
        Assert.Equal(0.08, snap.ByKey("seven_day_sonnet")!.Utilization, 6);
        Assert.Equal(0.42, Parse("""{"five_hour":{"utilization":0.42}}""").ByKey("five_hour")!.Utilization);
        Assert.Equal(0.42, Parse("""{"five_hour":{"utilization":"42"}}""").ByKey("five_hour")!.Utilization, 6);
    }

    [Fact]
    public void ResetTimesAreUtc()
    {
        var r = Parse(Fixture).ByKey("five_hour")!.ResetsAt!.Value;
        Assert.Equal(TimeSpan.Zero, r.Offset);
        Assert.Equal(new DateTimeOffset(2026, 5, 14, 18, 30, 0, TimeSpan.Zero), r);
    }

    [Fact]
    public void PlanOverageAndCounts()
    {
        var snap = Parse(Fixture);
        Assert.Equal("Max 5x", snap.Plan);
        Assert.Equal(new Overage(3.5, 50.0), snap.Overage);
        var runs = Assert.Single(snap.CountQuotas);
        Assert.Equal(("daily_routine_runs", "Daily routine runs", 0, 15), (runs.Key, runs.Label, runs.Used, runs.Limit));
        Assert.Equal("Max 20x", Parse("""{"plan":{"display_name":"Max 20x"}}""").Plan);
    }

    [Fact]
    public void UnknownKeysAreHumanized()
    {
        var q = Parse("""{"seven_day_something_new":{"utilization":12,"resets_at":"2026-05-21T00:00:00Z"}}""").ByKey("seven_day_something_new")!;
        Assert.Equal("Seven day something new", q.Label);
    }

    [Fact]
    public void UnrelatedResponseYieldsNothing()
    {
        var snap = Parse("""{"some_unrelated_field":"value"}""");
        Assert.Empty(snap.Quotas);
        Assert.Empty(snap.CountQuotas);
        Assert.Null(snap.Overage);
    }
}

public class SettingsAndHistoryTests
{
    static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void ReadsPythonWrittenSettingsAndRoundTrips()
    {
        var s = Settings.Load(Fixture("settings-v0.1.4.json"));
        Assert.Equal(300, s.RefreshSeconds);
        Assert.Equal("max_5x", s.Plan);
        Assert.Equal([0.8, 0.95], s.NotifyAtThresholds);
        Assert.True(s.HideWhenFullscreen);
        Assert.Equal(6, s.PosOffsetRight);
        Assert.Equal(0.85, s.Opacity);
        Assert.Null(s.CredentialsPath);

        using var tmp = new TempDir();
        var f = tmp.File("settings.json");
        s.CredentialsPath = @"\\wsl.localhost\Ubuntu\home\me\.claude\.credentials.json";
        s.Save(f);
        Assert.Contains("\"pos_offset_right\": 6", File.ReadAllText(f));
        Assert.Equal(s.CredentialsPath, Settings.Load(f).CredentialsPath);
        Assert.Equal(15, Settings.Load(tmp.File("missing.json")).SnoozeMinutes);  // defaults
    }

    [Fact]
    public void ReadsPythonWrittenHistory()
    {
        var b = History.Load(Fixture("history-v0.1.4.json"));
        Assert.Equal(2, b.Count);  // the malformed entry is skipped, not fatal
        Assert.Equal("2026-05-14T10:00:00Z", b[0].T);
        Assert.Null(b[0].Uopus);
        Assert.Equal(0.1, b[1].Uopus);
    }

    [Fact]
    public void AppendCollapsesIntoTenMinuteBuckets()
    {
        using var tmp = new TempDir();
        var p = tmp.File("history.json");
        History.Append(0.1, 0.2, null, p, new DateTimeOffset(2026, 5, 14, 10, 0, 0, TimeSpan.Zero));
        History.Append(0.15, 0.25, 0.05, p, new DateTimeOffset(2026, 5, 14, 10, 5, 0, TimeSpan.Zero));
        History.Append(0.3, 0.4, 0.1, p, new DateTimeOffset(2026, 5, 14, 10, 20, 0, TimeSpan.Zero));
        var b = History.Load(p);
        Assert.Equal(2, b.Count);
        Assert.Equal(0.15, b[0].U5h);
        Assert.Equal(0.25, b[0].U7d);
        Assert.Equal("2026-05-14T10:20:00Z", b[1].T);
    }

    [Fact]
    public void TrimsAfterFourteenDays()
    {
        using var tmp = new TempDir();
        var p = tmp.File("history.json");
        History.Append(0.1, 0.2, null, p, new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero));
        History.Append(0.3, 0.4, null, p, new DateTimeOffset(2026, 5, 20, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(0.3, Assert.Single(History.Load(p)).U5h);
    }

    [Fact]
    public void SparklineSkipsNulls()
    {
        Bucket[] b = [new() { T = "1" }, new() { T = "2", U5h = 0.1 }, new() { T = "3", U5h = 0.2 }];
        Assert.Equal([0.1, 0.2], History.SparklineSeries(b, x => x.U5h));
        Assert.Equal([0.2], History.SparklineSeries(b, x => x.U5h, points: 1));
    }
}

public class NotifierAndPricingTests
{
    static readonly DateTimeOffset Reset = new(2026, 5, 19, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ThresholdFiresOncePerCycle()
    {
        var n = new ThresholdNotifier([0.75, 0.90, 0.95]);
        Assert.Empty(n.Check("7d", 0.5, Reset));
        Assert.Equal([0.75], n.Check("7d", 0.78, Reset));
        Assert.Empty(n.Check("7d", 0.79, Reset));
        Assert.Equal([0.90, 0.95], n.Check("7d", 0.96, Reset));
    }

    [Fact]
    public void NewCycleResetsAndWindowsAreIndependent()
    {
        var n = new ThresholdNotifier([0.75]);
        Assert.Equal([0.75], n.Check("7d", 0.8, Reset));
        Assert.Empty(n.Check("7d", 0.8, Reset));
        Assert.Equal([0.75], n.Check("7d", 0.8, Reset.AddDays(7)));
        Assert.Equal([0.75], n.Check("5h", 0.8, Reset));
    }

    [Fact]
    public void ResetDetectedOnceAfterResetTimePasses()
    {
        var n = new ThresholdNotifier([]);
        var before = Reset.AddHours(-1);
        Assert.False(n.CheckReset("5h", Reset, before));                // first sighting — nothing to compare
        Assert.False(n.CheckReset("5h", Reset.AddSeconds(3), before));  // jitter before the reset is due
        Assert.False(n.CheckReset("5h", null, before));                 // no reset time — ignored
        var after = Reset.AddMinutes(10);
        Assert.True(n.CheckReset("5h", Reset.AddHours(5), after));      // old time passed, new window
        Assert.False(n.CheckReset("5h", Reset.AddHours(5), after));     // only once
        Assert.False(n.CheckReset("7d", Reset.AddDays(7), after));      // windows are independent
    }

    [Fact]
    public void PricingScalesAndFormats()
    {
        Assert.All(Pricing.Prices.Values, m => Assert.True(m.OutputPerMtok > m.InputPerMtok && m.InputPerMtok > 0));
        var a = Pricing.EstimateCostUsd(0.10, "7d", "max_5x");
        var b = Pricing.EstimateCostUsd(0.20, "7d", "max_5x");
        Assert.Equal(2 * a, b, 6);
        Assert.True(Pricing.EstimateCostUsd(1.0, "7d", "max_5x") > Pricing.EstimateCostUsd(1.0, "7d", "pro"));
        Assert.True(Pricing.EstimateCostUsd(1.0, "7d_opus", "max_5x") > 0);
        Assert.Equal("$0.05", Pricing.FormatUsd(0.05));
        Assert.Equal("$1.23", Pricing.FormatUsd(1.234));
        Assert.Equal("$12.5", Pricing.FormatUsd(12.5));
        Assert.Equal("$1,234", Pricing.FormatUsd(1234.0));
    }
}

public class PollerTests
{
    static UsageSnapshot Ok(double u) => new() { Ok = true, Quotas = [new Quota("five_hour", "x", u, null)] };
    static UsageSnapshot RateLimited(int? retryAfter = null) => new() { StatusCode = 429, RetryAfterS = retryAfter };

    [Fact]
    public void BacksOffOn429AndResets()
    {
        var p = new Poller(new Settings());
        Assert.Equal(Poller.NormalS, p.NextInterval());
        p.UpdateBackoff(RateLimited());
        Assert.Equal(2 * Poller.NormalS, p.NextInterval());
        p.UpdateBackoff(RateLimited());
        Assert.Equal(4 * Poller.NormalS, p.NextInterval());
        p.UpdateBackoff(RateLimited(retryAfter: 99_999));
        Assert.Equal(Poller.BackoffCapS, p.NextInterval());
        p.UpdateBackoff(Ok(0.5));
        Assert.Equal(Poller.NormalS, p.NextInterval());
        p.UpdateBackoff(RateLimited());
        p.RequestRefresh();  // manual refresh clears back-off
        Assert.Equal(Poller.NormalS, p.NextInterval());
    }

    [Fact]
    public void GoesIdleAfterThreeLowReadsAndOverrideWins()
    {
        var p = new Poller(new Settings());
        for (var i = 0; i < 3; i++) p.UpdateBackoff(Ok(0.05));
        Assert.Equal(Poller.IdleS, p.NextInterval());
        p.UpdateBackoff(Ok(0.5));
        Assert.Equal(Poller.NormalS, p.NextInterval());
        Assert.Equal(60, new Poller(new Settings { RefreshSeconds = 60 }).NextInterval());
    }

    [Fact]
    public async Task LoopEmitsSnapshotsAndNoCredentials()
    {
        var got = new TaskCompletionSource<UsageSnapshot>();
        var p = new Poller(new Settings(), _ => new Credential("oauth", "t", "test"), (_, _) => Task.FromResult(Ok(0.4)));
        p.SnapshotReady += s => got.TrySetResult(s);
        p.Start();
        Assert.Equal(0.4, (await got.Task.WaitAsync(TimeSpan.FromSeconds(5))).Quotas[0].Utilization);
        p.Stop();

        var none = new TaskCompletionSource();
        var q = new Poller(new Settings(), _ => null);
        q.NoCredentials += () => none.TrySetResult();
        q.Start();
        await none.Task.WaitAsync(TimeSpan.FromSeconds(5));
        q.Stop();
    }
}

public class FormattingTests
{
    static readonly DateTimeOffset Now = new(2026, 5, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RelativeAndAge()
    {
        Assert.Equal("2d 3h", TooltipPanel.Relative(new TimeSpan(2, 3, 10, 0)));
        Assert.Equal("4h 48m", TooltipPanel.Relative(new TimeSpan(4, 48, 30)));
        Assert.Equal("7m", TooltipPanel.Relative(TimeSpan.FromMinutes(7.5)));
        Assert.Equal("42s", TooltipPanel.HumanizeAge(42));
        Assert.Equal("5m", TooltipPanel.HumanizeAge(330));
        Assert.Equal("2h", TooltipPanel.HumanizeAge(7300));
    }

    [Fact]
    public void ResetAndCountdownText()
    {
        Assert.Equal("resets — unknown", TooltipPanel.FormatReset(null, Now));
        Assert.StartsWith("resets in 1h 30m · ", TooltipPanel.FormatReset(Now.AddMinutes(90), Now));
        Assert.EndsWith("(passed)", TooltipPanel.FormatReset(Now.AddMinutes(-5), Now));

        Assert.Equal("—", UsageWidget.Countdown(null, Now));
        var snap = new UsageSnapshot { Quotas = [new("seven_day", "w", 0.1, Now.AddDays(3)), new("five_hour", "s", 0.2, Now.AddHours(2))] };
        Assert.StartsWith("resets in 2h 0m · ", UsageWidget.Countdown(snap, Now));
        Assert.Equal("resetting…", UsageWidget.Countdown(new UsageSnapshot { Quotas = [new("x", "x", 0, Now.AddMinutes(-1))] }, Now));
    }

    [Fact]
    public void ShortErrors()
    {
        Assert.Equal("API rate-limited", UsageWidget.ShortError("Rate-limited by Anthropic. Slowing down…"));
        Assert.Equal("Auth — re-login", UsageWidget.ShortError("Auth failed — token may be expired."));
        Assert.Equal("No credentials fou", UsageWidget.ShortError("No credentials found"));
    }
}
