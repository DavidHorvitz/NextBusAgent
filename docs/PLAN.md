# BusPulse Plan

Single source of truth for scope, decisions, status and next steps. It merges the original agent brief, the follow-up brief and the four-workstream execution plan. Status is as of 2026-10-20 (`dotnet build`: 0 errors; 74 unit tests and 2 live probes pass). Supporting documents: [audit.md](audit.md) (requirement-by-requirement audit), [live-data-probe.md](live-data-probe.md) (measured provider behavior), [assets.md](assets.md) (icon and audio specs), [../SECURITY.md](../SECURITY.md).

## 1. Product

BusPulse is a WPF desktop app (.NET 8, Windows 10+) that lives in the notification area and shows Israeli public-transport arrivals, so the user does not need a phone during work. All user-facing text is Hebrew with right-to-left layout (English is supported); code, comments, logs and commit messages are English.

Non-negotiables: **secure**, **interactive**, **real live data**. Nothing scheduled is ever shown as live; when no real source has data, the UI says so.

## 2. Decisions already made

- **TLS:** use the OS default (TLS 1.2 or later), certificate validation always on. The forced TLS 1.3 was removed because the Stride API only speaks TLS 1.2. Done.
- **CI:** GitHub-hosted `windows-latest`. No self-hosted runner. Done.
- **Primary live source:** Curlbus (unofficial, no SLA). The public Stride SIRI feed is stale (last real snapshot 2026-03-19, with impossible dates such as 2036/2038) and is not used as live data. Stride GTFS static data is current and serves as the schedule fallback.
- **Israel Railways:** keep the provider disabled until an official API key is supplied through configuration (`RailApiKey`). The key embedded in rail.co.il's public JavaScript, used by the `israel-rail-api` package, is deliberately not used.
- **Location:** Windows Location API only. No IP-based fallback (it would send the user's IP to a third party). The user can anchor the search on a saved place instead.
- **Installers:** local installers such as `docs/Claude Setup.exe` are git-ignored.
- **Ministry of Transport SIRI-SM:** implement behind configuration only, disabled by default. Access needs an approved application and a static-IP whitelist, so it realistically needs a small proxy server.

## 3. Working rules

- Keep the app building and runnable after every step. Small, focused commits. After every change: `dotnet build`, and `dotnet test` before committing.
- Never delete working functionality without replacing it. Never fake data.
- When unsure about an external API's shape, call it and inspect the real response first; save samples as test fixtures.
- All network and IO is async with `CancellationToken`, timeouts and `ILogger` logging. Nothing blocks the UI thread.
- No secrets in code, config in the repo, logs or exceptions (`dotnet user-secrets` for development; DPAPI for anything the user enters at runtime).

## 4. Status by phase

Legend: Done, Partial, Open.

### Phase 1: Architecture (MVVM)

| Item | Status | Notes |
| --- | --- | --- |
| Views and ViewModels: Shell, Stops, TripPlanner, Lines, Settings | Done | CommunityToolkit.Mvvm; `MainWindow.xaml.cs` is wiring only. Lines is an empty state |
| Generic Host DI, configuration, logging, `IHttpClientFactory` | Done | `Program.cs` |
| Interfaces: `ILocationService`, `IStopCatalog` (planned name `IStaticGtfsService`), `ILiveArrivalsProvider`, `ILiveDataAggregator`, `ISettingsService`, `IClock`, `IDispatcher`, `ILocalizer`, `IScheduleProvider` | Done | |
| `ITrainProvider`, `ITripPlannerService`, `IDisruptionService`, `IHistoryService` | Open | `RailProvider` implements `ILiveArrivalsProvider` for now |
| Single UI-thread marshalling abstraction | Done | `IDispatcher` |

### Phase 2: Live data

| Item | Status | Notes |
| --- | --- | --- |
| Probe results documented | Done | `live-data-probe.md` |
| `CurlbusArrivalsProvider` (cache 25 s, 1 req/s spacing, descriptive User-Agent, circuit breaker, strict validation) | Done | |
| `RailProvider` | Partial | Disabled stub; real client needs an official key |
| `StrideSiriProvider` (use only if newest snapshot is under 5 minutes old) | Open | Not built; the feed is currently stale |
| `MotSiriProvider` (config only) and `docs/live-data.md` | Open | |
| Schedule fallback from Stride GTFS | Partial | Per-stop schedule works; stop catalog cached daily; schedule rows themselves are not cached |
| `LiveDataAggregator` (freshest valid per line, priority tie-break, schedule merge, rejects invalid data, every ETA carries `Source`, `IsLive`, `ObservedAt`) | Done | |
| Poll 15-30 s only while the view is visible; pause when minimized or idle/locked | Partial | 25 s while the Stops tab is visible and the window is not minimized; idle/lock detection open |
| Connection probe with backoff (max about 5 attempts) from Settings, per-provider status | Partial | Single probe per provider, no retry loop yet |
| Debug logging of empty/malformed responses and UI updates | Partial | Provider side done; UI-update logging open |
| Disruptions (`IDisruptionService`; Israel Railways general updates; empty road provider) | Open | |

### Phase 3: Security

| Item | Status | Notes |
| --- | --- | --- |
| HTTPS only, OS-default TLS, validation on, one client per provider, timeouts, size caps | Done | |
| No secrets in repo; DPAPI for runtime secrets; user-secrets for development | Done | Settings file is DPAPI-encrypted |
| Strict typed parsing, range validation, length caps | Done | |
| Privacy: local-only data, redacted coordinates, history cap, "clear history" | Partial | No history store exists yet; location is never stored or logged |
| Dependencies pinned; vulnerable-package check in CI | Done | Fixed a critical transitive `System.Drawing.Common 4.7.0` |
| `Microsoft.Extensions.Http.Resilience` (Polly) | Open | Curlbus has its own breaker; adopt Polly for the rest |
| Background-loop exceptions caught, logged and shown as UI state | Partial | Stops tab does; poller logs only |
| Toasts contain only line, stop and time | Open | Toast delivery not wired |
| `SECURITY.md` | Done | |
| gitleaks in CI | Done | Workflow written; first run on GitHub pending |

### Phase 4: Features and interactivity

General rules for every tab: in-place updates without flicker, ETAs counting down between polls, instant feedback (under 100 ms), debounced search (about 250 ms) with keyboard navigation, tooltips with source and "updated N seconds ago", context menu ("add to favorites", "show line", "plan route from here"), favorites pinned first, shortcuts, tray mode.

| Item | Status | Notes |
| --- | --- | --- |
| Shortcuts: `Ctrl+1..4`, `F5`, `Ctrl+,` | Done | `Ctrl+F` focus search open |
| **Stops tab:** loads on entry, sorted by distance, name/code/distance only, expands to arrivals with "live"/"scheduled" badge, source and freshness tooltip, countdown in place, stale data greyed after 2 minutes | Done | |
| Location accuracy shown, saved-place override, how-to message when permission is off | Done | |
| Address search, "approximate location" label | Open | IP fallback intentionally excluded |
| Debounced search, favorites, context menu | Open | |
| **Trip planner:** origin/destination/line check against the aggregator | Partial | Open: "my location" default and swap button, Home/Work quick buttons, history suggestions, time selector (now / depart at / arrive by) with wheel picker, regional filtering with operator and endpoints, direction arrow, time-to-leave with optional toast |
| **Lines tab:** line and direction picker, ordered stops, live vehicles between stops | Open | Empty state only |
| **Settings:** places, provider status | Partial | Open: Home/Work, polling interval, notification lead time, theme, favorites, clear history |
| Tray tooltip with next favorite departure; optional always-on-top mini window | Open | Tray icon states (active/muted/alert) and mute are done |

### Phase 5: Design

Target Windows 11 Fluent with WPF-UI: Mica, NavigationView, right-side navigation in RTL, cards on a 4/8 px grid with 12 px radius, embedded Heebo or Rubik, all colors as light/dark tokens in `Theme.xaml` with one accent, status colors (green live, amber stale/scheduled, red disruption) always paired with an icon or text, operator-colored line pills, skeleton/empty/error/offline states for every view, 150-250 ms motion that respects "reduce animations", full RTL correctness, keyboard navigation, `AutomationProperties` names and contrast in both themes, plus screenshots of each tab in light and dark.

Status: Open, apart from basic RTL switching and automation names on some inputs. The current UI is plain WPF.

### Phase 6: CI, build and tests

| Item | Status |
| --- | --- |
| GitHub Actions on `windows-latest`: restore, build, unit tests excluding `Category=Live`, vulnerable-package check, gitleaks | Done (workflow committed; first GitHub run pending) |
| Provider tests from recorded fixtures (valid, malformed, stale, invalid-date) | Done for Curlbus and Stride schedule/stops |
| Aggregator priority, freshness and fallback tests | Done |
| ViewModel tests with mocked services and a fake `IClock` | Done for Shell, TripPlanner, Settings, Stops |
| Trip planning, time-to-leave, regional filtering tests | Open (features not built) |
| Live probes in `[Trait("Category","Live")]`, run manually | Done |

## 5. Workstreams (ownership and acceptance)

Each workstream is a branch and a reviewed pull request; shared contracts are agreed interfaces before parallel work; a human maintainer owns integration and release.

1. **Core engine and API.** Typed provider contracts replace provisional parsing; departure reminders from arrival time, walking duration and a configurable safety buffer; ETA-aware polling and inactive service windows; retry/backoff, cancellation, stale-data handling. Acceptance: contract tests over recorded responses; deterministic tests for buffer boundaries, stale/late data, schedule changes and polling intervals; malformed and oversized responses fail safely; cancellation stops requests promptly.
2. **Security and infrastructure.** Review DPAPI storage and file ACLs; decide whether credentials belong in Windows Credential Manager; threat-model feed parsing and logs; per-user installer without elevation; SBOM, dependency scanning, signing; evaluate a maintained, licensed obfuscator for release builds only. Acceptance: no secrets in source or build logs; tampered config fails closed with recoverable UX; install and uninstall without admin; signed, reproducible artifact; obfuscation must not break WPF resources, toast activation or diagnostics.
3. **UI/UX and localization.** Route/profile workflows, tray lifecycle, toast identity (AUMID and Start Menu shortcut registered per user, no elevation), accessible notification behavior, Hebrew-first RTL across the whole shell. Acceptance: starts in tray, tray menu and double-click work, language changes without restart, correct Hebrew reading order, notifications respect mute, profile and time-window state.
4. **QA, testing and performance.** xUnit plus WireMock.Net for HTTP success, timeout, transport, status, malformed, oversized and retry cases; DPAPI on supported Windows runners; audit subscriptions, timers and disposal; profile idle and active memory and CPU; offline recovery and repeated wake/sleep. Acceptance: suite passes on supported Windows versions; no unbounded response or retry behavior; idle CPU about zero between scheduled work; the working-set target (under 20 MB) is measured, not assumed.

Integration order: confirm schemas against reachable endpoints (done), replace provisional parsing with typed contracts (done for Curlbus and Stride), wire UI workflows to the contracts, security review of provider, config, toast registration and installer paths, then the full QA matrix and resource measurements before a signed release.

## 6. Next steps (in order)

1. Run the new workflow on GitHub and fix anything the hosted runner surfaces (gitleaks, vulnerable-package check).
2. Trip planner: time-to-leave (live ETA minus walking time), regional filtering, direction display, time selector, Home/Work, swap; toast notification before departure. Tests first.
3. Lines tab with ordered stops and vehicle positions from Curlbus line endpoints.
4. Settings: Home/Work, polling interval, notification lead time, theme, clear history; add the history store with a size cap.
5. Replace the single connection probe with retry plus exponential backoff, adopt `Microsoft.Extensions.Http.Resilience`, and pause polling when the machine is idle or locked.
6. Rail: obtain an official key, then implement the client and the general-updates disruptions provider; add `StrideSiriProvider` (only when the newest snapshot is under 5 minutes old) and the config-only `MotSiriProvider` with `docs/live-data.md`.
7. Design pass (Phase 5): WPF-UI, `Theme.xaml` tokens, Hebrew font, states, motion, accessibility, screenshots.
8. Packaging: per-user installer, AUMID and Start Menu shortcut for toasts, signing, .NET 8 end-of-support (2026-11-10) upgrade plan.

## 7. Definition of done

- `dotnet build`: 0 errors and no new warnings; `dotnet test`: 100% of unit tests pass; CI green.
- `live-data-probe.md` shows real probe results; at least one live bus provider returns current data (Curlbus does).
- Every ETA in the UI shows its source and freshness; nothing scheduled is shown as live.
- No secrets in the repo (secret scan clean); `SECURITY.md` current.
- Every requirement is Done or explicitly deferred with a reason.
- App launched and left running for a live check.

## 8. Final report format

1. Updated audit table (before and after).
2. Live-data probe results per provider.
3. What changed, by area, with key files.
4. Screenshots (light and dark, each tab).
5. Deferred items, known limitations (unofficial sources may break, location accuracy on desktops without GPS, data lag) and anything needed from the maintainer (Israel Railways key).
