# Security and Privacy

BusPulse runs in the current Windows user session and needs no elevation.

## Data stored locally

| Data | Location | Protection |
| --- | --- | --- |
| Settings: language, profile, mute state, saved places (name and stop code, or name, address and coordinates for a place chosen by address search), address-search switch, selected route | `%LOCALAPPDATA%\BusPulse\settings.dpapi` | Encrypted with Windows DPAPI (`CurrentUser` scope); size-capped and validated on load |
| Google Calendar refresh token (only if you connect) | `%LOCALAPPDATA%\BusPulse\calendar.dpapi` | Encrypted with Windows DPAPI (`CurrentUser`); removed on disconnect |
| Crash log (exception details, no location or tokens) | `%LOCALAPPDATA%\BusPulse\crash.log` | Plain text, local only |
| Recent destinations (place id and name, at most 30; places chosen by address are not recorded) | `%LOCALAPPDATA%\BusPulse\user_history.json` | Plain text, local only, size-capped |
| Stop list cache (public data) | `%LOCALAPPDATA%\BusPulse\cache\stops-<date>.json` | Not encrypted; replaced daily |
| Ministry of Transport timetable (public data, about 150 MB) | `%LOCALAPPDATA%\BusPulse\cache\gtfs-<date>.zip` | Not encrypted; refreshed at most daily, files older than 3 days deleted |

Your device location is read through the Windows Location API only to sort nearby stops. It is held in memory, is never written to disk, never logged at Information level, and is never sent to any server. Only a stop code leaves the machine when arrivals are requested. There is no IP-based location fallback, because it would send your IP address to a third party.

## External hosts contacted

| Host | Purpose | What is sent |
| --- | --- | --- |
| `curlbus.app` | Live bus arrivals (unofficial community service) | A stop code, and a descriptive `User-Agent` |
| `open-bus-stride-api.hasadna.org.il` | GTFS static stops and scheduled arrivals | A date, a stop code or id, and a time window |
| `gtfs.mot.gov.il` | Official Ministry of Transport timetable file (about 150 MB) for planning trips on future dates; downloaded at most once a day and read locally | Nothing about you: one fixed HTTPS GET with a descriptive `User-Agent`, redirects off, 400 MB size cap. The file is cached under `%LOCALAPPDATA%\BusPulse\cache` and files older than 3 days are deleted |
| `nominatim.openstreetmap.org` | Optional address search for the trip planner (OpenStreetMap Nominatim; the server address is configurable in the settings file) | The text you typed into address search, your IP address, and a descriptive `User-Agent` (no email address). Not your device location, calendar locations or stop names. Off when the Settings switch is off |
| `accounts.google.com`, `oauth2.googleapis.com`, `www.googleapis.com` | Optional Google Calendar connection (sign-in, token exchange, event list) | OAuth requests with your consent, read-only scope `calendar.events.readonly`. Nothing else from the app is sent |
| The configured Infisical URL (default `app.infisical.com`) | Optional secrets lookup for the Google client id and secret | The Machine Identity client id and secret (login), then the secret name, project id, environment and path. Only if `BUSPULSE_INFISICAL_*` is set |

All of them are HTTPS only, use the operating system's default TLS (1.2 or later) with certificate validation always on, and have redirects disabled (except the Google calendar client, which uses the defaults). Timeouts: Curlbus 10 s, Nominatim and Infisical 15 s, Stride 45 s (120 s for the stop list download, because the Stride API can take 10-50 seconds per request), the timetable file 15 minutes. Responses have size caps (the timetable download is capped at 400 MB). The two Stride clients retry a failed GET twice on 500/502/503/504/408 or a dropped connection. Israel Railways and the Ministry of Transport live (SIRI) feeds are not contacted; they stay disabled until an official key or endpoint is configured. The Ministry of Transport timetable file above is the only Ministry host used.

## Google Calendar

The connection is off until you click Connect in Settings. It uses the OAuth installed-app flow in your browser with a loopback listener and requests only the read-only scope `calendar.events.readonly`. The refresh token is stored with DPAPI in `%LOCALAPPDATA%\BusPulse\calendar.dpapi`. The OAuth client id and secret are read from the environment variables `BUSPULSE_GOOGLE_CLIENT_ID` and `BUSPULSE_GOOGLE_CLIENT_SECRET` and are never committed. Event locations are matched to your saved places on this machine and are never sent anywhere else.

## Secrets source

The Google client id and secret (secret names `GOOGLE_CLIENT_ID` and `GOOGLE_CLIENT_SECRET`) are read through a secret provider: Infisical first (only when `BUSPULSE_INFISICAL_*` is configured), then the environment variables `BUSPULSE_GOOGLE_CLIENT_ID` and `BUSPULSE_GOOGLE_CLIENT_SECRET`. The Infisical path is written from the official documentation and has not been verified against a live Infisical. Infisical is configured only by environment variables (`BUSPULSE_INFISICAL_CLIENT_ID`, `BUSPULSE_INFISICAL_CLIENT_SECRET`, `BUSPULSE_INFISICAL_PROJECT_ID`, and optionally `BUSPULSE_INFISICAL_ENV`, `BUSPULSE_INFISICAL_PATH`, `BUSPULSE_INFISICAL_URL`); give the Machine Identity read-only access to a single project. Requests go to the configured HTTPS host only, with redirects off, a 15 second timeout, a 64 KB response cap and strict JSON parsing. The access token is held in memory until shortly before it expires and secret values are cached in memory for 10 minutes; nothing is written to disk. Secret values, tokens and the client secret are never logged (only the secret name and an HTTP status). A failure or a missing secret in Infisical falls back to the environment variables.

## Destination search

Searching for a destination by name (saved places, then any stop name) runs against the stop list held locally. That text is not sent anywhere.

Searching for an address is the one exception. When you press Search (never while typing), the typed address text is sent to the OpenStreetMap Nominatim service (`nominatim.openstreetmap.org`), and the service sees your IP address. The device location, calendar event locations and stop names are never sent. The Settings switch "Address search" turns it off, and then nothing is sent. Requests are limited to one per second, results are cached in memory for 24 hours (no repeated query is sent twice), and the app identifies itself with a `User-Agent` that names the project and its repository URL (the URL contains the maintainer's GitHub user name; there is no email address). Typed addresses are not written to the history file or to the logs; only an address you choose to save is kept, in the DPAPI-encrypted settings. The server address can be changed in the settings file (`GeocoderBaseUrl`, must be https). Results are credited "© OpenStreetMap contributors" (ODbL).

## Handling untrusted data

Every API response is treated as untrusted: it is parsed with `System.Text.Json` into strict models, strings are length-capped, coordinates must fall inside Israel, and timestamps must be plausible (impossible years, past arrivals and stale live observations are discarded). Response content is never rendered as markup or executed.

## Secrets

No keys or tokens are stored in the repository. A future Israel Railways key is read from configuration (`RailApiKey`, via `dotnet user-secrets` or environment variables). The key found in third-party client code on rail.co.il is intentionally not used. CI runs a gitleaks secret scan and a vulnerable-package check on every push.

## Not yet implemented

An installer, and the app identity (AUMID and Start Menu shortcut) that Windows toast delivery needs for an unpackaged app. This document will be updated when they exist.

## Reporting a vulnerability

Please use GitHub's private vulnerability reporting (Security tab of this repository) instead of a public issue.
