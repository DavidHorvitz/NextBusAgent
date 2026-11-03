# Handoff: what is done, what is not, and what is needed

Snapshot: 2026-11-03, branch `main`, last commit `54cc36b`. `dotnet build` 0 errors; `dotnet test --filter "Category!=Live"` 255 passing; 3 live probes passing. Read [STATUS.md](STATUS.md) for verified/unverified detail and [PLAN.md](PLAN.md) for scope.

**Local commits are not pushed.** Per the maintainer's rule, nothing is pushed until the on-screen checks below are done (toasts, full-screen popup, tray tooltip, context menus). Those checks are what still blocks a push.

## Start here tomorrow

1. Do not `git pull` over the local work: the commits on `main` are local only and unpushed. Run `git log origin/main..HEAD` to see them.
2. `dotnet build BusPulse.sln` and `dotnet test BusPulse.sln --filter "Category!=Live"` to confirm the baseline.
3. Do the on-screen checks listed under "Visual checks", then pick from "Needed from you".

## Done and tested (unit tests; visual checks noted)

- Tray icon with mute, language and profile menu; tooltip shows the next departure of the selected route.
- Stops tab: nearby stops from the Windows location (or a saved place), expand for arrivals with live/scheduled badges and freshness tooltips, pin with a star (pinned first). All lines due soon are listed (next two arrivals per line).
- Trip planner: origin is always "my current location"; destination with swap, Home/Work, search with history, and search by any stop name (local stop list, no geocoding); now / depart at / arrive by with a wheel time picker; direct trips only; leave-by = departure - walk - buffer.
- Smart alerts: upcoming, soon, leave now (amber, lead time 0-15 min), full-screen red popup under 2 minutes, late, missed with next option. Toast "Alert added" when a route is selected.
- Calendar card on the trip planner: upcoming events whose location matches a saved place; "Plan this trip" fills destination, arrive-by and time (unit-tested only).
- Window close hides to the tray; Exit in the tray ends the app.
- Code review of `ed3e254..314215b` done; all verified findings fixed (see STATUS.md).
- Slow Stride handled: 45 s client timeout, 120 s `StrideBulk` client, stop list served from cache while it refreshes.
- Lines tab: line number + origin + direction, stops in order with your stop marked. Schedule only (see below).
- Settings: language, theme (system/light/dark), notifications on/off, lead time, refresh interval, Home/Work, clear history, clear favorites, test notification, provider status check, Google Calendar connect/disconnect.
- Shortcuts: Ctrl+1..4, Ctrl+, , Ctrl+F (opens planner search), F5.
- Fluent UI (WPF-UI 4.3.0): brand banner header, theme-aware cards, Mica window.
- Daylight-saving-safe target times; sub-minute walk text; reported save failures; crash log at `%LOCALAPPDATA%\BusPulse\crash.log`.

## Visual checks: partial

- Verified in the running app: a stop expanded to live and scheduled arrivals; destination search by stop name picked "המרכז הרפואי יצחק שמיר, באר יעקב"; window close hides to the tray; dark and light themes (screenshots of every tab).
- Not checked on screen (these block a push): toast appearance, the full-screen popup, the tray tooltip, the context menus (right-click on stops and lines). Also unchecked: the time wheel, Hebrew RTL on every tab, and the calendar card with a real Google account.

## Needed from you

1. **Google Calendar (to enable the button):** in Google Cloud Console, create a project, enable the Google Calendar API, create an OAuth client ID of type **Desktop app**. Then set environment variables `BUSPULSE_GOOGLE_CLIENT_ID` and `BUSPULSE_GOOGLE_CLIENT_SECRET` and restart the app. Never commit these values.
2. **Decision on live bus positions:** Curlbus does not expose them (route-alternative endpoints return 500 upstream). Options: keep the Lines tab schedule-only, or find another source.
3. **Official data (optional, for a real live source and rail):** apply for Ministry of Transport SIRI access (see PLAN.md). Israel Railways data needs an official key or permission; the unofficial key is intentionally not used.
4. **Decision on transfers:** the planner finds direct trips only (a Ramat Gan to Be'er Ya'akov trip returned none). Decide whether to build transfers.

## Not done (in order of value)

1. The on-screen checks above, and a fix for anything that looks wrong. Then push.
2. A real Google connection test (the OAuth flow has not run against Google).
3. Transfers in the trip planner.
4. Known issues (STATUS.md): favorites live in the config instead of `favorites.json`; a Lines direction shows one ride per distinct destination; a cancelled route can re-show its popup if re-selected.
5. Toast delivery on a clean machine depends on app identity (AUMID and Start Menu shortcut). Not built, so toasts may fail silently.
6. GitHub Actions: the workflow is committed but its first run has not been checked (no push yet).
7. Design polish (PLAN.md Phase 5): typography and spacing pass, status colours paired with icons.

## Notes for the next session

- Screenshot helper used in this session lived outside the repo (scratchpad). It launches `BusPulse.exe`, captures the window with `CopyFromScreen`, and accepts `--tab=N` and `--theme=light|dark` launch flags (the app reads them for one run and never saves them).
- Curlbus stop data works for stop 3490; route-level endpoints do not, and some stop codes (for example 4237) return 500.
- Stride can take 10-50 s per request (seen 2026-11-03); the live probes have long timeouts for that reason.
- Keep the golden rule: build, test, then commit small pieces.
- Model choice (from the user's guide): Opus with high effort for review and debugging, Sonnet with medium effort for building, Haiku with low effort for quick checks.
