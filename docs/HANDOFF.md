# Handoff: what is done, what is not, and what is needed

Snapshot: 2026-10-20, branch `main`, last commit `422433e`. `dotnet build` 0 errors; `dotnet test --filter "Category!=Live"` 223 passing. Read [STATUS.md](STATUS.md) for verified/unverified detail and [PLAN.md](PLAN.md) for scope.

## Start here tomorrow

1. `git pull` (nothing else is needed; the 24 local commits are the work described here).
2. `dotnet build BusPulse.sln` and `dotnet test BusPulse.sln --filter "Category!=Live"` to confirm the baseline.
3. Pick from "Needed from you" below, starting with the Google Calendar client if you want that feature.

## Done and tested (unit tests; visual checks noted)

- Tray icon with mute, language and profile menu; tooltip shows the next departure of the selected route.
- Stops tab: nearby stops from the Windows location (or a saved place), expand for arrivals with live/scheduled badges and freshness tooltips, pin with a star (pinned first). All lines due soon are listed (next two arrivals per line).
- Trip planner: origin is always "my current location"; destination with swap, Home/Work, search with history; now / depart at / arrive by with a wheel time picker; direct trips only; leave-by = departure - walk - buffer.
- Smart alerts: upcoming, soon, leave now (amber, lead time 0-15 min), full-screen red popup under 2 minutes, late, missed with next option. Toast "Alert added" when a route is selected.
- Lines tab: line number + origin + direction, stops in order with your stop marked. Schedule only (see below).
- Settings: language, theme (system/light/dark), notifications on/off, lead time, refresh interval, Home/Work, clear history, clear favorites, test notification, provider status check, Google Calendar connect/disconnect.
- Shortcuts: Ctrl+1..4, Ctrl+, , Ctrl+F (opens planner search), F5.
- Fluent UI (WPF-UI 4.3.0): brand banner header, theme-aware cards, Mica window.
- Daylight-saving-safe target times; sub-minute walk text; reported save failures; crash log at `%LOCALAPPDATA%\BusPulse\crash.log`.

## Visual checks: partial

- Screenshots were taken of the Stops, Trip planner, Lines and Settings tabs in light and dark, and the header in light and dark. Only the header and first screens were looked at closely.
- Not checked on screen: toast appearance, the full-screen popup, the tray icon and tooltip, the time wheel, the context menus (right-click on stops and lines), Hebrew RTL on every tab, and the new calendar card in Hebrew.

## Needed from you

1. **Google Calendar (to enable the button):** in Google Cloud Console, create a project, enable the Google Calendar API, create an OAuth client ID of type **Desktop app**. Then set environment variables `BUSPULSE_GOOGLE_CLIENT_ID` and `BUSPULSE_GOOGLE_CLIENT_SECRET` and restart the app. Never commit these values.
2. **Decision on live bus positions:** Curlbus does not expose them (route-alternative endpoints return 500 upstream). Options: keep the Lines tab schedule-only, or find another source.
3. **Official data (optional, for a real live source and rail):** apply for Ministry of Transport SIRI access (see PLAN.md). Israel Railways data needs an official key or permission; the unofficial key is intentionally not used.
4. **Decision on trip suggestions from calendar events:** the matcher exists (`CalendarTripSuggester`) but is not shown in the UI yet. Confirm you want suggestions as a card on the trip planner tab.

## Not done (in order of value)

1. Calendar suggestions shown in the UI, and a real connection test once item 1 above is done. The OAuth flow itself has not been run against Google.
2. Visual checks listed above, and a fix for anything that looks wrong.
3. Code review of the whole diff since `ed3e254` (PLAN.md step 9). Not started.
4. "Clear favorites" button and theme picker: implemented and tested; confirm on screen.
5. Known issues (STATUS.md): the favorites live in the config instead of `favorites.json`; a Lines direction shows one ride per distinct destination; a cancelled route can re-show its popup if re-selected.
6. Toast delivery on Windows depends on app identity (AUMID and Start Menu shortcut). Not built, so toasts may fail silently on a clean machine.
7. GitHub Actions: the workflow is committed but its first run has not been checked.
8. Design polish (PLAN.md Phase 5 step 7): typography and spacing pass, status colours paired with icons.

## Notes for the next session

- Screenshot helper used in this session lived outside the repo (scratchpad). It launches `BusPulse.exe`, captures the window with `CopyFromScreen`, and accepts `--tab=N` and `--theme=light|dark` launch flags (the app reads them for one run and never saves them).
- Curlbus stop data works for stop 3490; route-level endpoints do not.
- Keep the golden rule: build, test, then commit small pieces.
- Model choice (from the user's guide): Opus with high effort for review and debugging, Sonnet with medium effort for building, Haiku with low effort for quick checks.
