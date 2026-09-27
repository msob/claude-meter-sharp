# Tickets 01 — Port Claude Meter from Python to C#, drop WSL

## Goal

Rewrite Claude Meter (v0.1.4, Python + PySide6, ~3.3k lines in `src/claude_usage_widget/`) in C#.
Keep the same behaviour and look. Remove the WSL dependency: today `credentials.py` runs `wsl.exe`
to find credentials.

## Decisions

| Topic      | Choice                                                                                            |
|------------|---------------------------------------------------------------------------------------------------|
| Runtime    | .NET 10 (LTS), `net10.0-windows`                                                                  |
| UI         | WPF. WinForms is referenced only for `NotifyIcon` (tray and balloon toasts)                       |
| Shipping   | Framework-dependent single-file `ClaudeMeter.exe`. The user needs the .NET 10 Desktop Runtime     |
| WSL        | Removed. No `wsl.exe` calls and no child processes. WSL users set an explicit credentials path    |
| Tests      | xUnit, porting the existing pytest cases                                                          |

## Ground rules

- **Behaviour parity with v0.1.4.** Port the code; don't redesign it. Improvements go in later tickets.
- **Use the platform first:** the base class library (`HttpClient`, `System.Text.Json`, `Microsoft.Win32.Registry`,
  `Mutex`), then WPF/WinForms, then P/Invoke. The app itself needs no NuGet packages.
- **Keep compatibility for existing users.** Keep `%APPDATA%\ClaudeMeter\settings.json`, `%APPDATA%\ClaudeMeter\history.json`
  and the HKCU `Run` value `ClaudeMeter` in the same format, so an upgrade from v0.1.4 keeps the user's settings,
  history and autostart.
- **One source file per Python module** as a rule of thumb. Don't add abstractions the Python code doesn't have.

## Order

```
T01 ─┬─ T02 ─ T03 ─ T05 ─┐
     ├─ T04 ─────────────┤
     ├─ T06              ├─ T12 ─ T13 ─ T14
     ├─ T07 ─ T08        │
     └─ T09 ─ T10 ─ T11 ─┘
```

T02–T06 are pure logic and can be tested without a UI. T14 goes last and runs only after the C# build has been verified.

---

## T01 — Scaffold solution

**Scope**
- `src-sharp/ClaudeMeter.slnx`
- `src-sharp/ClaudeMeter/ClaudeMeter.csproj`: `net10.0-windows`, `UseWPF=true`, `UseWindowsForms=true` (for `NotifyIcon` only),
  `Nullable=enable`, `OutputType=WinExe`, `AssemblyName=ClaudeMeter`
- `app.manifest` with PerMonitorV2 DPI awareness
- `src-sharp/ClaudeMeter.Tests/` (xUnit) referencing the app project
- `.gitignore`: add `bin/`, `obj/`, `*.user`
- Single-instance guard: a named `Mutex("ClaudeMeterSingleInstance")` in `App.OnStartup`. If another instance
  already holds it, exit silently. This replaces the `QSharedMemory` guard in `main.py`.
- `ShutdownMode=OnExplicitShutdown`, the equivalent of `setQuitOnLastWindowClosed(False)`

**Acceptance**
- `dotnet build` and `dotnet test` succeed on a clean clone.
- Launching the exe twice leaves only one process running.

---

## T02 — Credentials (WSL removed)

**Ports** `credentials.py`

**Scope**
- `Credential` record: `Kind` (`oauth` | `api_key`), `Token`, `Source`, `ExpiresAtMs?`, and `IsExpired` with 30 s leeway.
- Discovery order. The first non-expired candidate wins. If every candidate is expired, return the first one anyway,
  because the resulting 401 tells the user to log in again.
  1. `Settings.CredentialsPath`, if set (**new**, see below)
  2. `%USERPROFILE%\.claude\.credentials.json` (`claudeAiOauth.accessToken` and `expiresAt`)
  3. `Settings.ManualApiKey`
  4. `ANTHROPIC_API_KEY` environment variable
- **Delete** all WSL code: `_try_wsl_claude_code`, `_find_wsl_exe`, `_list_wsl_distros`, `_wsl_read`,
  `_decode_wsl_output`, and the `CREATE_NO_WINDOW` subprocess flags.
- **New setting `credentials_path`** (optional string). It is the escape hatch for users who run Claude Code only
  inside WSL. They point it at `\\wsl.localhost\<distro>\home\<user>\.claude\.credentials.json`. The app reads it as
  a normal file and starts no process. A missing or unreadable file falls through to the next candidate.

**Acceptance**
- The app contains no `Process.Start` calls and no `wsl` references.
- Port the 5 cases in `tests/test_credentials.py`: valid file, missing `claudeAiOauth`, corrupt JSON,
  environment variable fallback, expired token. Add one case for `credentials_path`.

---

## T03 — Usage client + parser

**Ports** `usage.py`

**Scope**
- Records: `Quota` (`Key`, `Label`, `Utilization`, `ResetsAt?`, `Percent`), `CountQuota`, `Overage`, and `UsageSnapshot`
  (`Ok`, `Error`, `RetryAfterS`, `FetchedAt`, `Plan`, lists, `CredentialSource`/`Kind`, `ByKey()`).
- `Probe(Credential)`:
  - Uses one static `HttpClient` with a 10 s timeout.
  - Sends the same headers as today: `Authorization: Bearer`, `anthropic-version: 2023-06-01`,
    `anthropic-beta: oauth-2025-04-20`, `User-Agent: claude-code/1.0 (claude-usage-widget)`, and `Accept: application/json`.
  - Never throws. Every failure is reported in `Error`.
  - Rejects `api_key` credentials with the existing message.
  - Reads `Retry-After` on every response.
  - Maps 401 and 429 to the existing messages. Other non-2xx responses become `HTTP <code>: <error.message or first 200 chars of the body>`.
- `ParseInto(snapshot, JsonElement)` (split out so it can be tested):
  - Known keys come first, in `KNOWN_QUOTA_ORDER`, with their labels. `seven_day_omelette` maps to "Weekly · Claude Design".
  - Unknown keys with a utilization value follow, with humanised labels. `extra_usage`, `daily_routine_runs` and `plan` are skipped.
  - Percent-scale detection: if any utilization value is above 1.5, divide every value by 100.
  - Numbers may arrive as JSON numbers or as numeric strings.
  - Parse ISO timestamps (including a trailing `Z`) to UTC.
  - Plan: `plan`, `subscription` or `plan_name`, either a string or an object with `name` or `display_name`.
  - Overage comes from `extra_usage`. Count quotas are objects with a used/limit pair and no utilization field.

**Acceptance**
- Port every case in `tests/test_usage.py`.

---

## T04 — Settings + history

**Ports** `settings.py`, `history.py`

**Scope**
- `Settings` class with the same fields and defaults. Add `CredentialsPath` (from T02).
- Serialise with `System.Text.Json` and `JsonNamingPolicy.SnakeCaseLower`, indented output. Unknown keys are ignored.
  A missing or corrupt file falls back to defaults.
- History:
  - `Bucket(t, u5h, u7d, uopus)` in 10-minute buckets.
  - If the newest bucket has the same `t`, merge the readings and keep the maximum of each value.
  - Keep 14 days and trim older buckets.
  - Timestamps are ISO strings ending in `Z`. Trimming compares these strings.
  - Write compact JSON.
  - `SparklineSeries(field, points=96)`.
- Both files live in `%APPDATA%\ClaudeMeter\`.

**Acceptance**
- Loads `settings.json` and `history.json` written by v0.1.4 without data loss. Add one fixture file per format.
- Port `tests/test_history.py`.

---

## T05 — Poller

**Ports** `poller.py`

**Scope**
- An async loop on the thread pool (`Task.Run` with a `CancellationToken`). No dedicated thread object is needed,
  which retires the QThread-parenting gotcha.
- Cadence:
  - `refresh_seconds` greater than 0 overrides everything else.
  - Otherwise poll every 20 min after 3 consecutive reads with a top utilization below 15%, and every 7 min in all other cases.
- On 429, back off: `min(60 min, max(Retry-After, current × 2))`. Any other result resets the back-off.
- `RequestRefresh()` wakes the loop immediately (a `SemaphoreSlim` or a cancelled delay) and clears the back-off.
- Emits `SnapshotReady(UsageSnapshot)` and `NoCredentials`. The controller marshals both to the UI thread with `Dispatcher.InvokeAsync`.
- `Stop()` cancels the loop and waits up to 2 s.
- Detect the back-off case from a status code or flag on the snapshot, not from the `"Rate-limited" in error` string match.

**Acceptance**
- Unit test for `NextInterval` and the back-off maths. Inject the clock and the probe; don't make network calls.

---

## T06 — Notifier, pricing, products

**Ports** `notifications.py`, `pricing.py`, `products.py`

**Scope**
- `ThresholdNotifier.Check(window, utilization, resetAt)` returns the thresholds that fire on this call.
  The set of fired thresholds is kept per reset cycle and cleared when `resetAt` changes.
- Toast: `NotifyIcon.ShowBalloonTip` on the tray icon from T08. Windows 10/11 show it as a native toast.
  This removes the optional `win11toast`/`win10toast` fallbacks and the console print.
- `Pricing`: the `PRICES` and `PLAN_CAPS` tables, `EstimateCostUsd`, and `FormatUsd`, ported 1:1.
- `Products.DetectAll()`: Claude Code (Windows), Claude desktop, Cowork, Codex CLI, and the Chrome extensions directory.
  Drop the WSL comment.

**Acceptance**
- Port `tests/test_notifications.py` and `tests/test_pricing.py`.

---

## T07 — Win32 interop

**Ports** `fullscreen.py`, `win32_glass.py`, `startup.py`

**Scope**
- One `Native.cs` with the P/Invoke declarations: `GetForegroundWindow`, `GetShellWindow`, `GetDesktopWindow`,
  `GetWindowRect`, `GetWindowLongPtr`/`SetWindowLongPtr`, `DwmSetWindowAttribute`, and `DestroyIcon`.
- `IsForegroundFullscreen(monitorRect)`: the foreground window covers the monitor (±4 px) and has no `WS_CAPTION`.
  Maximised windows keep their caption, so they don't count. Work in physical pixels for the monitor of the widget.
- `TryEnableGlassBackdrop(hwnd)`: `DWMWA_SYSTEMBACKDROP_TYPE` (38) = `DWMSBT_TRANSIENTWINDOW` (3). Fails silently.
- Helper to add `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`, so the window is not in Alt-Tab and never takes focus.
- `Startup`: `Microsoft.Win32.Registry`, `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value `ClaudeMeter` = quoted
  `Environment.ProcessPath`. Provide `IsEnabled`, `Enable` and `Disable`. The "frozen" check goes away because the exe is always the real binary.
- Drop `theme.is_dark_mode`. It is unused.

**Acceptance**
- Enabling autostart in v0.1.4 and then running the C# build shows autostart as enabled.

---

## T08 — Tray icon

**Ports** `tray.py`

**Scope**
- WinForms `NotifyIcon` with a `ContextMenuStrip`. Menu items: Show/Hide widget, Refresh now, Settings…, Run at startup (checkable), and Quit.
- Clicking the tray icon toggles the widget, as it does today.
- `SetState(utilization, error, label, tooltip)`:
  - Draw the icon with `System.Drawing` using the colour ramp from T09, or the error glyph.
  - Destroy the old icon handle with `DestroyIcon` to avoid a GDI leak.
  - `NotifyIcon.Text` has a 127-character limit, so truncate the tooltip.

**Acceptance**
- The icon colour follows utilization.
- The icon survives 24 h of updates without GDI handle growth. Check the GDI objects column in Task Manager.

---

## T09 — Theme, logo, glyphs

**Ports** `theme.py`, `claude_logo.py`, `icons.py`

**Scope**
- `Theme` holds the palette from `theme.py` as frozen `SolidColorBrush`es, plus `UtilColor(u)`: blue below 50%, orange below 80%, red otherwise.
- Claude logo:
  - Run `Geometry.Parse` on the single `<path d=…>` from `assets/claude-logo.svg`, embedded as a resource and filled with coral.
  - Drop the asset-scanning override logic (user-supplied logos in `%APPDATA%`/`assets/`) and the hand-drawn fallback. No ticket needs them.
- Refresh glyph (a 300° clockwise arc with an arrow head) and clock glyph as `StreamGeometry` helpers, ported from `icons.py`.

**Acceptance**
- The logo and glyphs render crisply at 100%, 150% and 200% scaling.

---

## T10 — Widget window

**Ports** `widget.py`

**Scope**
- Borderless 264×84 DIP window: `Topmost`, `ShowInTaskbar=false`, `ShowActivated=false`, with the no-activate/tool style from T07.
- Position: bottom-right of the primary work area, offset by `pos_offset_right` and `pos_offset_bottom`.
  `Reposition()` is called again after the settings are saved.
- Rendering: override `OnRender(DrawingContext)` as a direct port of `paintEvent`:
  - Logo area.
  - Compact rows for `five_hour` and `seven_day`, drawn with gradient bars.
  - Countdown text ("resets in 4h 48m").
  - Amber stale dot, and an "auth expired / not refreshed" state.
  - Refresh button with a hit area, a 15 s cooldown, and a 2 s "refreshing…" label.
- Timers (`DispatcherTimer`): the countdown tick, and a visibility tick that hides the widget while a real fullscreen app runs,
  if `hide_when_fullscreen` is on.
- Context menu: Refresh, Hide for N min (`snooze_minutes`), Hide, Settings…, and Quit. `ManualShow`, `ManualHide` and snooze behave as they do today.
- Hover shows the tooltip panel from T11. It hides 120 ms after the mouse leaves, unless the mouse is over the panel.
- **Spike first:** in WPF, `Opacity` needs `AllowsTransparency=true`, which makes the window layered. A layered window
  can't use the DWM Acrylic backdrop. Choose one of these, then note the choice in CLAUDE.md:
  - (a) `AllowsTransparency` with a semi-transparent brush. This keeps the `opacity` setting and drops Acrylic.
  - (b) Acrylic with `WindowChrome`. This keeps the glass and maps `opacity` to background alpha.

**Acceptance**
- Compare side by side with `assets/Screenshot.png`.
- The window never steals focus and doesn't appear in Alt-Tab.
- A maximised browser does not hide the widget. A fullscreen video does.

---

## T11 — Tooltip panel

**Ports** `tooltip.py`

**Scope**
- A second borderless, topmost, no-activate window whose height follows its content.
- Content:
  - Header: plan and credential source.
  - One quota row per `Quota`: label, percent, bar, and reset time.
  - Count rows and the overage row.
  - 14-day sparkline from `history.SparklineSeries`.
  - "Updated N min ago", refreshed by a 1 s tick while the panel is visible.
- `ShowNear(anchor)`: place the panel above the widget, and beside it when there isn't room above. This is the known tooltip-overlap gotcha.
  Clamp the panel to the work area.
- Port `_format_reset`, `_humanize_age` and `_shorten` as static helpers, with a small unit test.

**Acceptance**
- The panel is never clipped off-screen when the taskbar is at the bottom, the top, or on a secondary monitor.

---

## T12 — Settings window + app controller

**Ports** `settings_dialog.py`, `app.py`, `main.py`

**Scope**
- `SettingsWindow` (modal) with the same fields as `settings_dialog.py`: refresh interval, plan, manual API key,
  notifications on/off and thresholds, hide when fullscreen, offsets, opacity, glass, and snooze minutes.
  Add a **Credentials file** path field with a Browse… button.
- `AppController` (the `WidgetApp` equivalent):
  - Wires the tray, widget, poller, history and notifier.
  - When a refresh fails, keeps showing the last good snapshot and marks it as stale. It shows an error only if no data has arrived yet.
  - Tray label is the `five_hour` percent. The tray tooltip lists one line per quota.
  - Fires threshold toasts.
  - When settings are saved: persist them, rebuild the notifier, reposition the widget, re-apply visual settings, and refresh.
  - Quit stops the poller, disposes the `NotifyIcon`, and releases the mutex.

**Acceptance**
- End to end: launch, then see data within about 10 s.
- With networking disabled, the last good data stays on screen with the stale dot.
- Saving settings takes effect without a restart.

---

## T13 — Build & CI

**Ports** `build.spec`, `.github/workflows/ci.yml`, `.github/workflows/release.yml`

**Scope**
- Publish command:
  `dotnet publish src-sharp/ClaudeMeter -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:Version=<tag>`
  produces `ClaudeMeter.exe`.
- `ci.yml`: `windows-latest`, `actions/setup-dotnet` (10.0.x), `dotnet test`. It runs on push and pull request, as today.
- `release.yml`: on a `v*.*.*` tag, test, publish, upload the artifact, and create a GitHub Release with the exe.
  Take the version from the tag, removing the leading `v`.
- The app icon (`.ico`) is generated from the logo and set as `ApplicationIcon`.

**Acceptance**
- Pushing a tag produces a release with `ClaudeMeter.exe`.
- The exe runs on a clean Windows 11 VM that has only the .NET 10 Desktop Runtime installed.

---

## T14 — Remove Python, update docs

**Scope**
- Delete the Python code and build files:
  - `src/claude_usage_widget/`
  - `tests/*.py` and `tests/__init__.py`
  - `pyproject.toml`, `requirements.txt`, `requirements-dev.txt`
  - `build.spec`, `build.cmd`, `build-exe.cmd`, `run.cmd`
  - the empty `claude-meter/` directory
- `.gitignore`: remove the Python and PyInstaller sections.
- `README.md`:
  - Replace the Python badge with a .NET badge.
  - Prerequisite: the .NET 10 Desktop Runtime, with a link.
  - Build from source: `dotnet publish …`.
  - WSL users set Settings → Credentials file.
- `SETUP.md`: replace the Python and venv instructions with the `dotnet` ones.
- `CHANGELOG.md`: add a v0.2.0 entry: rewritten in C#/WPF, WSL auto-discovery removed (explicit path instead),
  toasts are now native, and settings, history and autostart carry over.
- `CLAUDE.md`:
  - Change the stack line to C#/WPF/.NET 10.
  - Drop the PyInstaller-import, QThread and `wsl.exe` console-flash gotchas.
  - Keep the tooltip, fullscreen and morning-stale gotchas.
  - Add the T10 transparency/Acrylic decision.

**Acceptance**
- `git grep -il python` finds only the historical CHANGELOG entries.
- `git grep -i wsl -- src` is empty.

---

## Implementation notes (2026-09-27)

All tickets were implemented in `src-sharp/`. Where the result differs from the tickets above:

- **T06** — `pricing.py` was ported with its tests, although nothing in the UI calls it (same as in v0.1.4).
- **T10** — the spike chose option (a): `AllowsTransparency` + opacity, no Acrylic. `enable_glass_backdrop` only round-trips in the settings file,
  and the Settings window no longer shows it.
- **T11** — `_shorten` was unused in v0.1.4, so it was dropped. The header shows the plan only, as v0.1.4 did, not the credential source.
  The refresh tick is 30 s, as in v0.1.4, not 1 s.
- **T12** — the widget and tray context menus use WinForms `ContextMenuStrip`. A WPF `ContextMenu` on a no-activate window may not close reliably.

## Definition of done (whole migration)

- `dotnet test` passes locally and in CI.
- The published framework-dependent exe runs on a machine with only the .NET 10 Desktop Runtime.
- A user upgrading from v0.1.4 keeps their settings, 14-day history and autostart entry.
- The widget, tooltip and tray match v0.1.4 visually and behaviourally, checked side by side.
- The app starts no processes and has no WSL code. WSL-only users are covered by the explicit credentials path.
