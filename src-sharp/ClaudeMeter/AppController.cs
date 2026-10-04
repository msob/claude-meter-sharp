using System.Windows;

namespace ClaudeMeter;

/// <summary>
/// Glues tray, widget, poller, history and notifications together.
/// A failed refresh never wipes the widget: the last good snapshot stays on
/// screen with the new error injected, so the widget shows a "stale" dot.
/// </summary>
public sealed class AppController
{
    readonly Application _app;
    readonly Settings _settings = Settings.Load();
    readonly UsageWidget _widget;
    readonly Tray _tray;
    readonly Poller _poller;
    readonly List<DetectedProduct> _products = Products.DetectAll();
    List<Bucket> _history = History.Load();
    ThresholdNotifier _notifier;
    UsageSnapshot? _lastGood;

    public AppController(Application app)
    {
        _app = app;
        _widget = new UsageWidget(_settings);
        _tray = new Tray(onOpen: _widget.Toggle, onRefresh: Refresh, onSettings: OpenSettings, onQuit: Quit);
        _notifier = new ThresholdNotifier(_settings.NotifyAtThresholds);

        _widget.RefreshRequested += Refresh;
        _widget.SettingsRequested += OpenSettings;
        _widget.QuitRequested += Quit;
        _widget.Show();
        _widget.UpdateData(null, _history, _products);

        _poller = new Poller(_settings);
        _poller.SnapshotReady += snap => app.Dispatcher.InvokeAsync(() => OnSnapshot(snap));
        _poller.NoCredentials += () => app.Dispatcher.InvokeAsync(OnNoCredentials);
        _poller.Start();
    }

    void OnSnapshot(UsageSnapshot snap)
    {
        if (snap.Ok && snap.Quotas.Count > 0)
        {
            _lastGood = snap;
            var five = snap.ByKey("five_hour");
            var seven = snap.ByKey("seven_day");
            var opus = snap.ByKey("seven_day_opus");
            try { _history = History.Append(five?.Utilization, seven?.Utilization, opus?.Utilization); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }  // history is best-effort
            _tray.SetState(snap.Quotas.Max(q => q.Utilization), error: false,
                TrayTooltip(snap, snap.TlsWarning is null ? null : "TLS certificate check bypassed — hover the widget for details"));
            _widget.UpdateData(snap, _history, _products);
            // Always run the checks so reset tracking stays current; only toast when enabled.
            var reset = snap.Quotas.Where(q => _notifier.CheckReset(q.Key, q.ResetsAt, DateTimeOffset.UtcNow)).ToList();
            foreach (var q in snap.Quotas)
                foreach (var t in _notifier.Check(q.Key, q.Utilization, q.ResetsAt))
                    if (_settings.NotificationsEnabled)
                        _tray.ShowToast($"{q.Label} at {(int)(t * 100)}%",
                            $"You've used {q.Percent:0}% of your {q.Label.ToLowerInvariant()} quota.");
            // One toast for all windows that reset together (the weekly rows share a reset time).
            if (_settings.NotificationsEnabled && reset.Count > 0)
                _tray.ShowToast(reset.Count == 1 ? $"{reset[0].Label} has reset" : "Claude usage has reset",
                    string.Join("\n", reset.Select(q => $"{q.Label}: now {q.Percent:0}%")));
            return;
        }

        if (_lastGood is not null)
        {
            // Keep last-good numbers (and tray colour); the error lives in the tooltip + stale dot.
            var merged = _lastGood with { Ok = false, Error = snap.Error, StatusCode = snap.StatusCode, RetryAfterS = snap.RetryAfterS, TlsWarning = null };
            _tray.SetState(_lastGood.Quotas.Max(q => q.Utilization), error: false,
                TrayTooltip(_lastGood, $"Last refresh failed: {snap.Error}"));
            _widget.UpdateData(merged, _history, _products);
            return;
        }

        _tray.SetState(0.0, error: !snap.Ok, $"Claude Meter — {snap.Error ?? "no data"}");
        _widget.UpdateData(snap, _history, _products);
    }

    void OnNoCredentials()
    {
        _tray.SetState(0.0, error: true, "No Claude credentials found. Log in with `claude` CLI, or set a credentials file in Settings…");
        _widget.UpdateData(new UsageSnapshot { Ok = false, Error = "No credentials found" }, _history, _products);
    }

    static string TrayTooltip(UsageSnapshot snap, string? note = null)
    {
        var lines = new List<string> { snap.Plan is { } plan ? $"Claude Meter — {plan}" : "Claude Meter" };
        lines.AddRange(snap.Quotas.Select(q => $"  {q.Label}: {q.Percent:0.0}%"));
        if (note is not null) lines.AddRange(["", note]);
        return string.Join("\n", lines);
    }

    void Refresh() => _poller.RequestRefresh();

    void OpenSettings()
    {
        if (new SettingsWindow(_settings).ShowDialog() != true) return;
        try { _settings.Save(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Couldn't save settings: {e.Message}", "Claude Meter");
        }
        _notifier = new ThresholdNotifier(_settings.NotifyAtThresholds);
        _widget.Reposition();
        _widget.ApplyVisualSettings();
        _poller.RequestRefresh();
    }

    void Quit()
    {
        _poller.Stop();
        _tray.Dispose();
        _app.Shutdown();
    }
}
