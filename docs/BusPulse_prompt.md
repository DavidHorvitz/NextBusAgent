# BusPulse — Agent Brief (audit first, then build)

You are working on **BusPulse**, a WPF desktop app (.NET 8, Windows) in the repo `DavidHorvitz/NextBusAgent`.
It is a real-time Israeli public-transport monitor that lives on the desktop so the user doesn't need their phone during work.
All user-facing text is **Hebrew, RTL**. Code, comments, logs and commit messages are in English.

The three non-negotiables for this work: **secure**, **interactive**, and **real live data** (sources in Phase 2).

---

## Phase 0 — Audit what already exists

`docs/audit.md` already exists from a previous pass. **Update it** rather than starting over:

1. Re-read the solution, every `.csproj`, `MainWindow.xaml(.cs)`, services (`OpenBusService`, `Program.cs`), tests, CI files.
2. Run `dotnet build` and `dotnet test` and record the result.
3. Keep the audit table current for every requirement in Phases 1–6:

   | # | Requirement | Status (Done / Partial / Missing / Broken) | Evidence (file:line or test) | Notes |

   "Done" means it works end-to-end, not just that a class with the right name exists.
4. Then continue straight into the work. Skip what's Done; fix what's Broken before building on it.

### Decisions already made (do not ask again)
- **TLS:** remove the forced TLS 1.3. Use the OS default (`SslProtocols.None` / system default) so TLS 1.2+ works against every host. Do not disable certificate validation anywhere.
- **CI runner:** do **not** create a self-hosted runner. Use GitHub-hosted `windows-latest` (see Phase 6).

---

## Working rules

- Keep the app building and runnable after every step. Small, focused commits with clear messages.
- Never delete working functionality without replacing it. **Never fake data**: if a real source is unavailable, show an honest state in the UI.
- When unsure about an external API's shape, call it and inspect the real response before writing models. Save sample responses as test fixtures.
- All network/IO is `async`, with `CancellationToken`, timeouts and structured logging (`ILogger`). Nothing blocks the UI thread.

---

## Phase 1 — Architecture (clean MVVM)

- Split `MainWindow.xaml(.cs)` into Views + ViewModels: `ShellViewModel`, `StopsViewModel`, `TripPlannerViewModel`, `LinesViewModel`, `SettingsViewModel`.
- **CommunityToolkit.Mvvm** (`ObservableObject`, `[ObservableProperty]`, `[RelayCommand]`) + **Microsoft.Extensions.Hosting** for DI, configuration, logging, and `IHttpClientFactory`.
- `MainWindow.xaml.cs` ends up with almost no logic.
- Services behind interfaces: `ILocationService`, `IStaticGtfsService`, `ILiveArrivalsProvider` (multiple implementations), `ITrainProvider`, `ILiveDataAggregator`, `ITripPlannerService`, `IDisruptionService`, `IHistoryService`, `ISettingsService`, `IClock`.
- UI-thread marshalling in one place (a dispatcher abstraction), not scattered `Dispatcher.Invoke` calls.

---

## Phase 2 — Live data (multi-provider)

The Stride SIRI feed is stale (last snapshot 2026-03-19, with impossible dates), so build a **multi-provider live layer**.

### 2.1 Probe first, then build
Before writing models for any provider, probe it and write the results into `docs/live-data-probe.md`: endpoint, HTTP status, latency, a trimmed sample response, and **how fresh the timestamps actually are**. Report these results to me before building the full UI on top.

### 2.2 Providers (in priority order)

1. **CurlbusArrivalsProvider — buses, per-stop ETAs (primary).**
   - `https://curlbus.app/<stop_code>`; use the JSON variant described in the README of `github.com/elad661/curlbus` (check the Accept header / URL format there).
   - It is an unofficial community service with no SLA, so be a good citizen: cache each stop's response for 20–30s, query only stops currently visible in the UI, send a descriptive `User-Agent` (`BusPulse/<version> (+repo URL)`), max ~1 request/sec overall, exponential backoff on errors, and a circuit breaker that pauses the provider for a few minutes after repeated failures.
2. **RailProvider — Israel Railways departures + delays.**
   - Inspect how the Python package `israel-rail-api` (`github.com/sh0oki/israel-rail-api`) calls the rail API (endpoint, headers, any subscription key it uses) and port it to C#. Return departure/arrival times, platform and `departure_delay` in minutes.
   - It's unofficial and may break: wrap it in the same backoff/circuit-breaker logic and fall back to the GTFS rail schedule.
3. **StrideSiriProvider — fallback only.** At startup, check the newest snapshot; use it only if it's under 5 minutes old. Otherwise disable it for the session and log why.
4. **MotSiriProvider — official MOT SIRI-SM, disabled by default.** Implement behind config only (enabled when an endpoint + credentials are configured). Document in `docs/live-data.md` that access requires an approved application to the Ministry of Transport and a static-IP whitelist, so it realistically needs a small proxy server.
5. **Scheduled fallback** — GTFS static schedule from Stride (`gtfs_stops`, `gtfs_ride_stops`, routes) or the MOT GTFS zip. Cache locally and refresh daily.

### 2.3 Aggregation rules
- `ILiveDataAggregator` asks providers in priority order, takes the freshest valid result, and merges it with the schedule (so a line with no live data still shows its scheduled time).
- Every ETA carries: `Source` (Curlbus / Rail / Stride / Mot / Schedule), `IsLive`, `ObservedAt`.
- Reject obviously invalid data (timestamps in the future beyond a few minutes, years like 2036/2038, ETAs in the past) and log it.
- Poll every 15–30s only while a relevant view is visible; pause when minimized or when the machine is idle/locked.
- **Live probe:** on startup and from a "בדיקת חיבור" button in Settings, probe each provider (max ~5 attempts with exponential backoff, then report failure — no infinite loop). Show per-provider status in Settings.
- **Debug logging:** log empty/malformed responses and each UI update, so "data isn't arriving" can be traced end to end.

### 2.4 Disruptions (accidents, closures, protests)
Implement `IDisruptionService` as a pluggable provider. Use Israel Railways' general-updates endpoint (found alongside the rail API) for train disruptions if it's reachable. For roads, if no official/open source is accessible, ship the interface with an empty provider and document it — don't scrape or invent.

---

## Phase 3 — Security (required throughout, not a final pass)

- **Transport:** HTTPS only, certificate validation always on, OS-default TLS (1.2+). One `HttpClient` per provider via `IHttpClientFactory`, with explicit timeouts (≤10s) and a max response size.
- **Secrets:** no keys or tokens in code, config files in the repo, logs or exceptions. Use `dotnet user-secrets` for development; at runtime store any user-entered secret encrypted with **Windows DPAPI** (`ProtectedData`, CurrentUser scope). Add a secret-scanning check to CI (e.g. `gitleaks`) and make sure `.gitignore` covers local config.
- **Untrusted input:** treat every API response as untrusted — deserialize into strict typed models with `System.Text.Json`, validate ranges (coordinates inside Israel, sane times), cap string lengths, and never render response content as XAML/markup or execute anything from it.
- **Privacy:** location, Home/Work addresses and `user_history.json` are stored only locally under `%LOCALAPPDATA%\BusPulse`, never sent anywhere except the coordinates/stop codes needed for a query. Never put coordinates or addresses in logs at Information level; redact them. Settings has "נקה היסטוריה" (clear history) and the history has a size cap.
- **Dependencies:** pin NuGet versions, enable `<NuGetAudit>` / `dotnet list package --vulnerable` in CI, and keep the dependency list minimal.
- **Resilience:** use `Microsoft.Extensions.Http.Resilience` (Polly) for retry + circuit breaker + timeout so a failing provider can't freeze or crash the app. All exceptions in background loops are caught, logged and surfaced as a UI state.
- **Notifications:** toast notifications contain only line/stop/time, no addresses.
- Add a short `SECURITY.md` describing what data is stored, where, and which external hosts the app contacts.

---

## Phase 4 — Features & interactivity

General interactivity rules (apply to every tab):
- Data updates **in place** — no list rebuilds, no scroll jumps, no flicker. ETAs count down live between polls.
- Instant feedback: every click shows a pressed/loading state within 100ms; long operations are cancellable.
- Search boxes filter as you type (debounced ~250ms) with keyboard navigation (↑/↓/Enter/Esc).
- Hover on a line/ETA shows a tooltip with source and "עודכן לפני X שניות".
- Right-click / context menu on a stop or line: "הוסף למועדפים", "הצג קו", "תכנן מסלול מכאן".
- **Favorites:** pin stops and lines; favorites appear at the top of their tabs.
- **Keyboard shortcuts:** `Ctrl+1/2/3` switch tabs, `Ctrl+F` focus search, `F5` refresh, `Ctrl+,` settings.
- **Tray mode:** minimize to system tray; tray tooltip shows the next favorite departure ("קו 1 בעוד 4 דק׳"); optional compact always-on-top mini window for one favorite stop.

### Stops tab
- Auto-load nearby stops on entering the tab (no search button), sorted by distance.
- Each row: stop name, stop code, distance ("180 מ׳"). **No raw lat/long.**
- Expanding a stop shows the next arrivals: line badge, destination, ETA, and a **"חי" / "מתוכנן"** badge.
- **Location:** Windows Location API (`Windows.Devices.Geolocation`; target `net8.0-windows10.0.19041.0`) with high accuracy, show the accuracy radius, let the user set/override location manually (address search or Home/Work). IP location only as a last resort, labeled "מיקום משוער". If Windows location permission is off, show how to enable it.

### Trip Planner tab
- Origin defaults to "המיקום הנוכחי שלי"; a swap button switches origin/destination.
- Destination: quick buttons "בית" / "עבודה" + search with suggestions from `user_history.json`.
- Time: default **"עכשיו"** (live); toggle to **"יציאה ב-"** / **"הגעה עד"** with a smooth wheel-style time picker (mouse wheel + drag + keyboard).
- Optional preferred line. Empty → fastest options across buses and trains, each showing whether its times are live or scheduled.
- **Regional filtering:** only lines serving stops near origin/destination (no confusing 167 Bnei Brak with 167 Haifa). Always show operator and endpoints.
- Explicit direction: "בני ברק ◄ תל אביב" with an arrow that animates to the direction of travel.
- **Time-to-leave:** "צא בעוד 7 דק׳" = live ETA − walking time to the stop, recalculated on every update, with an optional toast notification N minutes before (configurable).

### Lines tab
- Pick a line (with region/operator disambiguation) and direction.
- Full ordered stop list for that direction; click a stop to set it as the target.
- Live vehicles drawn between the stops they're traveling between, with progress and ETA to the selected stop. If no live source has data for the line, say so clearly and show the schedule.

### Settings
- Home/Work, polling interval, notification lead time, theme (light/dark/system), favorites management, clear history, and per-provider connection status with the "בדיקת חיבור" button.

---

## Phase 5 — Design (this must actually look good)

Target a modern **Windows 11 Fluent** look with **WPF-UI (lepoco/wpfui)**: Mica background, NavigationView, Fluent controls.

- **Layout:** navigation on the side (right side in RTL) with icons + labels. Content in cards on a 4/8px grid, 12px corner radius, generous padding. Minimum window ~420×640; works well wide too.
- **Typography:** embedded Hebrew-friendly font (**Heebo** or **Rubik**; fallback Segoe UI). ETA numbers large and bold; secondary info muted.
- **Color:** all colors as theme tokens in one `Theme.xaml` with full light and dark variants. One accent color. Status: green = live/on time, amber = delayed/stale/scheduled-only, red = disruption/cancelled. Never color alone — always pair with icon or text.
- **Line badges:** rounded pill with the line number, colored by operator. Live badge has a subtle pulsing dot.
- **States for every view:** skeleton loaders, empty state with a friendly message and next action, error state with retry, offline/stale banner.
- **Motion:** subtle and fast (150–250ms): fade/slide on list changes, smooth arrow rotation, ETA countdown. Respect Windows "reduce animations".
- **RTL correctness:** `FlowDirection="RightToLeft"` at the root; check icons/arrows that must not mirror, numbers, times, and mixed Hebrew/English.
- **Accessibility:** full keyboard navigation, visible focus, `AutomationProperties` names, good contrast in both themes.
- When done, take screenshots of each tab in light and dark themes for the final report.

---

## Phase 6 — CI, build & tests

- **GitHub Actions on `windows-latest`** (no self-hosted runner): restore, build, run unit tests (excluding `Category=Live`), vulnerable-package check, and secret scan. Trigger on push and PR.
- Create a test project (xUnit) covering:
  - every ViewModel, with mocked services and a fake `IClock`;
  - trip planning, time-to-leave, regional filtering;
  - each live provider, parsed from recorded fixture responses (including malformed/stale/invalid-date samples);
  - the aggregator's priority/freshness/fallback rules.
- Live probes go in `[Trait("Category","Live")]` and run manually, not in CI.

---

## Definition of done

- `dotnet build`: 0 errors, no new warnings. `dotnet test`: 100% of unit tests pass. CI green.
- `docs/live-data-probe.md` shows real probe results; at least one live bus provider returns current data, or the doc explains exactly why not.
- Every ETA in the UI shows its source and freshness; nothing scheduled is shown as live.
- No secrets in the repo (secret scan clean); `SECURITY.md` written.
- Every requirement in the audit table is Done or explicitly deferred with a reason.
- App launched and left running for a live check.

## Final report

1. Updated audit table (before → after).
2. Live-data probe results per provider.
3. What changed, by area, with key files.
4. Screenshots (light + dark, each tab).
5. Deferred items, known limitations (unofficial sources may break, location accuracy on desktops, data lag), and anything needed from me.
