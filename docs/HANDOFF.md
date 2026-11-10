# Handoff: what is done, what is not, and what is needed

Snapshot: 2026-11-07, branch `main`, last commit: see `git log`. The app targets .NET 10 (`net10.0-windows10.0.19041.0`); `dotnet test --filter "Category!=Live"` passed 514 unit tests on that date (not re-run while this file was updated). Live probes (`Category=Live`) exist for Curlbus, Stride, the aggregator, the planner, Nominatim and the GTFS feed; run them by hand. Read [STATUS.md](STATUS.md) for verified/unverified detail and [PLAN.md](PLAN.md) for scope.

**Local commits are not pushed.** Per the maintainer's rule (push only verified work), nothing is pushed until the remaining on-screen checks below are done and the maintainer approves. `git log --oneline a136d63..HEAD` lists the work of 2026-11-07 (.NET 10, Infisical, saved places by coordinates, address search, planner dates, Ministry of Transport feed, retry handler).

## Start here tomorrow

1. Do not `git pull` over the local work: the commits on `main` are local only and unpushed. Run `git log origin/main..HEAD` to see them.
2. `dotnet build BusPulse.sln` and `dotnet test BusPulse.sln --filter "Category!=Live"` to confirm the baseline.
3. Do the on-screen checks listed under "Visual checks", then pick from "Needed from you".

## Done and tested (unit tests; visual checks noted)

- Tray icon with mute, language and profile menu; tooltip shows the next departure of the selected route.
- Stops tab: nearby stops from the Windows location (or a saved place), expand for arrivals with live/scheduled badges and freshness tooltips, pin with a star (pinned first). All lines due soon are listed (next two arrivals per line).
- Trip planner: origin is the current location, a saved place or a searched address (origin search box); destination with swap, Home/Work, search with history, search by any stop name (local stop list) and an explicit "Search address" button (Nominatim, can be switched off in Settings); Save place / Save as Home / Save as Work; now / depart at / arrive by with a wheel time picker and a date selector (up to 60 days); direct trips only; leave-by = departure - walk - buffer. Today uses Stride + Curlbus; later dates use the official Ministry of Transport GTFS feed (about 155 MB download, schedule only, never labelled live).
- Smart alerts: upcoming, soon, leave now (amber, lead time 0-15 min), full-screen red popup under 2 minutes, late, missed with next option. Toast "Alert added" when a route is selected.
- Calendar card on the trip planner: upcoming events whose location matches a saved place; "Plan this trip" fills destination, arrive-by and time (unit-tested only).
- Window close hides to the tray; Exit in the tray ends the app.
- Code review of `ed3e254..314215b` done; all verified findings fixed (see STATUS.md).
- Second code review of `314215b..54cc36b` done; all 8 findings fixed with tests (see STATUS.md): today's stop ids via `WithCurrentIdsAsync`, trip-ref live matching with a time fallback, calendar horizon under 24 h, suggestions recomputed on "Find trips", late refresh cannot overwrite a newer plan, arrive-by asks for the latest departures first, a watch change cancels its in-flight re-plan, ordinal stop-name search.
- Slow Stride handled: 45 s client timeout, 120 s `StrideBulk` client, stop list served from cache while it refreshes; `TransientRetryHandler` retries sporadic 5xx/408 and dropped connections twice; one failed stop-id lookup no longer fails a plan.
- Secrets: `ISecretProvider` reads the Google client id and secret from Infisical when `BUSPULSE_INFISICAL_*` is set, else from `BUSPULSE_GOOGLE_*` (Infisical verified against the live project on 2026-11-10 with `InfisicalLiveProbeTests`; the three `BUSPULSE_INFISICAL_*` user variables are set on the maintainer's machine).
- Lines tab: line number + origin + direction, stops in order with your stop marked. Schedule only (see below).
- Settings: language, theme (system/light/dark), notifications on/off, lead time (default 10 minutes; saved configs keep their own), refresh interval, Home/Work, places by address (or by stop code in an advanced section), address-search switch with privacy line and attribution, secrets-source line, clear history, clear favorites, test notification, provider status check, Google Calendar connect/disconnect.
- Shortcuts: Ctrl+1..4, Ctrl+, , Ctrl+F (opens planner search), F5.
- Fluent UI (WPF-UI 4.3.0): brand banner header, theme-aware cards, Mica window.
- Daylight-saving-safe target times; sub-minute walk text; reported save failures; crash log at `%LOCALAPPDATA%\BusPulse\crash.log`.

## Visual checks: partial

- Verified in the running app (2026-11-03): a stop expanded to live and scheduled arrivals; destination search by stop name picked "המרכז הרפואי יצחק שמיר, באר יעקב"; window close hides to the tray; dark and light themes (screenshots of every tab).
- Verified in the running app (2026-11-07): the red full-screen popup ("Leave now - line 66 in 7 min!"); Windows toasts ("Alert added" and the upcoming-stage toast with "N min walk to <stop>"); the planner and Settings screens load; address search against the real Nominatim (typed address, result, Enter, trip options); planning for tomorrow from the real feed (options labelled schedule).
- Not checked on screen (these still block a push): the amber "soon" and "leave now" toast stages, the Settings add-place-by-address flow, the tray tooltip, the context menus (right-click on stops and lines), dark theme and right-to-left layout of every new row, the DatePicker in the dark theme, the time wheel, a real Google Calendar connection, and how a reminder set for a future date behaves once that date becomes today.

## Needed from you

1. **Google Calendar (to enable the button):** in Google Cloud Console, create a project, enable the Google Calendar API, create an OAuth client ID of type **Desktop app**. Then either set the environment variables `BUSPULSE_GOOGLE_CLIENT_ID` and `BUSPULSE_GOOGLE_CLIENT_SECRET`, or store them in Infisical as `GOOGLE_CLIENT_ID` and `GOOGLE_CLIENT_SECRET` and set `BUSPULSE_INFISICAL_CLIENT_ID`, `BUSPULSE_INFISICAL_CLIENT_SECRET` and `BUSPULSE_INFISICAL_PROJECT_ID` (optional `_ENV` default `dev`, `_PATH` default `/`, `_URL` default `https://app.infisical.com`). Restart the app. Never commit these values. Done on 2026-11-10 through Infisical (see `SECRETS_SETUP.md`); what remains is clicking Connect in Settings and approving in the browser.
2. **Decision on live bus positions:** Curlbus does not expose them (route-alternative endpoints return 500 upstream). Options: keep the Lines tab schedule-only, or find another source.
3. **Official data (optional, for a real live source and rail):** apply for Ministry of Transport SIRI access (see PLAN.md). Israel Railways data needs an official key or permission; the unofficial key is intentionally not used.
4. **Decision on transfers:** the planner finds direct trips only (a Ramat Gan to Be'er Ya'akov trip returned none). Decide whether to build transfers.
5. **Ministry of Transport timetable licence:** no explicit open licence was found for the GTFS file (terms read only through secondary sources). Fine for personal use; re-check before redistributing the app.

## Not done (in order of value)

1. The on-screen checks above, and a fix for anything that looks wrong. Then push (with the maintainer's approval).
2. A real Google connection test (the OAuth flow has not run against Google; the client id and secret are read from Infisical, verified 2026-11-10).
3. Transfers in the trip planner.
4. Known issues (STATUS.md): favorites live in the config instead of `favorites.json`; a Lines direction shows one ride per distinct destination; a cancelled route can re-show its popup if re-selected.
5. Toast delivery on a clean machine depends on app identity (AUMID and Start Menu shortcut). Not built, so toasts may fail silently.
6. GitHub Actions: the workflow is committed but its first run has not been checked (no push yet).
7. Design polish (PLAN.md Phase 5): typography and spacing pass, status colours paired with icons.

## Notes for the next session

- Screenshot helper used in this session lived outside the repo (scratchpad). It launches `BusPulse.exe`, captures the window with `CopyFromScreen`, and accepts `--tab=N` and `--theme=light|dark` launch flags (the app reads them for one run and never saves them).
- Curlbus stop data works for stop 3490; route-level endpoints do not, and some stop codes (for example 4237) return 500.
- Stride can take 10-50 s per request (seen 2026-11-03); the live probes have long timeouts for that reason.
- Lessons from 2026-11-07 (details in STATUS.md): run the app after XAML binding changes (a two-way DatePicker binding to a get-only property kept the window from opening); rebuild after a mutation check; if alerts are silent, check whether notifications are muted (the header button reads "Resume alerts").
- Never add background or timer geocoding: address search runs only when the user presses Search or Enter (OSM usage policy, confirmed by the maintainer on 2026-11-07).
- Keep the golden rule: build, test, then commit small pieces.
- Model choice (from the user's guide): Opus with high effort for review and debugging, Sonnet with medium effort for building, Haiku with low effort for quick checks.
