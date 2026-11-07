# Plan: live reliability, alternatives and transfers (no card, no paid API, no risk)

Status: proposal for the maintainer's approval. Nothing in this plan is implemented yet. Written 2026-11-07 against `main` @ `cb207c9` (538 unit tests, .NET 10).

Audience: the maintainer, and the agents that will implement it. Every measured number below was taken on 2026-11-07 from the real services or the cached official feed; items that were not measured are marked "unverified".

## 1. Goal

BusPulse is a desktop app used before leaving home. Once the user has left, nothing it computes matters any more. So the product question is only:

1. Which bus (or bus plus one transfer) should I take, and when do I have to leave the house?
2. If something is wrong with the bus I picked (late, early, not showing up), tell me before I leave and offer a better option.

### Hard constraints (from the maintainer)

- No credit card, no billing account, no paid API, no quota that can turn into a charge.
- No risk: a new data source failing must never break planning, alerts or startup.
- Honesty rule (already in `CLAUDE.md`): nothing scheduled is presented as live, and nothing is claimed that no source supports.
- Personal use, one machine. No new data leaves the machine except where this plan says so (none of the phases below sends the user's location anywhere new).

### Non-goals

- Detecting accidents, demonstrations, road closures or traffic. No free, machine-readable, verified source is known (section 4, spike S4). The plan only integrates one if the spike finds it.
- Tracking the trip after departure, per-leg alerts while riding, or live data for the second leg of a transfer.
- Google, Moovit, Waze or any paid or keyed service. Cars and walking-only routes.
- More than one transfer in the first release (the design leaves room for a second).

## 2. What exists today (verified in code and by measurement)

| Area | Fact |
| --- | --- |
| Planner | `TripPlannerService` finds direct trips only: a ride that calls near the origin and later near the destination. Today: Stride (`StrideRideScheduleSource`) with live overlay; later dates: `GtfsFeedScheduleSource` (official feed). |
| Option model | `TripOption` has no scheduled time next to the live one: `BusDeparture` is already live-adjusted when a live match exists, so the delay cannot be shown today. |
| Live data | Curlbus (unofficial) is the live source. It answers HTTP 500 for some stop codes (seen 2026-11-07: 26583, 25114, 21161, 21631, 31366, 36313); the provider has a per-stop failure cache and a circuit breaker. |
| Alerts | `SmartAlertEngine` stages: upcoming, soon, leave now (amber), full-screen popup (under 2 min), late, missed. `DepartureReminderService` re-plans every 15-60 s, keeps the last known times when a re-plan fails and (since today) re-identifies the watched ride when the source switches. |
| Official feed | `gtfs.mot.gov.il`, 155 MB, 35,400 stops (238 are stations, 1,088 have a `parent_station`), 36 agencies, **no `transfers.txt`**. Route types: 6,611 bus (3), 1,297 Israel Railways (2), 13 light rail (0), 14 type 715, 4 type 5, 4 type 8. `routes.txt` for rail has an empty `route_short_name`. |
| Day index | `GtfsDayIndex`: one Israeli calendar day in memory, 12-byte rows grouped per ride, rows also ordered per stop. A Sunday has 4.47 M stop calls and 122,633 rides; build 5-9 s, about 87 MB per day, 2 days max, query 8-15 ms. |
| Stride SIRI | `/siri_rides`, `/siri_ride_stops`, `/siri_vehicle_locations`, `/siri_snapshots`, `/siri_velocity_aggregation`, `/gtfs_rides_agg` exist. **Freshness is intermittent**: on 2026-11-03 the newest real snapshot was from 2026-03-19 (and rows dated 2033-2038 exist); on 2026-11-07 21:08 UTC vehicle locations were 7 seconds old and rides scheduled in the last hour were present. Ordering by `scheduled_start_time desc` returns the junk future rows, so every query needs an explicit time window. |
| Stride quality | Requests take 0.2-40 s and sometimes answer 500; `TransientRetryHandler` now retries those. |

Consequence: a real-time source that is free, keyless and fresh exists today (Stride SIRI), but it was dead four days ago. It can only be used behind a freshness gate, and the app must work identically without it.

## 3. Principles for every phase

1. Feature switches: each new behaviour has a Settings switch (default on) and a config field; turning it off restores today's behaviour exactly.
2. Failure isolation: new providers return a status instead of throwing; the plan and the alerts never wait for them longer than a short budget.
3. Freshness gate: SIRI-derived data is used only when its newest observation is under 120 seconds old; otherwise it is ignored and the UI says nothing about it (no "all is well" claim from missing data).
4. Untrusted data: strict JSON, size caps, Israel bounding box, plausibility checks on every timestamp (reject years outside the current year +/- 1), string length caps. Real responses recorded as fixtures.
5. Labels: scheduled stays "scheduled"; a delay derived from live data says where it came from.
6. No spam: alerts fire on a state change, need two consecutive readings, and have a cool-down.
7. Privacy: stop codes and time windows only, as today. No new third party.

## 4. Phase 0: foundations and spikes (decide before building)

Each spike writes its result into `docs/live-data-probe.md` and ends with a go/no-go line. No product code depends on a spike that has not finished.

| # | Task | Output / gate |
| --- | --- | --- |
| 0.1 | Commit the UI-automation scripts used on 2026-11-07 (launch, maximize, type, press, capture, monitor alerts) under `tools/ui-checks/` with a README (PowerShell 5.1, UTF-8 with BOM, settings-file backup/restore wrapper, kill-and-rebuild notes). | Repeatable on-screen checks for every later phase. |
| 0.2 (S1) | SIRI freshness over 48 hours: poll the newest `siri_vehicle_locations.recorded_at_time` every 5 minutes, record age and HTTP status. | Availability percentage and typical age. Gate: if fresh less than 70 percent of the time, Phase 1b is demoted to optional and Phase 1a (Curlbus only) ships alone. |
| 0.3 (S2) | Matching keys. Compare the numeric part of Stride SIRI `journey_ref` (format `2026-11-07-586248708`), GTFS `trip_id` (format `586039354_071126`) and Curlbus `trip_id` for 50 rides at 3 stops. | Match rate per pair and the rule to use (exact id, numeric part, or line plus time window). |
| 0.4 (S3) | Rail and light rail in the index: do their stops have `stop_code`? how are parent stations modelled? what name to show when `route_short_name` is empty (headsign or agency)? | Decide whether `GtfsDayIndex` keys stops by code, by `stop_id`, or both; list of rail-specific display rules. |
| 0.5 (S4) | Disruption-source survey: (a) Ministry of Transport SIRI-SM/SX (needs an approved application and a static-IP whitelist, so realistically a proxy; see `PLAN.md`), (b) GTFS-Realtime service alerts from the Ministry or operators, (c) `data.gov.il` datasets, (d) national road-authority feeds, (e) anything else found. For each: URL, format, licence, freshness, coverage, auth, a sample response. | A table with go/no-go. Only a verified, machine-readable, free source enters Phase 5. News feeds and social media are excluded for automatic use (not reliable). |
| 0.6 | Benchmarks: a script that times one-transfer search candidates on the real cached feed (needed to confirm the budget in 6.6). | Numbers in the doc. |

Maintainer action (optional, not blocking): apply for Ministry of Transport SIRI access. It needs the maintainer's identity and a static IP, so it is only worth it if S1 shows Stride SIRI is unreliable.

## 5. Phase 1: live reliability and alternatives

Value: the user learns before leaving that the chosen bus is late, early or probably not running, and gets a concrete alternative. Size: medium. No dependency on Phase 2.

### 5.1 Data model

- `TripOption` gains `ScheduledDeparture` (the timetable time) next to `BusDeparture` (live-adjusted when live data exists), plus `LiveStatus`: `Scheduled` (no live data expected), `OnTime`, `Delayed(minutes)`, `Early(minutes)`, `NoLiveData`, `Stale`.
- `TripOptionViewModel` shows the status as a small badge and in the tooltip with its source and age (the tooltip already shows source and age).

### 5.2 Rules (conservative, all configurable)

- Delayed: live ETA minus scheduled time is 8 minutes or more (Settings: 5-20). Early: 3 minutes or more.
- NoLiveData is only a hint, never a claim: shown when the ride is within 15 minutes of its scheduled time, the source answered successfully for that stop, other rides of the same line at that stop have live ETAs, and this one does not. Wording: "no live data, it may not be running". "Cancelled" is never said without an official source (Phase 5).
- A reading counts only if the source observation is fresh (the existing 5-minute staleness rule for Curlbus; 120 seconds for SIRI).
- State changes need two consecutive readings and a 5-minute cool-down per ride.

### 5.3 Phase 1a: Curlbus only (ships first)

- Carry the scheduled time through `TripPlannerService.MatchLive`.
- Add the rules in 5.2 using the data the app already fetches.
- Tests: delayed, early, on time, hint conditions, Curlbus 500 for the stop (no hint), stale observation (no status), hysteresis.

### 5.4 Phase 1b: `StrideSiriProvider` (gated by S1 and S2)

- New `ILiveArrivalsProvider` (source `StrideSiri`): for a stop and a time window, query `siri_ride_stops` with explicit `siri_ride__scheduled_start_time_from/to`, never open-ended; drop rows with implausible dates; require the newest vehicle location in the answer to be under 120 s old.
- Estimating the delay of a ride that has not reached the stop yet: SIRI gives vehicle positions, not ETAs. Use `nearest_siri_vehicle_location__distance_from_journey_start` against the GTFS `shape_dist_traveled` of the stops to find the last stop passed, then delay = observation time minus that stop's scheduled time. If the ride's stops lack `shape_dist_traveled` the provider reports no estimate (never guesses).
- Aggregator: Curlbus stays first; SIRI is a second opinion. Disagreement rule: use the fresher observation; if they differ by more than 5 minutes show the more cautious (larger) delay with source "SIRI/Curlbus differ".
- Failure: any error or staleness returns `NoData`; the circuit breaker pattern of `CurlbusArrivalsProvider` applies.

### 5.5 Alerts and alternatives

- New alert kinds in `SmartAlertEngine`: `Delayed`, `Early`, `NoLive`. Texts (he and en) name the line, the minutes and the source; for example "Line 66 is 12 min late. Line 82 leaves in 6 min and arrives 3 min earlier."
- Alternative selection (pure function, unit-tested): from the options of the same plan choose the one with the earliest arrival whose `LeaveBy` is still at least 2 minutes ahead, if it arrives at least 3 minutes earlier than the delayed watched ride; otherwise say nothing about an alternative.
- No automatic switch. The alert text points at the card; the card shows an "Alternative" ribbon and the existing bell moves the reminder (`ToggleReminder`).
- Leave-by already follows live ETAs (it is computed from the live-adjusted `BusDeparture`), so an early bus moves the "leave now" time earlier on its own; Phase 1 only adds the explanation.

### 5.6 Acceptance

- With a recorded Curlbus fixture where the watched ride is 12 minutes late and another line is on time, the app produces exactly one Delayed alert naming the alternative; with live data missing at the source it produces no claim.
- All new states are covered by unit tests with mutation checks; on-screen check of the alert text and the ribbon (Hebrew RTL, dark theme).

## 6. Phase 2: one-transfer planner on the official feed

Value: a route when no direct line exists (for example Ramat Gan to Be'er Ya'akov, or Or Yehuda terminal on a Saturday evening), including light rail and Israel Railways, which are in the same feed. Size: large; pure computation, free, works for any date in the feed, including today.

### 6.1 Index extensions (`GtfsDayIndex`)

- Per-ride stop sequence accessor (rows are already grouped per ride: `_rideStart`).
- Route type and agency per ride (extend `RouteInfo`).
- A grid index of stops (cell about 250 m) for walking transfers, and parent-station groups so a station counts as one place.
- Outcome of spike S3 decides how rail and light-rail stops are keyed.

### 6.2 Algorithm (bounded two-leg search)

Inputs: origin stops O (up to 5 within 800 m), destination stops D (up to 6 within 800 m), window [from, to], day index.

1. R1 = rides calling at any stop of O inside the window (cap 400, earliest first, same cap logic as today).
2. For each ride r1 in R1 and each later stop s on r1 (with arrival time t): s is a transfer candidate. Skip if s is O or within 250 m of O (pointless), if the stop is not "closer" to D than the previous candidate in the same ride beyond a detour factor (prunes going the wrong way), or if the ride has already reached D (that is a direct trip).
3. Build the reverse set Z = rides that call at any stop of D, each with the position and time at D (one pass over D's stop ranges; the index is already ordered per stop).
4. For each candidate (s, t): enumerate calls at s and at stops within 250 m (via the grid) in [t + minTransfer, t + maxWait]; keep a call c if its ride is in Z and calls at D after c's position. Compute walking time between stops with the existing `Walking.Estimate`.
5. Option = (r1, s, r2, d). Deduplicate on (r1, r2); per r1 keep the best three by arrival; sort by arrival time, then by total walking plus waiting.
6. Output at most 8 options in total, direct options always included first when they exist and are not slower than the best transfer by more than the "show transfers when" rule (6.5).

### 6.3 Rules (configurable constants, documented, tested)

- Minimum transfer time: 3 min at the same stop or parent station; 4 min plus walking when stops differ (up to 250 m); 8 min when either leg is rail.
- Maximum wait at the transfer: 30 min. Maximum total trip duration: direct duration times 2.5, or 3 h.
- Disallow transferring to the same line in the same direction; disallow a second leg that goes back through O.
- Walking speed and detour factor: `Walking.Estimate` unchanged.

### 6.4 Model

- `TripOption.Transfer` (nullable): transfer stop, second line, operator, second departure and arrival, walk between stops, and `Slack` (second departure minus first arrival minus minimum transfer).
- `RideId` stays the first leg's id so the reminder and `Reidentify` keep working; the dedupe key includes the second ride.
- Rail/light-rail display rules from S3 (line name from headsign, agency, icon).

### 6.5 When to show transfers

- Always compute when there is no direct option; otherwise compute when the setting "suggest transfers" is on and show a transfer only if it arrives at least 10 minutes before the best direct option (setting). This avoids clutter.

### 6.6 Budgets and risks

- Budget: plan under 2 s at the 95th percentile with the day index ready; temporary memory under 20 MB; no change to the index memory (two days max).
- Risk: exploding candidate sets in dense areas. Mitigation: the caps above, early pruning, and a hard time budget (1.5 s) after which the best options found so far are returned with a "partial" flag.

### 6.7 Live overlay and watching

- Live data is applied to the first leg only (Curlbus trip id equals the feed trip id in the spike measured earlier: 2 of 3 at stop 3490; Stride `journey_ref` has a different date suffix but the same number; rule from S2).
- For today the transfer options come from the GTFS index while direct options keep coming from Stride; the reminder's re-plan must use the same source for the watched option or rely on `Reidentify` (already merged). Test this switch explicitly.

### 6.8 Acceptance

- Fixture-based unit tests built from a real feed subset (new rows in `tools/make_gtfs_fixture.py`): a known two-bus connection, a light-rail plus bus connection, a walking transfer, a missed-connection boundary, no-backtracking, and caps.
- Live probe (`Category=Live`) on the real feed: origin near stop 26583 to Or Yehuda terminal on a weekday returns at least one transfer option; timing recorded.
- On-screen check with the UI scripts.

## 7. Phase 3: screens and alerts for transfers

- Option card: first leg prominent (line, leave time, bus time, walk), then a second row "Transfer at <stop>: line <X> at <time>, buffer <N> min" with a colour for the buffer (green 7+, amber 3-6, red under 3). Only the first leg's countdown is shown, as requested.
- Alert text for the first leg adds one line about the transfer. If a live delay on the first leg makes the buffer drop under the minimum transfer time, raise a "you will probably miss the transfer" alert and offer the best alternative (reuses Phase 1.5).
- Settings: show transfers, "suggest transfers only when faster by N minutes", delay threshold, alternative suggestions.
- Strings in he and en; `AutomationProperties.Name` on new controls; RTL arrows; dark theme; keyboard navigation.
- The `TripOptionViewModel` leave text rules from today stay (clock time from 180 min away).

## 8. Phase 4: reliability history (optional, after Phase 1)

- Record locally, per line, stop and hour of day, the observed delay at the moment the user's watched ride departed (a few bytes per sample, stored in the DPAPI config area or a small JSON next to the cache, capped). After at least 10 samples show "usually N min late at this hour" on the card. No dependency on Stride history (which may be stale).

## 9. Phase 5: disruption feeds (only if spike S4 finds a verified source)

- One provider per verified source behind the same freshness and failure rules; alerts say what the source says and name it; never "cancelled" unless the official source says so.
- If S4 finds nothing, this phase is closed and the docs state that accidents, closures and demonstrations are not detected.

## 10. Testing and verification protocol (applies to every phase)

Lessons from 2026-11-07 are part of the protocol:

1. Unit tests first, with recorded real responses as fixtures; mutation check at least two tests per change.
2. After any mutation run, rebuild: the mutant stays compiled in `bin` (this cost a wrong on-screen failure on 2026-11-07).
3. XAML errors only appear at runtime (a two-way binding to a get-only property stopped the window from opening; two branches defined the same string key): after every XAML or resource change run the app and check the window opens; keep the guard tests.
4. Settings-file safety: any UI script that changes settings backs up and restores `settings.dpapi`.
5. PowerShell 5.1 pitfalls: double quotes inside arguments to native programs are stripped (use files for commit messages and for mutation text), here-strings with Hebrew need a UTF-8 BOM, `Start-Process` for the app.
6. Live probes (`Category=Live`) for each new source; real-app checks with `tools/ui-checks/`.
7. Notifications must be unmuted for alert checks (a muted state saved in settings silences every toast and popup).
8. Line endings CRLF for code, Strings files stay as in the index; every new string exists in both languages (localization tests).

## 11. Process, agents and models (cost control)

- One worktree per workstream; Sonnet builds; Haiku for quick checks (grep, builds, counting); Opus only for the algorithm design review in Phase 2 and the final review of each phase.
- Parallelism: Phase 1a, spikes S1-S4 and the UI-script task run in parallel. Phase 1b waits for S1/S2. Phase 2 starts with S3 and can run in parallel with Phase 1. Phase 3 waits for 1 and 2.
- Merge protocol: merge branches one at a time, rebuild, run the whole suite and the real-app smoke test before the next merge; fix duplicate string keys at merge time.
- Documentation is updated in the same phase (`STATUS.md`, `HANDOFF.md`, `PLAN.md`, `SECURITY.md` for any new host, `live-data-probe.md` for measurements).
- Nothing is pushed without the maintainer's explicit request (existing rule).

## 12. Risks

| Risk | Likelihood | Effect | Mitigation |
| --- | --- | --- | --- |
| Stride SIRI goes stale again | High (happened) | Phase 1b has no data | Freshness gate; Phase 1a stands alone; the UI shows nothing instead of "on time". |
| Curlbus 500 for a stop | Known | No live data at that stop | Existing breaker; no "no live data" hint when the source failed. |
| False "may not be running" hints | Medium | Alert fatigue, wrong decisions | Hint only, strict conditions, two readings, cool-down, off switch. |
| Transfer search too slow or noisy | Medium | Sluggish plan, clutter | Caps, pruning, time budget, "faster by N minutes" rule. |
| Feed quirks (odd calendar rows, rail without short names, stops without `stop_code`) | Medium | Wrong or missing options | Spike S3, strict calendar handling already tested, fixtures from the real feed. |
| Ride-id mismatch between sources after midnight or when switching source | Medium | Lost live tracking | `Reidentify` (merged) plus an explicit test for transfer options. |
| Official feed terms | Low for personal use | Redistribution | Documented; re-check before sharing the app. |

## 13. Milestones and definition of done

1. M0: spikes S1-S4 documented, UI scripts committed.
2. M1: Phase 1a in the app: delay, early and hint states, alternative alert; 100 percent of new unit tests pass; on-screen check.
3. M2: Phase 2 planner with tests and live probe; budgets met.
4. M3: Phase 3 screens and alerts; Hebrew and English; dark theme; on-screen check of a transfer card and the missed-transfer alert.
5. M4 (optional): Phase 1b SIRI provider, Phase 4 history, Phase 5 feeds if verified.
6. Done means: build with 0 errors, all tests pass, no vulnerable packages, no new host without a `SECURITY.md` row, every item on the on-screen checklist verified or explicitly listed as not verified, and the docs updated.

## 14. Decisions needed from the maintainer

1. Defaults: delay threshold 8 minutes, early 3 minutes, minimum alternative gain 3 minutes, show a transfer only when it is 10 minutes faster than the best direct option. Change any of these?
2. One transfer only in the first release, or plan for two from the start (more complex, slower)?
3. Should "may not be running" hints be on by default, given they can be wrong?
4. Phase order: this plan ships Phase 1a, then Phase 2, then Phase 3. Do you prefer Phase 2 first?
5. Whether to apply for Ministry of Transport SIRI access (optional; only useful if Stride SIRI proves unreliable).

## 15. What this plan does not promise

It does not detect traffic jams, accidents, road closures or demonstrations. It detects what the bus data itself shows (late, early, no live data) and what the official timetable says (alternatives and transfers). If spike S4 finds a verified free feed for disruptions, Phase 5 adds it; otherwise the docs will say so plainly.
