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

## Infisical (not yet probed live)

No Infisical credentials existed when this provider was written, so nothing here was observed against a live service. The shapes come from the official API reference (checked on 2026-11-07) and the test fixtures say the same.

- Login: `POST {url}/api/v1/auth/universal-auth/login` with JSON `clientId` and `clientSecret` returns `accessToken`, `expiresIn` (seconds), `accessTokenMaxTTL`, `tokenType` ("Bearer").
- Read: `GET {url}/api/v4/secrets/{secretName}?projectId=&environment=&secretPath=` with `Authorization: Bearer <accessToken>` returns `{ "secret": { "secretKey", "secretValue", ... } }`. The older `/api/v3/secrets/raw/{name}` with `workspaceId` is not used; the current reference documents v4 with `projectId`.
- Errors (400, 401, 403, 404, 422, 500) return JSON with `reqId`, `statusCode`, `message`, `error`; the app only looks at the status code.

Run a real probe once the maintainer has set `BUSPULSE_INFISICAL_*` and record the result here.
