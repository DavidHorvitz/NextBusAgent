# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

Windows-only WPF app (`net8.0-windows`, single project `BusPulse/BusPulse.csproj`, no `.sln`). There is no test project or lint step yet; the plan in `docs/agent-execution-plan.md` calls for `BusPulse.Tests` (xUnit + WireMock.Net).

```powershell
dotnet build .\BusPulse\BusPulse.csproj -c Release
dotnet run --project .\BusPulse\BusPulse.csproj
dotnet publish .\BusPulse\BusPulse.csproj -c Release -r win-x64 --self-contained false
```

Regenerate binary assets (stdlib-only Python, run from repo root; the icon script takes ~25 s):

```
python -I tools/generate_icons.py   # -> BusPulse/Assets/Icons/*.ico
python -I tools/generate_sounds.py  # -> BusPulse/Assets/Sounds/*.wav
```

`.editorconfig` enforces CRLF line endings, 4-space indent (2 for xaml/json/md), and file-scoped namespaces.

## Architecture

- **Startup**: `App.OnStartup` loads the DPAPI-encrypted config, builds a Generic Host via `Program.BuildHost` (DI for `AppConfig`, `AppConfigStore`, `OpenBusService`, `ToastNotificationService`, `SoundService`, `MainWindow`, and the hosted `SchedulePollingService`), shows `MainWindow`, and starts the host in the background.
- **Shared mutable config**: a single `AppConfig` instance is a DI singleton shared by `MainWindow`, `SchedulePollingService` and `SoundService`. The UI mutates it and persists via `AppConfigStore.SaveAsync`; the poller and sound service read it live (e.g. `NotificationsMuted`, selected places, `LineName`).
- **Config storage**: `AppConfigStore` serializes the whole config as JSON and encrypts it with DPAPI `CurrentUser` plus fixed entropy to `%LOCALAPPDATA%\BusPulse\settings.dpapi`. JSON options reject unmapped members, `Validate()` runs on load and save, and legacy `StopCode` is migrated into saved places.
- **Polling**: `SchedulePollingService` (a `BackgroundService`) polls `OpenBusService` for the selected start place and line. Interval tiers (5 min / 1 min / 20 s) are defined, but arrival-time estimates are not computed yet, so it always uses the 5-minute tier.
- **OpenBus client**: `OpenBusService` uses the named `TransitApi` `HttpClient` (TLS 1.3 only, redirects disabled, 12 s timeout, configured in `Program`). Responses are capped at 1 MiB, parsed with strict `JsonDocument` options, and filtered by a provisional list of line-name field names. The live schema is unverified.
- **Tray and UI** (`MainWindow.xaml.cs`): the WinForms `NotifyIcon` and context menu are created in code. Mute toggling swaps the tray icon and tooltip between `bus_active.ico` and `bus_muted.ico`; `bus_alert.ico` is loaded but not yet used. WPF and WinForms are both enabled, so type names often need `System.Windows` / `System.Drawing` disambiguation.
- **Localization**: `Resources/Strings.{en,he}.xaml` are swapped at runtime by replacing `MergedDictionaries[0]`, so that slot must stay the strings dictionary. Hebrew sets `FlowDirection.RightToLeft`. Some tray menu text is hard-coded English.
- **Assets**: `Assets/**` is copied to the output directory (`Content` items in the csproj) and loaded from `AppContext.BaseDirectory`, not embedded. `SoundService` plays `arrival.wav` / `delay.wav` only when not muted. Nothing calls it yet, because toast and arrival logic is unwired. Asset requirements are in `docs/assets.md`; the generator scripts are the editable source for the binaries.
- **Toasts**: `ToastNotificationService` only builds toast XML. Delivery is not wired, and unpackaged delivery needs an AUMID and Start Menu shortcut registered at per-user install time.

## Constraints from the project docs

- Keep installation and data per-user with no elevation. Never put credentials in source or plaintext config.
- Audio must never play per polling update, and mute must work independently of Windows volume. Color must never be the only state cue (tooltips carry state names).
- .NET 8 support ends 2026-11-10; plan a runtime upgrade.
