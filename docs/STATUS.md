# BusPulse status (2026-11-03)

Start with [HANDOFF.md](HANDOFF.md) for the current handoff. Snapshot of what works, what was verified and how, what is still open, and known edge cases. [PLAN.md](PLAN.md) holds scope and decisions; this file holds the current state.

## Verified

| Area | Evidence |
| --- | --- |
| Build | `dotnet build BusPulse.sln`: 0 errors |
| Unit tests | 255 passing (`dotnet test --filter "Category!=Live"`) |
| Live probes | 3 passing (`Category=Live`): Curlbus stop 3490, the Stride stop list, and the end-to-end aggregator probe (Curlbus + Stride schedule, stop 3490). The stop list probe needed more than 30 s on a slow day, so the probe timeout was raised |
| App start | Smoke launch after the latest changes: the window opens and stays up (14 s, no crash events); dependency injection resolves and XAML loads |
| Stops tab in the running app | A stop was expanded and showed live and scheduled arrivals (2026-11-03) |
| Destination search by stop name | Typing the hospital name picked "המרכז הרפואי יצחק שמיר, באר יעקב" and planned (no direct trip from the test location) |
| Window close | Closing the window hides it to the tray; Exit in the tray ends the process |
| Themes | Dark and light themes checked on screen (`--theme=` launch flag); the app's own brushes follow a Windows theme switch while "system" is selected |
| Vulnerable packages | `dotnet list package --vulnerable --include-transitive` clean after pinning `System.Drawing.Common` 8.0.10 |

## Not verified

- **Not checked on screen:** toast appearance and delivery, the full-screen popup, the tray icon tooltip, the context menus (right-click on stops and lines), the time wheel, Hebrew RTL on every tab, and the "From your calendar" card with a real Google account (the OAuth flow has never run against Google). These need a person at the screen. Do not treat the RTL work as verified.
- **GitHub Actions** has not run yet. The workflow is committed (`.github/workflows/build-test.yml`).

## Feature status

| Feature | Status | Notes |
| --- | --- | --- |
| Tray icon, mute, language and profile menu | Done | Localized (en/he); tooltip shows the next departure of the selected route |
| Stops tab (nearby, expand, live/scheduled badges) | Done | Expanded in the running app: live and scheduled arrivals shown. The inline pin star sits on the stop line next to the distance |
| Trip planner: current location as origin, destination, swap, Home/Work, search with history | Done | Origin is always the current location |
| Trip planner: now / depart at / arrive by, wheel time picker | Done | The picker is not visually verified |
| Direct trips and regional filtering | Done | Only direct trips; no transfers (see live findings) |
| Destination search by stop name | Done | Matches saved places first, then local stop names (names starting with the text first, one entry per name and city); no geocoding service. "Find trips" with typed text and no pick uses the best match |
| Calendar suggestions on the trip planner | Done (untested against Google) | "From your calendar" card lists events whose location matches a saved place; "Plan this trip" sets destination, arrive-by and time. Unit-tested only |
| Theme picker, WPF-UI design pass | Done | `FluentWindow` with Mica, `IThemeService`, brand header, theme-aware cards; system/light/dark |
| Window close | Done | Hides to the tray; Exit in the tray ends the app |
| Time to leave with walking and waiting | Done | `Walking.Estimate` uses a fixed 1.25 m/s with a 25% detour factor |
| Smart alerts (upcoming, soon, leave now, popup, late, missed) | Done | Logic tested; popup and toast presentation not visually verified |
| Full-screen red popup | Done (untested visually) | Stays until dismissed; shown once per stage |
| Settings: language, lead time, refresh interval, notifications on/off, test notification, Home/Work, history clear | Done | |
| Lines tab | Done (schedule only) | Pick a line, an origin and a direction; stops listed in calling order with your stop marked |
| Live vehicle positions on the line | **Not available** | No connected source provides them; the Lines tab says times come from the schedule |
| Favorites (pin stops and lines) | Done | Stops: pinned first with a star. Lines: quick buttons. Stored in the config, not a separate favorites.json |
| Keyboard shortcuts | Done | `Ctrl+1..4`, `Ctrl+,`, `F5`, and `Ctrl+F` (opens the planner and focuses the destination search) |
| Rail provider | Disabled | Needs an official key; see [live-data-probe.md](live-data-probe.md) |
| Notification delivery on Windows | Wired, unverified | `AlertPresenter` shows toasts and plays sounds; delivery for an unpackaged app depends on AUMID / Start Menu shortcut registration, which is not built. Not seen on screen |

## Code review outcome

A review of `ed3e254..314215b` produced findings that were verified one by one and fixed (commits `09031f4`, `80bd4b5`, `c444e72`, with regression tests that fail without the fix):

- HTTP timeouts no longer crash the planner, Stops or Lines commands; they show the failure text, and ticker loops survive a failed refresh.
- Closing the window hides it to the tray instead of leaving a headless process.
- Rebuilding origin/destination lists no longer pushes null selections; "plan from here" adds its stop choice.
- A "missed" alert for an old ride no longer clears a newly selected ride (alert race).
- Curlbus re-checks its cache and open circuit after waiting for the gate; failures are cached per stop and the breaker needs three different failing stops.
- Trip option rows follow a language change.
- Calendar connect errors (listener start, non-JSON token replies, DPAPI errors) map to "failed".
- Arrive-by searches the 3 hours before the target, not from now.
- Ride batching: candidate rides are checked in batches of 80; destination rows are time-ordered, up to 500.
- The watched ride is kept past its scheduled time and always gets live data (`WatchRideId`).
- A slow re-plan is cut off after 8 s (the reminder waits at most 3 s before alerting from the last known times).
- The app's brushes follow a Windows theme switch while "system" is selected.
- The stop list is served from the newest cache (up to 30 days) after midnight while today's list downloads in the background; an empty or failed refresh keeps the previous list.

## Second code review

A review of `314215b..54cc36b` produced 8 findings, all fixed and covered by unit tests (271 unit tests and 4 live probes pass). Mutation checks confirmed that the new tests for findings 1, 2 and 6 fail without the fixes.

1. **Stop ids from an earlier day's cached stop list: fixed.** Stride stop ids change per date. `IStopCatalog.WithCurrentIdsAsync` (`StrideStopCatalog`) resolves today's id by stop code (`gtfs_stops/list?code=..&date_from=today&date_to=today`), caches it per day and drops stops with no service today. Used by `TripPlannerService`, `LinesViewModel` and `StrideScheduleProvider`.
2. **A watched ride that already left could take the next bus's live ETA: fixed.** `Arrival` now carries `TripRef` (Curlbus `trip_id`) and `RideStop` carries `JourneyRef` (Stride `gtfs_ride__journey_ref`). Per origin stop: if any live trip id equals a Stride journey ref, only equal trips are paired; otherwise each live arrival goes to the same-line departure closest in scheduled time (order-independent).
3. **Calendar horizon: fixed.** Suggestions are limited to events less than 24 h ahead (was 48 h), so the time of day resolves to the event's day.
4. **Stale destination suggestions: fixed.** "Find trips" always recomputes the suggestions before auto-picking.
5. **Late periodic refresh overwriting a newer plan: fixed.** `RunPlanAsync` checks the request reference before applying a result.
6. **Arrive-by hidden by the 100-row limit: fixed.** Arrive-by asks Stride for the latest departures first (`order_by=arrival_time desc`). `IRideScheduleSource.GetStopDeparturesAsync` got a `latestFirst` parameter.
7. **Stale re-plan after a watch change: fixed.** Watching another ride or cancelling cancels the in-flight re-plan (per-watch `CancellationTokenSource` in `DepartureReminderService`).
8. **Stop-name search: fixed.** It uses `OrdinalIgnoreCase`.

## Live findings (2026-11-03)

- **Stride was slow:** every page of the stop list took 17-51 s and small queries 10-25 s (10-50 s per request overall). The old 12 s timeout failed every request. The Stride client now has a 45 s timeout and a separate 120 s `StrideBulk` client for the stop list. Schedule lookups take the stop id from the loaded stop list, and the planner queries origin stops in parallel.
- **Curlbus returns HTTP 500 for some stop codes** (for example 4237). Failures are cached per stop, so one bad stop no longer disables the source.
- **Renamed hospital:** the old saved name "אסף הרופא" does not match the hospital stops. The current name is "המרכז הרפואי יצחק שמיר" (Be'er Ya'akov).
- **Direct trips only:** the planner has no transfers, which is why a trip from Ramat Gan to Be'er Ya'akov returned none.

## Findings from the earlier round

- **Curlbus line endpoint:** `https://curlbus.app/dan/4` returns route alternatives, but `/dan/4/<index>` and `/dan/4/<name>` return HTTP 500 from the server (checked for indexes 0-2). No line-level vehicle data is available from this source, so the Lines tab falls back to the schedule when it is built.
- **Edge cases tested** (16 tests in `EdgeCaseTests.cs`): exactly 2 minutes, lead time 0, zero walk and wait, departure instant, stage reset, antipodal distance, zero radius, malformed and wrong-typed Curlbus and Stride payloads, a visit with no line name, oversized history names, and config bounds and references.

## Known issues

Fixed in this round: the daylight-saving target time (now `TripTime`, tested for every day of 2026), the "1 min walk" text for walks under 30 seconds, and the discarded settings-save result (now reported in the Settings tab).

Still open:

1. **Missed bus after a route is cancelled:** cancelling a route removes the watch; re-selecting the same route can show the popup again for that stage (intended, but worth knowing).
2. **Stop catalog on midnight rollover:** fixed. The newest cache from the last 30 days is served at once and today's list downloads in the background; only a first run waits.
3. **Favorites storage:** the brief asked for `favorites.json`; favorites live in the already-validated config instead, so there is one settings file.
4. **Lines tab uses one ride per direction:** the stop list comes from the first matching ride; a line with several route patterns shows one per distinct destination.

## Next steps

1. Click through the app with a person at the screen: toasts, popup, tray tooltip, context menus, RTL, time wheel. Record the results here.
2. Connect a real Google account and check the calendar card (needs the OAuth client from HANDOFF.md).
3. Add transfers to the planner, or state clearly in the UI that only direct trips are searched.
4. Decide on a live vehicle source (Curlbus does not expose one), or keep the Lines tab schedule-only.
5. Apply for official Ministry of Transport access (see PLAN.md).
6. AUMID and Start Menu shortcut for toasts, installer.
