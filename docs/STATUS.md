# BusPulse status (2026-10-20)

Snapshot of what works, what was verified and how, what is still open, and known edge cases. [PLAN.md](PLAN.md) holds scope and decisions; this file holds the current state.

## Verified

| Area | Evidence |
| --- | --- |
| Build | `dotnet build BusPulse.sln`: 0 errors |
| Unit tests | 171 passing (`dotnet test --filter "Category!=Live"`) |
| Live probes | Curlbus stop 3490 and the Stride stop list pass (`Category=Live`) |
| App start | Smoke launch: the window opens, dependency injection resolves, and the stop catalog downloads (about 30k stops, 5.7 MB cached) |
| Vulnerable packages | `dotnet list package --vulnerable --include-transitive` clean after pinning `System.Drawing.Common` 8.0.10 |

## Not verified

- **Visual checks were not done:** toasts, the full-screen popup, RTL layout, the tray icon and tooltip, and the time wheel. These need a person at the screen. Do not treat the RTL work as verified.
- **GitHub Actions** has not run yet. The workflow is committed (`.github/workflows/build-test.yml`).
- **Stops tab** was not clicked through in the running app.

## Feature status

| Feature | Status | Notes |
| --- | --- | --- |
| Tray icon, mute, language and profile menu | Done | Localized (en/he); tooltip shows the next departure of the selected route |
| Stops tab (nearby, expand, live/scheduled badges) | Done | Not clicked through in the app |
| Trip planner: current location as origin, destination, swap, Home/Work, search with history | Done | Origin is always the current location |
| Trip planner: now / depart at / arrive by, wheel time picker | Done | The picker is not visually verified |
| Direct trips and regional filtering | Done | Only direct trips; no transfers |
| Time to leave with walking and waiting | Done | `Walking.Estimate` uses a fixed 1.25 m/s with a 25% detour factor |
| Smart alerts (upcoming, soon, leave now, popup, late, missed) | Done | Logic tested; popup and toast presentation not visually verified |
| Full-screen red popup | Done (untested visually) | Stays until dismissed; shown once per stage |
| Settings: language, lead time, refresh interval, notifications on/off, test notification, Home/Work, history clear | Done | |
| Lines tab | **Open** | Empty state only |
| Live vehicle positions on the line | **Open** | Depends on a live source for vehicles; see the Curlbus finding below |
| Favorites (pin stops and lines) | **Open** | `AppConfig` has the fields; no UI or ordering yet |
| Keyboard shortcuts | Partly | `Ctrl+1..4`, `Ctrl+,` and `F5` exist; `Ctrl+F` (focus search) is missing |
| Rail provider | Disabled | Needs an official key; see [live-data-probe.md](live-data-probe.md) |
| Notification delivery on Windows | Unverified | Toast XML is built; delivery depends on AUMID / Start Menu shortcut registration |

## Findings from this round

- **Curlbus line endpoint:** `https://curlbus.app/dan/4` returns route alternatives, but `/dan/4/<index>` and `/dan/4/<name>` return HTTP 500 from the server (checked for indexes 0-2). No line-level vehicle data is available from this source, so the Lines tab falls back to the schedule when it is built.
- **Edge cases tested** (16 tests in `EdgeCaseTests.cs`): exactly 2 minutes, lead time 0, zero walk and wait, departure instant, stage reset, antipodal distance, zero radius, malformed and wrong-typed Curlbus and Stride payloads, a visit with no line name, oversized history names, and config bounds and references.

## Known issues (found by reading the code, not yet fixed)

1. **DST day:** `TripPlannerViewModel.ResolveTarget` applies the current UTC offset to the chosen time. On a daylight-saving change day, a target time can be off by an hour.
2. **Zero walk text:** `WalkText` shows "1 min walk" when the walk is under 30 seconds, because it clamps to 1.
3. **Settings save:** language, lead time and notification changes call `SaveAsync()` without awaiting it, so a failed save is only logged.
4. **Missed bus after a route is cancelled:** cancelling a route removes the watch; if the user re-selects the same route, the popup can fire again for that stage (intended, but worth knowing).
5. **Stop catalog on midnight rollover:** the catalog is keyed by the Israel date, so the first request after midnight downloads the list again (about 2 MB). Intended, but it adds a download at a fixed time.

## Next steps

1. Click through the app with a person at the screen: toasts, popup, RTL, tray, time wheel. Record the results here.
2. Build the Lines tab from the Stride schedule (no live vehicles), then decide on a vehicle source.
3. Favorites: store in `AppConfig` (already validated) and add pin commands and ordering.
4. Add `Ctrl+F` to focus the destination search.
5. Fix known issues 1 and 2 and add tests for them.
