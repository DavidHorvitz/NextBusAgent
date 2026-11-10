# Live data probe results

Probed 2026-10-20, about 19:25 Israel time. Raw samples are trimmed; Hebrew text is unchanged.

| Provider | Endpoint | HTTP | Latency | Data freshness | Verdict |
| --- | --- | --- | --- | --- | --- |
| Curlbus (primary) | `GET https://curlbus.app/3490` with `Accept: application/json` | 200 | ~1.4 s | Response `timestamp` was the current time. Visit `eta` values were 11 to 30 minutes ahead; vehicle `location` and `departed` present. | **Usable. Live.** |
| Curlbus, bad stop | `GET https://curlbus.app/20123` | 200 | ~1.4 s | Body is `{"errors": ["Invalid stop code 20123"]}` with status 200, so errors must be detected in the body. | Handled by the parser. |
| Stride GTFS static | `GET https://open-bus-stride-api.hasadna.org.il/gtfs_stops/list`, `/gtfs_ride_stops/list` | 200 | ~0.3 s | Current (stop `date` 2026-10-20; scheduled arrivals for today). | **Usable as the schedule fallback.** |
| Stride SIRI | `/siri_rides/list`, `/siri_vehicle_locations/list` | 200 | ~0.3 s | Newest real snapshot `2026/03/19`; rows with `recorded_at_time` in 2038 and rides dated 2036. `/siri_snapshots/list` returns a 500. | **Stale. Do not use as live data.** |
| Israel Railways | `https://rail-api.rail.co.il/rjpa/api/v1` (from the `israel-rail-api` package) | not probed | | | **Blocked on a decision, see below.** |
| MOT SIRI-SM | official | not probed | | | Needs an approved application and a static-IP whitelist. Implement behind config only. |

## Curlbus response shape (used for the fixture)

Top level: `errors` (null or array), `timestamp`, `visits` (object keyed by stop code), `stop_info`.
Each visit: `line_name`, `eta`, `timestamp`, `departed`, `vehicle_ref`, `trip_id`, `location {lat, lon}`, `producer`, and `static_info` with `route.destination.name{HE,EN,AR}` and `agency.name{HE,EN}`.
Timestamps use the form `2026-10-20 19:36:00+03:00`. The fixture is `BusPulse.Tests/Fixtures/curlbus-stop-3490.json`.

## Israel Railways: why it is not wired yet

The `israel-rail-api` package authenticates with a subscription key that its source describes as "bundled in main.js of rail.co.il", meaning a key taken from the public website rather than issued to this project. This repo does not embed it and has not called the endpoint with it. `RailProvider` will read a key from user configuration (DPAPI-protected) and stay disabled until one is supplied. Options: obtain an official key from Israel Railways, or accept using the website key knowingly. The fallback is the GTFS rail schedule.

## Gaps

- Curlbus is unofficial and has no SLA; the provider caches 25 s, spaces requests by 1 s, and opens a circuit for 3 minutes after 3 failures.
- Only one stop was probed live. Coverage and behavior for stops with no service were not measured.

## Curlbus route alternatives (checked 2026-10-20)

The Curlbus README documents `/<operator>/<route_number>/<alternative>`. In practice:

| URL | HTTP | Result |
| --- | --- | --- |
| `/dan/4` | 200 | Lists six route alternatives with names |
| `/dan/4/0`, `/dan/4/1`, `/dan/4/2` | 500 | Server error, body "Server got itself in trouble" |
| `/dan/4/Lehi` | 500 | Server error |
| `/egged/4` | 200 | Returns data for line 4 of Egged |

So alternative-level data is unavailable upstream. The Lines tab must use the Stride schedule for line stops, and live vehicle positions need a different source.

## Trip ids and ordering (checked 2026-11-03, stop 3490)

- Curlbus trip ids and Stride journey refs share the format `<number>_<ddMMyy>`. They matched on 2026-10-20, but on 2026-11-03 none matched: Stride refs had the suffix `_021126` and different numbers (line 3: Stride 53263877 and 58106769, Curlbus 53264001_031126). Each source names trips from its own GTFS feed version, so the app matches by trip ref only when some live id equals a journey ref, and otherwise falls back to the closest scheduled time on the same line.
- Stride `order_by=arrival_time desc` works (verified by a live probe); arrive-by uses it to get the latest departures first.
- Curlbus needs the header `Accept: application/json`; a plain request returned non-JSON.

## Ministry of Transport GTFS feed (probed 2026-11-07)

`GET https://gtfs.mot.gov.il/gtfsfiles/israel-public-transportation.zip`, no key, no redirect, HTTP 200, `Cache-Control: no-store`, `Last-Modified: Fri, 06 Nov 2026 17:37:20 GMT`. 155,279,794 bytes; downloaded in 7 s with curl (about 2 minutes was measured on 2026-11-03). Used by `GtfsFeedScheduleSource` for dates after today, which Stride cannot answer.

- Files: agency, calendar, fare_attributes, fare_rules, routes, shapes (231 MB, never read), stop_times (663 MB uncompressed), stops, translations, trips. There is **no calendar_dates.txt**; the app handles both (the test fixture adds a synthetic one).
- Columns relied on (verified): `agency.txt` agency_id, agency_name; `routes.txt` route_id, agency_id, route_short_name, route_long_name; `trips.txt` route_id, service_id, trip_id; `stops.txt` stop_id, stop_code, stop_name; `stop_times.txt` trip_id, arrival_time, departure_time, stop_id, stop_sequence; `calendar.txt` service_id, sunday..saturday, start_date, end_date. `stop_times.stop_id` is the feed's own stop id, so it is mapped to `stop_code` through `stops.txt` (stop 3490 is stop_id 52158). The files are read by header name, so column order does not matter.
- Coverage on the probe day: 2026-11-07 to 2026-12-07 (about 30 days).
- Trip ids have the form `<number>_<ddMMyy>`, the same shape as Curlbus `trip_id`.

### Trip-id comparison for stop 3490 (2026-11-07, 20:00-01:00 Israel time)

| Source | Trips | Equal to the MOT feed |
| --- | --- | --- |
| Curlbus `trip_id` | 3 | 2 of 3 identical strings (`53313636_071126`, `586039354_071126`); the third (`53491814_071126`) is not in the feed |
| Stride `gtfs_ride__journey_ref` | 26 | 0 of 26 identical (Stride suffix is `_061126`, the feed's `_071126`); the numeric part before `_` equals the feed's for 26 of 26 |

So the feed matches Curlbus's version, and Stride uses the same numbers with the previous day's suffix. Matching live ids to feed trips can compare the numeric part. For the same stop and window the feed index returned 26 departures with exactly the same times as Stride's 26, so scheduled times agree.

### Measurements (Windows 10, release build of the app library, SSD, feed in the cache folder)

| Measure | Result | Target |
| --- | --- | --- |
| Build one day index (first, includes JIT) | 6.6-9.3 s; second date 5.1-5.6 s | under 40 s |
| Rows per day | 4.47 M stop calls (Sunday 2026-11-08), 4.31 M (Monday), 122,633 and 118,923 rides | |
| Peak working set while building one index | 284-318 MB (process baseline about 25 MB) | under 400 MB |
| Peak with a second index already loaded | 430 MB | |
| Steady memory | about 87 MB managed per day index, 175 MB for two | under 150 MB per index |
| Query after build | 8-15 ms for a 3-hour window at one stop | |

The first version used a 16-byte row and measured 104 MB per index; the ride is now implied by the row position (12-byte rows plus a 4-byte per-stop order array), and the build forces a garbage collection afterwards to give back about 150 MB of temporary arrays. A cold download plus first index is about 30 s on a fast line.

### Licence and terms

Not found as an explicit open licence. Searched on 2026-11-07: the Ministry publishes the feed under its own Hebrew "terms of use" on gov.il (referenced from the MobilityData catalogue entry mdb-2519). Secondary sources (OpenStreetMap community threads) report that the terms only reserve the right to change them without notice and disclaim liability for errors and out-of-date data. No attribution or non-commercial clause was found, but this was not read from the official page. Treat it as: free to download and use for personal, non-commercial purposes, no warranty. Re-check the official terms before any redistribution or publication.

## Infisical (probed live on 2026-11-10)

The provider was written from the official API reference (checked on 2026-11-07), which is also where the unit-test fixtures come from. On 2026-11-10 `InfisicalLiveProbeTests` ran against `app.infisical.com` with a project-level machine identity (Universal Auth, role Viewer) on the "Bus Pulse" project, environment `dev`, path `/`: login succeeded and both `GOOGLE_CLIENT_ID` (72 characters, ending `.apps.googleusercontent.com`) and `GOOGLE_CLIENT_SECRET` (35 characters) were read in about 2 s. Without credentials both routes answer 401 (an unknown route answers 404). The real response bodies were not recorded as fixtures because they contain the secrets.

- Login: `POST {url}/api/v1/auth/universal-auth/login` with JSON `clientId` and `clientSecret` returns `accessToken`, `expiresIn` (seconds), `accessTokenMaxTTL`, `tokenType` ("Bearer").
- Read: `GET {url}/api/v4/secrets/{secretName}?projectId=&environment=&secretPath=` with `Authorization: Bearer <accessToken>` returns `{ "secret": { "secretKey", "secretValue", ... } }`. The older `/api/v3/secrets/raw/{name}` with `workspaceId` is not used; the current reference documents v4 with `projectId`.
- Errors (400, 401, 403, 404, 422, 500) return JSON with `reqId`, `statusCode`, `message`, `error`; the app only looks at the status code.

Re-run with `dotnet test --filter "FullyQualifiedName~InfisicalLiveProbeTests"` (needs `BUSPULSE_INFISICAL_*`; prints lengths only).
