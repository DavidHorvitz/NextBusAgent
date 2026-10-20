# BusPulse Audit (Phase 0)

Audit of the repository against `docs/BusPulse_prompt.md`, dated 2026-10-20. Build: `dotnet build` = 0 errors, 19 warnings (all CA2007 `ConfigureAwait`). Tests: `BusPulse.Tests` added (14 xUnit tests, all passing) covering the Curlbus provider.

The brief describes files that do not exist in this repo (`LiveBusService`, `user_history.json`, tests, CI/runner files). The code is the small baseline described in `README.md`, not a partially built app.

## Live API findings (probed 2026-10-20)

| Endpoint | Result |
| --- | --- |
| `https://openbus-stride-api.hasadna.org.il` (host used by the code) | Does not resolve. **This was a bug; fixed** to `open-bus-stride-api.hasadna.org.il`. |
| `GET /gtfs_stops/list?date_from=…&date_to=…&city=…` | 200. Current data (stop date 2026-10-20). Fields: `id, date, code, lat, lon, name, city`. |
| `GET /gtfs_ride_stops/list?arrival_time_from=…&arrival_time_to=…` | 200. Current scheduled arrivals, with `gtfs_stop__*` and `gtfs_ride__*` fields. |
| `GET /siri_rides/list?limit=1` | 200, but the first rows are from 2023, and ordering by newest returns rows dated 2036. |
| `GET /siri_vehicle_locations/list` | 200, but newest snapshot is `2026/03/19` and some rows carry `recorded_at_time` in 2038. |
| `GET /siri_snapshots/list` | 500 (server-side validation error). |
| `GET /stops/list` | 404 (not an endpoint). |

**TLS (decision made in the updated brief: use OS defaults):** `open-bus-stride-api.hasadna.org.il` rejects TLS 1.3 (`openssl s_client -tls1_3` returns alert 70, protocol version) and only negotiates TLS 1.2. `Program.cs` pins the `TransitApi` handler to `SslProtocols.Tls13`, so every request will fail even with the corrected hostname. Options: allow TLS 1.2 or later for this one host (recommended; TLS 1.2 is still considered secure with modern ciphers), or keep TLS 1.3 and accept that the Stride API cannot be used. The updated brief decides this: remove the forced TLS 1.3. The edit to `Program.cs` was blocked by the automated permission check in the session that ran it, so the `TransitApi` client still pins TLS 1.3 until the maintainer applies it or approves the change. The new `Curlbus` client already uses OS defaults.

**Conclusion:** static GTFS (stops, scheduled arrivals) is current. Public SIRI live data is stale (about 7 months) and contains impossible timestamps. The UI must show "no live data, scheduled times only" instead of presenting these as live ETAs. The brief requires this honesty, so it is a product requirement, not a nice-to-have.

Also unverified: whether `siri_rides/list` accepts `stop_code` (the current code assumes it does). Use `gtfs_ride_stops` with `gtfs_stop__code` for stop-based arrivals instead.

## Requirements table

| # | Requirement | Status | Evidence | Notes |
| --- | --- | --- | --- | --- |
| 0 | Audit before changes | Done | this file | |
| 1.1 | Views + ViewModels (Shell, Stops, TripPlanner, Lines, Settings) | Missing | `MainWindow.xaml.cs` has all logic | Single window, code-behind only |
| 1.2 | CommunityToolkit.Mvvm | Missing | `BusPulse.csproj` | Not referenced |
| 1.2 | Generic Host DI/config/logging | Partial | `Program.cs` | Host exists; services are concrete classes, no interfaces |
| 1.3 | Service interfaces (`ILocationService`, `IStopsService`, `ILiveBusService`, `ITripPlannerService`, `IDisruptionService`, `IHistoryService`, `IClock`) | Missing | `Services/` | Only `OpenBusService`, `ToastNotificationService`, `SoundService`, `SchedulePollingService` |
| 1.4 | Single UI-thread marshalling abstraction | Missing | `MainWindow.xaml.cs:61,80`, `App.xaml.cs:51` | Scattered `Dispatcher.Invoke` |
| 2.1 | Stride API (GTFS static only; SIRI is stale) | Broken, fixed | `OpenBusService.cs:9` | Hostname was wrong; now correct. Parsing is still provisional |
| 2.2 | Startup + Settings connection probe with backoff | Missing | | No retry or probe anywhere |
| 2.3 | `LiveBusService` polling 15-30 s, pause when minimized | Partial | `SchedulePollingService.cs` | 5-min fixed tier; no minimize pause; no vehicle position/speed/ETA |
| 2.4 | Freshness timestamps and stale greying | Missing | | |
| 2.5 | Debug logging of empty/malformed responses | Partial | `SchedulePollingService.cs` | Logs record count and failures; no UI-update logging |
| 2.6 | `IDisruptionService` pluggable | Missing | | |
| 2.7 | Local GTFS cache, daily refresh | Missing | | |
| 3.1 | Stops tab (nearby, distance, arrivals) | Missing | | Only manual stop-code entry |
| 3.1 | Windows Location API, manual override | Missing | csproj targets `net8.0-windows` | Needs `net8.0-windows10.0.19041.0` |
| 3.2 | Trip Planner tab | Missing | | A route picker (start/destination/line) exists, nothing computes a plan |
| 3.2 | Time-to-leave + toast | Partial | `ToastNotificationService.cs` | Builds XML only; nothing computes or delivers |
| 3.3 | Lines tab | Missing | | |
| 3.4 | Settings (Home/Work, interval, theme, probe) | Missing | | Only a language toggle and mute |
| 4.x | Fluent design (WPF-UI, Theme.xaml, Heebo/Rubik, states, motion, a11y) | Missing | `MainWindow.xaml` | Plain WPF; hard-coded English in tray menu |
| 4.x | RTL | Partial | `MainWindow.xaml.cs` `ApplyLanguage` | Runtime `FlowDirection` switch only; brief wants Hebrew-first |
| 5.1 | Self-hosted runner | Dropped | | Decision: use GitHub-hosted `windows-latest` instead |
| 6 | CI on `windows-latest` (build, tests, vulnerable check, gitleaks) | Missing | no `.github/` | |
| 6 | Unit tests: providers | Partial | `BusPulse.Tests/CurlbusArrivalsProviderTests.cs` | Curlbus done; rail, aggregator, ViewModels, planner pending |
| 6 | `[Trait("Category","Live")]` probes | Missing | | |
| 2.2 | CurlbusArrivalsProvider | Partial | `Services/LiveData/CurlbusArrivalsProvider.cs` | Parser, validation, cache, spacing, circuit breaker, tests. Not yet used by UI or aggregator |
| 2.2 | RailProvider | Blocked | `docs/live-data-probe.md` | Needs a decision on the rail subscription key |
| 2.3 | `ILiveDataAggregator` | Missing | | |
| 3 | `SECURITY.md`, DPAPI settings, secret scan | Partial | `AppConfigStore` | Settings are DPAPI-encrypted; `SECURITY.md` and gitleaks missing |
| - | Tray icons (active/muted/alert) and sounds | Done | `Assets/`, `tools/` | `bus_alert.ico` and `SoundService` are not yet used |

## Risks found

- Wrong API hostname (fixed). The README previously blamed the dev environment.
- `OpenBusService` matches line names by guessing field names against a response schema that was never verified; the real rows use different fields (`siri_route__line_ref`, etc.).
- Polling interval never adapts because no ETA is computed (always 5 min).
- `App.OnStartup` is `async void` and starts the host fire-and-forget; failures surface only as a message box.
- `MainWindow` code-behind mixes persistence, network calls and UI; hard to unit-test.
- 19 CA2007 warnings (no `ConfigureAwait`); a WPF app does not need it, so suppressing the rule in `.editorconfig` is reasonable rather than editing every await.
- Public SIRI data is stale and has impossible timestamps (see above).

## Ordered work plan

1. Fix hostname (done). Rebase the API layer on `gtfs_stops` and `gtfs_ride_stops`, with typed models from the real responses.
2. Add `BusPulse.Tests` (xUnit) with a fake `HttpMessageHandler`, then extract service interfaces and `IClock`.
3. MVVM split with CommunityToolkit.Mvvm, one ViewModel at a time, keeping the app runnable.
4. Stops tab (nearby + scheduled arrivals + honest "no live data"), then Trip Planner, Lines, Settings.
5. Fluent theme, Hebrew-first RTL, states.
6. CI workflow and self-hosted runner setup (needs the user's go-ahead, see the summary).
