# Security and Privacy

BusPulse runs in the current Windows user session and needs no elevation.

## Data stored locally

| Data | Location | Protection |
| --- | --- | --- |
| Settings: language, profile, mute state, saved places (name and stop code), selected route | `%LOCALAPPDATA%\BusPulse\settings.dpapi` | Encrypted with Windows DPAPI (`CurrentUser` scope); size-capped and validated on load |
| Stop list cache (public data) | `%LOCALAPPDATA%\BusPulse\cache\stops-<date>.json` | Not encrypted; replaced daily |

Your device location is read through the Windows Location API only to sort nearby stops. It is held in memory, is never written to disk, never logged at Information level, and is never sent to any server. Only a stop code leaves the machine when arrivals are requested. There is no IP-based location fallback, because it would send your IP address to a third party.

## External hosts contacted

| Host | Purpose | What is sent |
| --- | --- | --- |
| `curlbus.app` | Live bus arrivals (unofficial community service) | A stop code, and a descriptive `User-Agent` |
| `open-bus-stride-api.hasadna.org.il` | GTFS static stops and scheduled arrivals | A date, a stop code or id, and a time window |

Both are HTTPS only, use the operating system's default TLS (1.2 or later) with certificate validation always on, have redirects disabled, 10-12 second timeouts, and response size caps. Israel Railways and the Ministry of Transport feeds are not contacted; they stay disabled until an official key or endpoint is configured.

## Handling untrusted data

Every API response is treated as untrusted: it is parsed with `System.Text.Json` into strict models, strings are length-capped, coordinates must fall inside Israel, and timestamps must be plausible (impossible years, past arrivals and stale live observations are discarded). Response content is never rendered as markup or executed.

## Secrets

No keys or tokens are stored in the repository. A future Israel Railways key is read from configuration (`RailApiKey`, via `dotnet user-secrets` or environment variables). The key found in third-party client code on rail.co.il is intentionally not used. CI runs a gitleaks secret scan and a vulnerable-package check on every push.

## Not yet implemented

Search history storage, a "clear history" control, desktop notification delivery, and an installer. This document will be updated when they exist.

## Reporting a vulnerability

Please use GitHub's private vulnerability reporting (Security tab of this repository) instead of a public issue.
