# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

Windows-only WPF app (`net10.0-windows10.0.19041.0`). Solution: `BusPulse.sln` with `BusPulse/` (app) and `BusPulse.Tests/` (xUnit).

```powershell
dotnet build .\BusPulse.sln -c Release
dotnet test .\BusPulse.sln --filter "Category!=Live"          # what CI runs
dotnet test .\BusPulse.sln --filter "FullyQualifiedName~StopsViewModelTests"   # one class; use ~TestName for one test
dotnet test .\BusPulse.sln --filter "Category=Live"            # manual probes against curlbus.app and Stride
dotnet run --project .\BusPulse\BusPulse.csproj
dotnet list .\BusPulse.sln package --vulnerable --include-transitive   # CI fails if this reports anything
```

Regenerate binary assets (stdlib-only Python, from the repo root; the icon script takes about 25 s):

```
python -I tools/generate_icons.py   # -> BusPulse/Assets/Icons/*.ico
python -I tools/generate_sounds.py  # -> BusPulse/Assets/Sounds/*.wav
```

`.editorconfig` enforces CRLF, 4-space indent (2 for xaml/json/md) and file-scoped namespaces. WPF disables the `System.IO` implicit using, so add `using System.IO;` explicitly (also in the test project).

## Plan and decisions

`docs/PLAN.md` is the single source of truth for scope, decisions, per-phase status and next steps. Read it before starting work. Key decisions: .NET 10 (LTS); OS-default TLS (the Stride host only speaks TLS 1.2); GitHub-hosted `windows-latest` CI, no self-hosted runner; Curlbus is the primary live source; address search uses the public OSM Nominatim server, explicit searches only (the maintainer confirmed the usage policy on 2026-11-07); the Israel Railways key scraped from rail.co.il is deliberately not used (`RailProvider` stays disabled until `RailApiKey` is configured); no IP-based geolocation. `docs/live-data-probe.md` records measured provider behavior, and `SECURITY.md` the data and hosts the app touches.

## Architecture

- **Startup:** `App.OnStartup` loads the DPAPI-encrypted `AppConfig`, builds the Generic Host in `Program.BuildHost` (all DI registrations live there), resolves `MainWindow`, shows it, and starts the host in the background (hosts `SchedulePollingService`).
- **MVVM:** `MainWindow` (a WPF-UI `FluentWindow`) binds to `ShellViewModel`, which owns the child view models (`StopsViewModel`, `TripPlannerViewModel`, `LinesViewModel`, `SettingsViewModel`) shown in a tab control. Code-behind is wiring only. Cross-cutting seams: `IDispatcher` (the only UI-thread marshalling), `ILocalizer` (resource lookup and runtime language swap by replacing `MergedDictionaries[0]`, so that slot must stay the strings dictionary), `ISettingsService` (config plus persistence that returns false instead of throwing), `SavedPlaceCollection` (places shared by Settings, Trip planner and Stops), `TrayController` (notification-area icon driven by `ShellViewModel`), `IThemeService` (system/light/dark; brushes follow a Windows theme switch). Closing the window hides it to the tray; only the tray Exit ends the app. Diagnostic launch flags `--theme=light|dark` and `--tab=N` apply to one run and are never saved.
- **Activity gating:** `ShellViewModel` calls `StopsViewModel.SetActive(...)` only when the Stops tab is selected and the window is visible and not minimized; only then does the 1 s countdown ticker and the 25 s refresh of expanded stops run.
- **Live data layer** (`Services/LiveData`): `ILiveArrivalsProvider` implementations (`CurlbusArrivalsProvider` with a per-stop cache, 1 req/s spacing and a circuit breaker; `RailProvider`, disabled) plus `IScheduleProvider` (`StrideScheduleProvider`, GTFS). `LiveDataAggregator` queries providers in `ArrivalSource` priority order, discards invalid, stale (over 5 minutes) or future data, picks the freshest provider per line, and fills lines without live data from the schedule marked `IsLive = false`. Every `Arrival` carries `Source`, `IsLive` and `ObservedAt`; nothing scheduled may be presented as live. Live arrivals are paired with scheduled rides by trip ref (`Arrival.TripRef` against `RideStop.JourneyRef`) when the two feeds agree, otherwise by the closest scheduled time on the same line.
- **Trip planner and future dates** (`Services/Trips`): `TripPlannerService` plans for today from `IRideScheduleSource` (Stride) plus live data. Stride holds no timetable after today, so a later date (up to `TripTime.MaxDaysAhead` = 60) goes to `IFutureScheduleSource` / `GtfsFeedScheduleSource` (`Services/Trips/Gtfs`), which downloads the official Ministry of Transport GTFS zip (about 155 MB, cached under `cache\`, refreshed daily) and builds an in-memory index per date (at most 2). Its stop ids are stop codes and nothing from it is ever live. Statuses `NoScheduleForDate` and `ScheduleUnavailable` come from the feed state.
- **Address search:** `IGeocoder` / `NominatimGeocoder` (`Services/Geocoding`) is triggered only by an explicit Search or Enter (1 request/s, 24 h in-memory cache, `AppConfig.AddressSearchEnabled`, base URL in `GeocoderBaseUrl`). Never call it from typing, timers or background work, never log the query, never put an address in `user_history.json`. A `SavedPlace` can be defined by `Latitude`/`Longitude`/`Address` instead of a stop code; `PlacePoint.Resolve` / `NearestStop` turn it into a point and a stop.
- **Secrets:** `ISecretProvider` (`Services/Secrets`) is `ChainedSecretProvider`: Infisical (`BUSPULSE_INFISICAL_*` environment variables; verified against the live project on 2026-11-10 with `InfisicalLiveProbeTests`) first, then `BUSPULSE_<NAME>` environment variables. Names are `GOOGLE_CLIENT_ID` and `GOOGLE_CLIENT_SECRET`.
- **Stops and location:** `StrideStopCatalog` downloads all stops for today (about 30k, paged because the API has no geo filter) on the 120 s `StrideBulk` client and caches them in `%LOCALAPPDATA%\BusPulse\cache`, serving the newest cache first and refreshing in the background. The normal Stride client (`TransitApi`) times out at 45 s because Stride can take 10-50 s per request, and both Stride clients go through `TransientRetryHandler` (`Infrastructure`; two retries of a GET on 500/502/503/504/408 or a dropped connection); `Geo.FindNearby` computes distances locally. Stride stop ids change per date, so `IStopCatalog.WithCurrentIdsAsync` resolves today's id by stop code (cached per day, stops with no service today are dropped) before any schedule lookup. `WindowsLocationService` uses `Windows.Devices.Geolocation`, which is why the TFM includes the Windows 10 SDK version.
- **External data is untrusted:** providers parse with strict `System.Text.Json` options, cap response size and string length, check coordinates against Israel and timestamps for plausibility. Keep new providers consistent with this, and record real responses as fixtures in `BusPulse.Tests/Fixtures` rather than inventing shapes.
- **Config storage:** `AppConfigStore` serializes `AppConfig` and encrypts it with DPAPI `CurrentUser` plus fixed entropy to `%LOCALAPPDATA%\BusPulse\settings.dpapi`; unmapped JSON members are rejected and `Validate()` runs on load and save.
- **Assets and sound:** `Assets/**` is copied to the output (`Content` items) and loaded from `AppContext.BaseDirectory`. `AlertPresenter` shows toasts (`ToastNotificationService`) and plays sounds (`SoundService`, only when not muted). Toast delivery for the unpackaged app is not verified on screen and needs an AUMID and Start Menu shortcut that do not exist yet.
- **Calendar:** `Services/Calendar` (Google OAuth, read-only, token DPAPI-encrypted) feeds `CalendarTripSuggester`; client id and secret come from `ISecretProvider` (`Services/Secrets`: Infisical via `BUSPULSE_INFISICAL_*` first, then `BUSPULSE_GOOGLE_CLIENT_ID` / `BUSPULSE_GOOGLE_CLIENT_SECRET`). Crashes are logged to `%LOCALAPPDATA%\BusPulse\crash.log`.
- **Tests:** view models are tested with fakes (`TestDoubles.cs` and per-file fakes) and a fixed `IClock`; providers over recorded fixtures. Live probes carry `[Trait("Category","Live")]` and are excluded from CI.
