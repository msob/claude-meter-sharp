# CLAUDE.md — Claude Meter

Context for any Claude (or human) picking up this repo. Keep it current.

## What this is

A live Windows taskbar widget showing your Claude usage — per-model breakdown,
percentages, and reset countdowns — so you can see how much quota is left without
leaving the editor. C# / WPF on .NET 10 (`src-sharp/`), shipped as a single
framework-dependent `.exe`. v0.1.x was Python + PySide6; see `doc/tickets-01.md`
for the port.

## How it gets the data

`GET https://api.anthropic.com/api/oauth/usage` with `Authorization: Bearer <token>`
and header `anthropic-beta: oauth-2025-04-20`. The token is the
`claudeAiOauth.accessToken` from `~/.claude/.credentials.json` (or the file in the
`credentials_path` setting — no `wsl.exe`, no child processes). The JSON returns
per-quota rows (`five_hour`, `seven_day`, `seven_day_sonnet`, `seven_day_opus`,
`seven_day_omelette` = the Claude Design codename). The endpoint rate-limits hard,
so polling is adaptive with backoff.

## UI

WPF windows drawn in `OnRender` (a `Surface` element — the stand-in for Qt's
`paintEvent`, coordinates are DIPs = the old Qt logical pixels), light "Claude
brew" theme, the Claude logo from the SVG path in `Theme.cs`, progress bars
coloured blue → orange (>50%) → red (>80%), reset countdowns, refresh control.
Tray icon, tray/widget context menus and toasts use WinForms `NotifyIcon` /
`ContextMenuStrip` (`UseWindowsForms` is on only for that). No XAML.

## Hard-won gotchas

- **Tooltip overlap** — reposition beside the widget when it can't fit above.
- **Fullscreen detection** — hide only for real fullscreen (foreground covers the
  monitor AND has no `WS_CAPTION`); maximized windows keep their caption.
- **Morning stale data** — the widget showed yesterday's data and refresh hung;
  handled by re-probing and a visible "auth expired / not refreshed" state
  (keyed off HTTP 401 / 429 status codes, not error text).
- **Transparency vs Acrylic** — WPF `Opacity` needs `AllowsTransparency=true`
  (a layered window), which can't use the DWM Acrylic backdrop. We kept opacity
  and dropped Acrylic; `enable_glass_backdrop` only round-trips in settings.json.
- **WPF + WinForms name clashes** — implicit `System.Windows.Forms` /
  `System.Drawing` usings are removed in the csproj; alias them (`WF`, `SD`).
  WPF also drops the implicit `System.IO` using; the csproj adds it back.
- **Never steal focus** — widget and tooltip set `WS_EX_NOACTIVATE |
  WS_EX_TOOLWINDOW` in `SourceInitialized` (also keeps them out of Alt-Tab).
- **TLS diagnostics** — `Usage.cs` has a strict and a lenient `HttpClient`; one
  validation callback records why a certificate failed (on the request's
  `Options`) and only the lenient one accepts it. Requests send
  `Connection: close` so the callback runs on every poll, never skipped by a
  pooled connection. `TlsTests` cover both paths against a local TLS server.
- **Settings/history compatibility** — keep snake_case keys and the
  `%APPDATA%\ClaudeMeter\` paths; v0.1.x users upgrade in place.

## Build & ship

`dotnet test src-sharp/ClaudeMeter.slnx`; `dotnet publish src-sharp/ClaudeMeter -c Release
-r win-x64 --self-contained false -p:PublishSingleFile=true` → ~300 KB `.exe` needing
the .NET 10 Desktop Runtime. GitHub Actions (windows-latest): `release.yml` tests and
publishes a release on every push to main (version = csproj major.minor + run
number; `[skip ci]` skips); `ci.yml` tests other branches and PRs. SmartScreen warns on the unsigned exe (More info → Run
anyway); documented in the README.

## Roadmap

v0.2: browser cookie auth (sessionKey from Chrome/Edge), Codex usage support.
Later: multi-account cycling, Linux tray build, CSV export of the 14-day history.
**v0.3 idea:** log usage over time and add a rich history dashboard (trends per
model, burn rate, time-of-day heatmaps, projections).

## Part of the fleet

- **Claude Meter** — you are here.
- [Claude Lifeboat](https://github.com/JackBhanded/claude-lifeboat) — backup & restore for Claude data.
- [Claude Lifejacket](https://github.com/JackBhanded/claude-lifejacket) — keep every session aware of your projects.

_Maintainer's working-style/personal context is kept in private notes, not in this public file._
