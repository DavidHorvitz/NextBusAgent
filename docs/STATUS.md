# BusPulse status (2026-11-07)

Start with [HANDOFF.md](HANDOFF.md) for the current handoff. Snapshot of what works, what was verified and how, what is still open, and known edge cases. [PLAN.md](PLAN.md) holds scope and decisions; this file holds the current state.

## Verified

| Area | Evidence |
| --- | --- |
| Build | `dotnet build BusPulse.sln`: 0 errors (2026-11-03); the .NET 10 move on 2026-11-07 is described below |
| Unit tests | 514 passing on net10.0 as of 2026-11-07 (`dotnet test --filter "Category!=Live"`); not re-run while writing this update |
| Live probes | 3 passing (`Category=Live`): Curlbus stop 3490, the Stride stop list, and the end-to-end aggregator probe (Curlbus + Stride schedule, stop 3490). The stop list probe needed more than 30 s on a slow day, so the probe timeout was raised |
| App start | Smoke launch after the latest changes: the window opens and stays up (14 s, no crash events); dependency injection resolves and XAML loads |
| Stops tab in the running app | A stop was expanded and showed live and scheduled arrivals (2026-11-03) |
| Destination search by stop name | Typing the hospital name picked "המרכז הרפואי יצחק שמיר, באר יעקב" and planned (no direct trip from the test location) |
| Window close | Closing the window hides it to the tray; Exit in the tray ends the process |
| Themes | Dark and light themes checked on screen (`--theme=` launch flag); the app's own brushes follow a Windows theme switch while "system" is selected |
| Vulnerable packages | `dotnet list package --vulnerable --include-transitive` was clean on 2026-11-03; `System.Drawing.Common` is no longer a package reference on .NET 10. Not re-run for 2026-11-07 |

## Not verified

- **Superseded by the 2026-11-07 section below:** the popup and toasts were since seen on screen. **Not checked on screen (as of 2026-11-03):** toast appearance and delivery, the full-screen popup, the tray icon tooltip, the context menus (right-click on stops and lines), the time wheel, Hebrew RTL on every tab, and the "From your calendar" card with a real Google account (the OAuth flow has never run against Google). These need a person at the screen. Do not treat the RTL work as verified.
- **GitHub Actions** has not run yet. The workflow is committed (`.github/workflows/build-test.yml`).

## Feature status

| Feature | Status | Notes |
| --- | --- | --- |
| Tray icon, mute, language and profile menu | Done | Localized (en/he); tooltip shows the next departure of the selected route |
| Stops tab (nearby, expand, live/scheduled badges) | Done | Expanded in the running app: live and scheduled arrivals shown. The inline pin star sits on the stop line next to the distance |
| Trip planner: origin (current location, saved place or searched address), destination, swap, Home/Work, search with history | Done | Origin search and address search added 2026-11-07 |
| Trip planner: now / depart at / arrive by, wheel time picker, date selector (up to 60 days) | Done | The picker is not visually verified; dates after today come from the Ministry of Transport feed |
| Direct trips and regional filtering | Done | Only direct trips; no transfers (see live findings) |
| Destination search by stop name | Done | Matches saved places first, then local stop names (names starting with the text first, one entry per name and city). Address search is a separate, explicit button (Nominatim, can be switched off). "Find trips" with typed text and no pick uses the best match |
| Calendar suggestions on the trip planner | Done (untested against Google) | "From your calendar" card lists events whose location matches a saved place; "Plan this trip" sets destination, arrive-by and time. Unit-tested only |
| Theme picker, WPF-UI design pass | Done | `FluentWindow` with Mica, `IThemeService`, brand header, theme-aware cards; system/light/dark |
| Window close | Done | Hides to the tray; Exit in the tray ends the app |
| Time to leave with walking and waiting | Done | `Walking.Estimate` uses a fixed 1.25 m/s with a 25% detour factor |
| Smart alerts (upcoming, soon, leave now, popup, late, missed) | Done | Logic tested; the popup and the upcoming-stage toast were seen on screen on 2026-11-07, the amber stages were not |
| Full-screen red popup | Done | Stays until dismissed; shown once per stage; seen on screen 2026-11-07 |
| Settings: language, lead time, refresh interval, notifications on/off, test notification, Home/Work, places by address or stop code, address-search switch, secrets-source line, history clear | Done | Default lead time is 10 minutes since 2026-11-07 |
| Lines tab | Done (schedule only) | Pick a line, an origin and a direction; stops listed in calling order with your stop marked |
| Live vehicle positions on the line | **Not available** | No connected source provides them; the Lines tab says times come from the schedule |
| Favorites (pin stops and lines) | Done | Stops: pinned first with a star. Lines: quick buttons. Stored in the config, not a separate favorites.json |
| Keyboard shortcuts | Done | `Ctrl+1..4`, `Ctrl+,`, `F5`, and `Ctrl+F` (opens the planner and focuses the destination search) |
| Rail provider | Disabled | Needs an official key; see [live-data-probe.md](live-data-probe.md) |
| Notification delivery on Windows | Wired, partly seen | `AlertPresenter` shows toasts and plays sounds. Toasts appeared on this machine on 2026-11-07; delivery for an unpackaged app on a clean machine still depends on AUMID / Start Menu shortcut registration, which is not built |

## 2026-11-07: .NET 10, address search, future dates

All of this is in local commits (`git log --oneline a136d63..HEAD`); nothing is pushed.

What changed:

- **.NET 10 (LTS to 2028-11-14).** `net10.0-windows10.0.19041.0`, `Microsoft.Extensions.*` 10.0.14, xunit 2.9.3. The `System.Drawing.Common` and `ProtectedData` package references are gone (both are in the framework now). .NET 11 was still a release candidate.
- **Infisical secret provider** (`Services/Secrets`). Environment variables `BUSPULSE_INFISICAL_CLIENT_ID`, `_CLIENT_SECRET`, `_PROJECT_ID` (and optionally `_ENV`, default `dev`; `_PATH`, default `/`; `_URL`, default `https://app.infisical.com`). It reads `GOOGLE_CLIENT_ID` and `GOOGLE_CLIENT_SECRET` and falls back to `BUSPULSE_GOOGLE_*`. Verified against the live "Bus Pulse" project on 2026-11-10 (`InfisicalLiveProbeTests`, Viewer machine identity, both secrets read); the unit-test fixtures still come from the official documentation (v4 read endpoint).
- **Saved places by coordinates.** `SavedPlace` has `Latitude`, `Longitude`, `Address` and `DisplayDetail`; `PlacePoint.Resolve` and `PlacePoint.NearestStop` turn a place into a point and the nearest stop. Old configs still load.
- **Address search.** `IGeocoder` / `NominatimGeocoder` against the public OpenStreetMap Nominatim server. The maintainer explicitly confirmed the OSM usage policy on 2026-11-07: at most 1 request/s, no search-as-you-type, the typed text is sent to OSM, the location is never sent. The Settings switch `AddressSearchEnabled` turns it off. Attribution is shown, the query text is never logged, addresses are never written to the history file, and background or timer geocoding must never be added.
- **Settings screen:** add a place by address, address-search switch with a privacy line and attribution, a secrets-source status line, a Google card that says what is missing, and an advanced add-by-stop-code section.
- **Trip planner screen:** an origin search box, "Search address" buttons for origin and destination, Save place / Save as Home / Save as Work, and a date selector (Today / Tomorrow plus a DatePicker, up to 60 days) for depart-at and arrive-by. `TripTime.Resolve(date, ...)` builds the target; a time that already passed today shows tomorrow's date. The alert text is "Leave in N min: M min walk to <stop> for line X"; a departure 180 minutes or more away shows "Leave at HH:mm" instead of a countdown.
- **Default alert lead time is 10 minutes** (saved configs keep their own value).
- **Future dates.** Stride holds no timetable after today (probed empty for tomorrow). The planner now uses the official Ministry of Transport GTFS feed (`https://gtfs.mot.gov.il/gtfsfiles/israel-public-transportation.zip`, about 155 MB, about 30 days ahead, no key) through `IFutureScheduleSource` / `GtfsFeedScheduleSource` (`Services/Trips/Gtfs`). Measured by the build agent (not re-measured here): download about 7 s, index build 5-9 s per date, query 8-15 ms, peak about 284-318 MB while building, about 87 MB per day index, at most 2 indexes in memory (dropped after 15 idle minutes). It matched Stride's departures for stop 3490 exactly on one probed window. Statuses: `NoScheduleForDate` (the date is outside the feed, or the feed has no service), `ScheduleUnavailable` (download failed), and a ready feed with no departures nearby is `NoOptions`. Future-date trips are never labelled live. Today still uses Stride + Curlbus. Licence: no explicit open licence was found and the Ministry terms were read only through secondary sources; fine for personal use, re-check before redistributing. Trip ids: the Curlbus `trip_id` matches the feed's; Stride's `journey_ref` has a different date suffix but the same numeric part.
- **Resilience.** `TransientRetryHandler` (`Infrastructure`) retries GETs on 500/502/503/504/408 and dropped connections (two retries, 0.4 s and 1.5 s) on the two Stride clients (`TransitApi`, `StrideBulk`). One failed per-stop id lookup no longer fails a plan; only all of them failing does. Observed the same day: Stride answered sporadic 500s and a plan failed before this fix.
- **Second code review:** the 8 findings fixed earlier that day are described below ("Second code review"); not repeated here.

Verified on the real screen (2026-11-07):

- The red full-screen alert popup ("Leave now - line 66 in 7 min!" with the station and a close button).
- Windows toasts: "Alert added", and the upcoming-stage toast with "N min walk to <stop>".
- The planner and Settings screens load.
- Address search against the real Nominatim: a typed address, a result, chosen with Enter, then trip options.
- Planning for tomorrow from the real feed in the app (options labelled as schedule).

Not verified on screen:

- The amber "soon" and "leave now" toast stages (unit-tested only).
- The Settings add-place-by-address flow.
- The tray tooltip and the context menus.
- Dark theme and right-to-left layout of every new row, and the DatePicker look in the dark theme.
- A real Google Calendar connection (needs OAuth client credentials).
- How a reminder set for a future date behaves once that date becomes today (the watched ride's id comes from a different source).

Lessons from bugs found only by running the app:

- An XAML `DatePicker.DisplayDateEnd` bound two-way to a get-only property made the window never open (the "startup failed" message).
- Two merged branches both defined the same string keys; the localization tests caught it.
- A mutation check leaves the mutant compiled in `bin`; rebuild after it.
- Alerts were silent because notifications were muted in the saved settings (the header button reads "Resume alerts" when muted).

Tests: 514 unit tests pass on net10.0. Live probes (`Category=Live`) exist for Curlbus, the Stride stop list, the aggregator, trip-id and ordering, the planner against real services, Nominatim, and the GTFS feed (the last downloads 155 MB). They were not re-run for this update.

Still open: transfers (direct trips only), the maintainer's Infisical details, a Google OAuth client, and the push, which is blocked until the on-screen items above are checked and the maintainer approves (push only verified work).

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

1. Click through the app with a person at the screen: the amber toast stages, tray tooltip, context menus, RTL and dark theme of the new rows, DatePicker, time wheel, Settings add-by-address. Record the results here.
2. Connect a real Google account and check the calendar card (needs the OAuth client from HANDOFF.md, which now comes from Infisical; that path was verified live on 2026-11-10).
3. Add transfers to the planner (direct trips only today).
4. Decide on a live vehicle source (Curlbus does not expose one), or keep the Lines tab schedule-only.
5. Apply for official Ministry of Transport access (see PLAN.md).
6. AUMID and Start Menu shortcut for toasts, installer.

## Session 0 done (2026-11-10): design system

Design system only, no feature changes. Spec: [DESIGN.md](DESIGN.md).

What changed:

- `BusPulse/Themes/Theme.xaml`: Radix Colors 3.0.0 scales (slate, indigo accent, green, amber, red) as full light and dark token sets, role aliases, type scale, spacing and radius, and styles for buttons (primary, secondary, quiet, destructive, icon, list row), text box, suggestion row, toggle chip, `LineBadge`, `StatusChip`, `Banner`, skeleton and the departure row. `ThemeTokens` turns the active set into `Bp.*` brushes; `IThemeService` applies them and maps the older brush keys (`AccentBrush` and friends) onto tokens, so every tab now uses the indigo palette.
- Heebo (SIL OFL 1.1, static Regular/Medium/SemiBold/Bold) embedded and set on the window; Segoe UI as fallback.
- New controls in `Views/Controls/DesignControls.cs`: `LineBadge` (GTFS route colours with a luminance fallback), `StatusChip`, `Banner`, `TickerText` (cross-fading countdown) and `Motion` (fade/slide, skeleton pulse, honours Windows "Animation effects").
- Stops tab rebuilt on tokens and styles only. `ArrivalRowViewModel` gained `EtaValue`, `EtaUnit` and `Status`; live data older than 120 s now shows a "not updated" chip instead of a faded row. New strings `StaleBadge` and `EtaUnitMinutes` (en/he).
- `App.OnStartup` applies the theme before the window is shown, so the first frame has the tokens.

Verified:

- `dotnet build` (Debug and Release) and `dotnet test --filter "Category!=Live"`: 598 passed. `dotnet list package --vulnerable --include-transitive`: none.
- New tests: `DesignSystemTests` (token sets complete, 31 contrast pairs in both themes, line badge colours against the four `route_color` values in the real feed, Heebo weights with tabular digits and Hebrew glyphs, licence, no raw colours in the Stops view, theme merged after the strings dictionary) and `DesignRenderTests` (Stops screen rendered in he/en x light/dark, skeleton, component gallery, "9 to 10 to 99 min" moves nothing, font icons do not mirror in RTL while paths do).
- Mutation checks: the no-shift test fails with an `Auto` ETA column, and the `FindTokenDictionary` test fails with the old lookup.
- The real app was launched with `--theme=dark` and `--theme=light`: it starts, the Stops tab shows nearby stops with the new styles in Hebrew. The first launch failed at startup ("The given key 'Accent.9' was not present"): `ResourceDictionary.Contains` also searches merged dictionaries, so the token lookup returned the App dictionary. Fixed and covered by a regression test.
- Screenshots: `docs/screens/session-0/` (offscreen render with sample data; real-app captures were not committed because they show stops near the real location).

Not verified:

- Animations on a real screen, with Windows "Animation effects" on and off.
- An expanded stop with real live data in the running app (the expanded state was seen only in the offscreen render).
- The English UI in the running app (seen only in the offscreen render).
- The other tabs after the palette change: they use the mapped legacy brushes and were not checked on screen.

Found and left open:

- Line names that mix digits and Hebrew follow the text direction: "54א" shows as "א54" in the Hebrew UI. Open question for the maintainer (DESIGN.md, RTL checklist).
- `Arrival` carries no route colour yet, so every badge on the Stops tab is neutral with the operator name. The Ministry feed has `route_color` on 1,380 of 7,855 routes (four values) and no `route_text_color`.
- `OperatorBrushConverter` in the trip planner invents operator colours, against the new rule; replace it when the planner is rebuilt.
- Raw colours remain in `MainWindow` (header) and the other views; listed in DESIGN.md section 9.

## Session 1 done (2026-11-10): shell, navigation, settings panel

Spec: [DESIGN.md](DESIGN.md) section 11. Tab control and brand header replaced by a `NavigationView` (Stops, Trip planner, Lines); a gear in the title bar opens a settings side panel (closed at startup) with an "Advanced" expander. Settings rebuilt on tokens (no cards). Ctrl+1..3 tabs, Ctrl+4 / Ctrl+comma open settings, Esc closes. `dotnet build` clean, 621 tests pass (`Category!=Live`), 21 new in `ShellRenderTests`.

Verified on screen (running app, Windows 10 build 19045; real captures were not committed because they show the owner's stops):
- App starts in Hebrew and English, light and dark; nav pane on the right in Hebrew and on the left in English; settings panel on the opposite edge; gear click, Ctrl+comma, Ctrl+3 and Esc all work; the language combo dropdown aligns correctly in RTL. Language was switched to English and back, so the saved setting is unchanged.
- Windows without Mica (this machine): window renders cleanly on the plain background (the fallback). Mica itself: not seen.
- An expanded stop in the English UI (schedule rows, "scheduled" chips). Committed sample screenshots (made-up data, off-screen): `docs/screens/session-1/` `shell-{he,en}-{light,dark}-tab{1,2,3}.png`, `settings-{he,en}-{light,dark}.png`, `settings-he-light-advanced.png`.

Unit tests only: RTL placement of nav and panel (`ShellRenderTests`), panel closed at startup, shortcut 4 opens it, Advanced closed by default, theme choice saved (`ViewModelTests`), no raw colours in `ShellView` and `SettingsView`.

Unverified:
- An expanded stop with **live** data: the two stops tried returned "no live data", so no live row was seen.
- Animations: with "Animation effects" on, the panel background changed over about 200 ms (29, 25, 24 in three samples); with it off the probe could not tell the difference from the first frame. Not a proof either way. The setting was restored to on.
- Focus rings: seen on the panel's close button only; not checked on every control (nav items, combo boxes, check boxes use WPF-UI's own focus visuals).
- The gear and title bar are not in the offscreen renders; the gear was seen only in the real app. Mica; system-theme following while running; window at the 440 px minimum width.

Carry-over (other tabs, for the sessions that rebuild them): Trip planner and Lines still use default controls in a card; in dark mode the planner's summary line ("origin → destination") is black on dark and almost unreadable; both views are flush with no density pass. The Stops hint line ("Your location…") stays Hebrew after switching to English until the next load. `HeaderGradientBrush` in `ThemeService` has no user left. Test hosts must put strings, WPF-UI dictionaries and tokens at application level for views inside the navigation frame (see `ShellRenderTests.Build`).

Next: session 2.

## Session 2 done (2026-11-10): free-text origin and destination

Origin and destination are free-text boxes (no combo boxes, no search button). Suggestions: my location (origin only, first), saved places (recent first), calendar event locations, stops by name, then geocoder results; deduplicated by name or 50 m. Local sources filter instantly, no network. Enter takes the highlighted row, or with none highlighted looks the text up (Nominatim) and highlights the first address; losing focus looks it up once. Up/Down/Esc/click work. "לא נמצאו תוצאות" is the single empty line; the thin progress bar shows only after 300 ms of a real call. With `GOOGLE_PLACES_API_KEY` (see SECRETS_SETUP.md) suggestions come as you type after 400 ms, min 3 chars, previous call cancelled, session tokens, cap 200/day. Nominatim: accept-language=he, 10-minute pause after 429/403, encrypted 24 h disk cache.
Build clean; 671 tests pass (`Category!=Live`).

Verified on screen: the real app starts on the Trip planner tab; a typed text plus Enter ran a real lookup and showed the no-results line; RTL of the page (found and fixed: pages inside the navigation Frame did not get the window's FlowDirection; now bound in `ShellView.GetPage`). Committed sample screenshots (made-up data, off-screen): `docs/screens/session-2/freetext-{typing,geocoder,empty}-*.png`.
Unit tests only: the progress bar timing, Google provider (request shape, sessions, cap, breaker), disk cache, focus-loss lookup, keyboard handling in code-behind.
Unverified: Google Places against the real API (no key; response shapes are from the docs, not recorded); a successful Nominatim result in the real app; focus-loss behaviour with the mouse; Hebrew placeholder in the real app.
Notes: "recent trips" come from user_history.json, which only holds saved-place ids, so they rank saved places (no address is ever written to it). The rest of the planner view still uses legacy brushes. Spinner is a 2 px progress bar (ProgressRing was replaced after a test-host crash that turned out to be a blocking-wait in my test, not the control).

## Session 3 done (2026-11-10): trip results, walking legs, time picker, refresh

Carry-over: route colours (`RouteColorCatalog` reads agency and routes from the cached Ministry GTFS zip, never downloads; a key whose routes disagree gets no colour) flow `Arrival.RouteColor` / `TripOption.RouteColor` to `LineBadge`; `OperatorBrushConverter` deleted; line names always read digits first (owner decision, DESIGN.md "Line name order", LineBadge only); planner restyled on Bp.* tokens (no raw colours, test added).
Trip results: max 5 direct options, DepartureRow layout (badge, boarding stop, "צא ב-HH:mm", walk legs "הליכה X דק׳ (Y מ׳)" with "משוער" when estimated, hero minutes, status chip). Walking: `OsrmWalkingRouter` (FOSSGIS foot profile, 1 req/s, 4 s timeout, 24 h cache, 10-min pause on 429/403, fallback straight line x1.3). Refresh every 30 s while the tab is active, refresh button and F5, "עודכן לפני X שניות" with a warning icon after 90 s; rows keep identity by ride id. A watched route is polled by the reminder service while the tab is paused. New `TimeFieldPicker` replaces the wheel (mode chips, +10/+20/+30/+60, typed hour/minute, buttons step 5 min, arrows 1, date chips, 150 ms fade). Line filter suggests lines already seen (no network); "אין נתונים לקו הזה" on empty; progress bar only after 300 ms of a real plan. "טעינת קו" is the Lines-tab button label, not a spinner; left for the Lines session.
Build clean; 708 tests pass (`Category!=Live`). The real app was launched (`--tab=1`) and stays running.

Unit tests only / unverified on screen: no screenshots were taken this session (docs/screens/session-3/ is empty): results with walking legs, time picker modes, stale "עודכן לפני", dark-mode summary line, RTL and the colours from the real feed all still need a visual check. The routing service could not be reached from this machine (TLS handshake fails against routing.openstreetmap.de), so the success path is tested only with an OSRM-shaped response, and the fallback path is what runs here. Add routing.openstreetmap.de to SECURITY.md (sends origin and stop coordinates). Walk-adjusted leave-by drops options that become uncatchable without re-ranking the full candidate list.
Next: session 4.
