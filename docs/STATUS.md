# BusPulse status (2026-10-20)

Snapshot of what works, what was verified and how, what is still open, and known edge cases. [PLAN.md](PLAN.md) holds scope and decisions; this file holds the current state.

## Verified

| Area | Evidence |
| --- | --- |
| Build | `dotnet build BusPulse.sln`: 0 errors |
| Unit tests | 187 passing (`dotnet test --filter "Category!=Live"`) |
| Live probes | Curlbus stop 3490 and the Stride stop list pass (`Category=Live`) |
| App start | Smoke launch after the latest changes: the window opens and stays up (14 s, no crash events); dependency injection resolves and XAML loads |
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
| Lines tab | Done (schedule only) | Pick a line, an origin and a direction; stops listed in calling order with your stop marked |
| Live vehicle positions on the line | **Not available** | No connected source provides them; the Lines tab says times come from the schedule |
| Favorites (pin stops and lines) | Done | Stops: pinned first with a star. Lines: quick buttons. Stored in the config, not a separate favorites.json |
| Keyboard shortcuts | Done | `Ctrl+1..4`, `Ctrl+,`, `F5`, and `Ctrl+F` (opens the planner and focuses the destination search) |
| Rail provider | Disabled | Needs an official key; see [live-data-probe.md](live-data-probe.md) |
| Notification delivery on Windows | Unverified | Toast XML is built; delivery depends on AUMID / Start Menu shortcut registration |

## Findings from this round

- **Curlbus line endpoint:** `https://curlbus.app/dan/4` returns route alternatives, but `/dan/4/<index>` and `/dan/4/<name>` return HTTP 500 from the server (checked for indexes 0-2). No line-level vehicle data is available from this source, so the Lines tab falls back to the schedule when it is built.
- **Edge cases tested** (16 tests in `EdgeCaseTests.cs`): exactly 2 minutes, lead time 0, zero walk and wait, departure instant, stage reset, antipodal distance, zero radius, malformed and wrong-typed Curlbus and Stride payloads, a visit with no line name, oversized history names, and config bounds and references.

## Known issues

Fixed in this round: the daylight-saving target time (now `TripTime`, tested for every day of 2026), the "1 min walk" text for walks under 30 seconds, and the discarded settings-save result (now reported in the Settings tab).

Still open:

1. **Missed bus after a route is cancelled:** cancelling a route removes the watch; re-selecting the same route can show the popup again for that stage (intended, but worth knowing).
2. **Stop catalog on midnight rollover:** the catalog is keyed by the Israel date, so the first request after midnight downloads the list again (about 2 MB). Intended.
3. **Favorites storage:** the brief asked for `favorites.json`; favorites live in the already-validated config instead, so there is one settings file.
4. **No "clear favorites" button** in Settings yet.
5. **Lines tab uses one ride per direction:** the stop list comes from the first matching ride; a line with several route patterns shows one per distinct destination.

## Next steps

1. Click through the app with a person at the screen: toasts, popup, RTL, tray, time wheel, the new Lines tab and pin buttons. Record the results here.
2. Add a "Clear favorites" button to Settings.
3. Decide on a live vehicle source (Curlbus does not expose one), or keep the Lines tab schedule-only.
4. Apply for official Ministry of Transport access (see PLAN.md) so there is an official live source and a rail option.
5. Design pass (Fluent, theme tokens, dark mode) once behavior is confirmed.
