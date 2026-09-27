namespace ClaudeMeter;

/// <summary>
/// Decides which threshold toasts fire. Each threshold fires once per reset
/// cycle per window; the state clears when the window's reset time moves.
/// Pure logic — the toast itself is <see cref="Tray.ShowToast"/>.
/// </summary>
public sealed class ThresholdNotifier(IEnumerable<double> thresholds)
{
    readonly double[] _thresholds = thresholds.Distinct().Order().ToArray();
    readonly Dictionary<string, (DateTimeOffset? ResetAt, HashSet<double> Fired)> _state = [];
    readonly Dictionary<string, DateTimeOffset> _lastResetAt = [];

    /// <summary>
    /// True once when a window has reset: the reset time we saw before has
    /// passed and the API now reports a later one. Requiring the old time to be
    /// in the past ignores API jitter on a not-yet-due reset time.
    /// </summary>
    public bool CheckReset(string window, DateTimeOffset? resetAt, DateTimeOffset now)
    {
        if (resetAt is not { } r) return false;
        var seen = _lastResetAt.TryGetValue(window, out var prev);
        _lastResetAt[window] = r;
        return seen && r > prev && prev <= now;
    }

    /// <summary>The thresholds that fired on <em>this</em> call.</summary>
    public List<double> Check(string window, double utilization, DateTimeOffset? resetAt)
    {
        if (!_state.TryGetValue(window, out var st))
            st = (null, []);
        if (resetAt is not null && st.ResetAt != resetAt)
            st = (resetAt, []);  // new cycle ⇒ wipe the fired set
        _state[window] = st;

        var fired = new List<double>();
        foreach (var t in _thresholds)
            if (utilization >= t && st.Fired.Add(t))
                fired.Add(t);
        return fired;
    }
}
