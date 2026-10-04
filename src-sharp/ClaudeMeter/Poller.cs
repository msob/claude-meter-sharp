namespace ClaudeMeter;

/// <summary>
/// Background loop that probes the usage API on an adaptive schedule
/// (the /api/oauth/usage endpoint rate-limits aggressively):
///   * normal — every 7 min
///   * idle   — every 20 min after 3 consecutive reads below 15%
///   * back-off — on 429 double the interval (cap 60 min), honouring Retry-After if larger
/// A manual refresh fires immediately and clears the back-off.
/// Events are raised on a thread-pool thread — marshal to the UI yourself.
/// </summary>
public sealed class Poller(
    Settings settings,
    Func<Settings, Credential?>? discover = null,
    Func<Credential, CancellationToken, Task<UsageSnapshot>>? probe = null)
{
    public const int NormalS = 7 * 60;
    public const int IdleS = 20 * 60;
    public const int BackoffCapS = 60 * 60;

    readonly Func<Settings, Credential?> _discover = discover ?? (s => Credentials.Discover(s.ManualApiKey, s.CredentialsPath));
    readonly Func<Credential, CancellationToken, Task<UsageSnapshot>> _probe =
        probe ?? ((c, ct) => Usage.ProbeAsync(c, settings.IgnoreTlsErrors, ct));
    readonly SemaphoreSlim _kick = new(0, 1);
    readonly CancellationTokenSource _cts = new();
    Task? _loop;
    int? _backoffS;
    int _consecutiveLow;

    public event Action<UsageSnapshot>? SnapshotReady;
    public event Action? NoCredentials;

    public void Start() => _loop = Task.Run(() => RunAsync(_cts.Token));

    public void RequestRefresh()
    {
        _backoffS = null;  // the user wants data NOW
        try { _kick.Release(); } catch (SemaphoreFullException) { }  // already kicked
    }

    public void Stop()
    {
        _cts.Cancel();
        try { _loop?.Wait(2000); } catch (AggregateException) { }
    }

    async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await PollOnceAsync(ct);
            while (!ct.IsCancellationRequested)
            {
                await _kick.WaitAsync(TimeSpan.FromSeconds(NextInterval()), ct);
                await PollOnceAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    async Task PollOnceAsync(CancellationToken ct)
    {
        if (_discover(settings) is not { } cred)
        {
            NoCredentials?.Invoke();
            return;
        }
        var snap = await _probe(cred, ct);
        ct.ThrowIfCancellationRequested();
        UpdateBackoff(snap);
        SnapshotReady?.Invoke(snap);
    }

    internal int NextInterval()
    {
        var baseS = settings.RefreshSeconds > 0 ? settings.RefreshSeconds
                  : _consecutiveLow >= 3 ? IdleS
                  : NormalS;
        return _backoffS is int b ? Math.Max(baseS, b) : baseS;
    }

    internal void UpdateBackoff(UsageSnapshot snap)
    {
        if (!snap.Ok && snap.RateLimited)
        {
            _backoffS = Math.Min(BackoffCapS, Math.Max(snap.RetryAfterS ?? 0, (_backoffS ?? NormalS) * 2));
            return;
        }
        _backoffS = null;
        if (snap.Ok && snap.Quotas.Count > 0)
            _consecutiveLow = snap.Quotas.Max(q => q.Utilization) < 0.15 ? _consecutiveLow + 1 : 0;
    }
}
