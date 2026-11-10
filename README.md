# BusPulse

![Platform](https://img.shields.io/badge/platform-Windows%2010%2B-147D70)
![Framework](https://img.shields.io/badge/.NET-10%20LTS-512BD4)
![Status](https://img.shields.io/badge/status-baseline-orange)

BusPulse is a Windows desktop and system-tray monitor for Israeli public transport. It shows nearby stops with live or scheduled arrivals, is designed for per-user installation, and aims to give route-aware departure reminders with walk-time buffers.

> Work in progress, not a release. Stops, trip planning (direct trips only, including future dates), address search, lines, settings, alerts and the Fluent design pass are built; the popup and the first toast stage were seen on screen, but several screens and the amber toast stages are not yet checked, and the installer is still open. See [docs/PLAN.md](docs/PLAN.md) for scope, decisions and status.

## Current State

- WPF app on .NET 10 with MVVM (CommunityToolkit.Mvvm) and four tabs: Stops, Trip planner (direct trips only), Lines, Settings. Closing the window hides it to the tray. It lives in the notification area with active, muted and alert icon states; double-click opens it and the tray menu offers mute, profiles, settings and exit.
- **Stops tab:** uses the Windows Location API (or a saved place) to list the nearest stops with distances, expands a stop to its next arrivals marked "live" or "scheduled", shows the source and freshness in a tooltip, and counts down in place.
- **Live data:** Curlbus is the primary live source. Stride GTFS provides the schedule fallback and the stop catalog (cached daily under `%LOCALAPPDATA%\BusPulse\cache`). An aggregator picks the freshest valid data per line and rejects stale or impossible values. The Israel Railways provider is disabled until an official key is configured. The public Stride SIRI feed is stale and is not used. Measured results are in [docs/live-data-probe.md](docs/live-data-probe.md).
- **Trip planner:** the origin is your location, a saved place or a searched address; the destination is a saved place, any stop name or an address. Choose now, depart at or arrive by, on a date up to 60 days ahead. Today uses Stride plus Curlbus. Later dates use the official Ministry of Transport timetable (a file of about 155 MB, downloaded when needed, schedule only and never shown as live).
- **Address search:** optional, through the public OpenStreetMap Nominatim server, only when you press Search or Enter (at most one request per second). The typed text and your IP address reach OpenStreetMap; your device location does not. A Settings switch turns it off. Results are credited "© OpenStreetMap contributors". Saved places can be defined by an address (coordinates) instead of a stop code.
- English/Hebrew resource dictionaries switch at runtime; Hebrew applies right-to-left flow direction.
- Settings (including saved places) are serialized as JSON and encrypted with Windows DPAPI `CurrentUser` under `%LOCALAPPDATA%\BusPulse`.
- Alerts are wired to toasts, a full-screen popup and mute-aware sounds, but their on-screen delivery is unverified; unpackaged desktop toasts also need the app identity and Start Menu shortcut registration described in the plan.
- An optional read-only Google Calendar connection suggests trips from events whose location matches a saved place (needs your own OAuth client, supplied through Infisical or environment variables; see below and [SECURITY.md](SECURITY.md)).

## Architecture

```mermaid
flowchart LR
  UI[WPF views and tray] --> VM[ViewModels]
  VM --> AGG[Live data aggregator]
  VM --> LOC[Windows Location API]
  VM --> CAT[Stop catalog, daily cache]
  AGG --> CURL[Curlbus provider]
  AGG --> RAIL[Rail provider, disabled]
  AGG --> SCHED[Stride GTFS schedule]
  VM --> PLAN[Trip planner service]
  PLAN --> SCHED
  PLAN --> MOT[Ministry of Transport GTFS feed, future dates]
  VM --> GEO[Address search, Nominatim]
  VM --> SEC[Secret provider: Infisical, then environment]
  VM --> CFG[DPAPI settings store]
  RES[English and Hebrew dictionaries] --> UI
```

## Prerequisites

- Windows 10 (build 19041) or 11 x64. Network access uses the operating system's default TLS (1.2 or later).
- .NET 10 SDK and Windows Desktop Runtime (.NET 10 is the current LTS, supported until November 2028).
- Visual Studio 2022 with the .NET desktop workload, or VS Code with C# Dev Kit and the .NET SDK.
- Internet access. No API key is needed for the current sources (Curlbus, Stride, the Ministry of Transport timetable file, Nominatim). A saved place needs a stop code or coordinates (from an address search).
- Human decisions: provider and feed terms, supported service area, stop/route UX, notification policy, supported Windows versions, and signing/installer identity.
- For production toast notifications in an unpackaged app: register an AUMID and Start Menu shortcut during per-user setup. Do not request elevation for this.

### Optional environment variables

| Variable | Purpose |
| --- | --- |
| `BUSPULSE_GOOGLE_CLIENT_ID`, `BUSPULSE_GOOGLE_CLIENT_SECRET` | Google OAuth client (type Desktop app) for the Calendar connection |
| `BUSPULSE_INFISICAL_CLIENT_ID`, `BUSPULSE_INFISICAL_CLIENT_SECRET`, `BUSPULSE_INFISICAL_PROJECT_ID` | Read `GOOGLE_CLIENT_ID` and `GOOGLE_CLIENT_SECRET` from Infisical instead (Machine Identity with read-only access). Optional: `BUSPULSE_INFISICAL_ENV` (default `dev`), `BUSPULSE_INFISICAL_PATH` (default `/`), `BUSPULSE_INFISICAL_URL` (default `https://app.infisical.com`). Infisical is tried first, then the `BUSPULSE_GOOGLE_*` variables. Verified against the live project on 2026-11-10; setup steps are in `docs/SECRETS_SETUP.md` |

No credentials are needed to build the current scaffold. Do not place credentials in source control or plaintext config files. The current baseline encrypts its complete settings document with per-user DPAPI.

## Build and Run

From the repository root on Windows with the .NET 10 SDK installed:

```powershell
dotnet restore .\BusPulse.sln
dotnet build .\BusPulse.sln -c Release
dotnet test .\BusPulse.sln --filter "Category!=Live"
dotnet run --project .\BusPulse\BusPulse.csproj
```

Live probes against the real services run manually with `dotnet test --filter "Category=Live"`.

To publish a per-user, framework-dependent build:

```powershell
dotnet publish .\BusPulse\BusPulse.csproj -c Release -r win-x64 --self-contained false
```

The project builds in this environment with .NET SDK 10.0.401, targeting `net10.0-windows10.0.19041.0`.

## Security Notes

See [SECURITY.md](SECURITY.md) for stored data, contacted hosts and privacy rules.

- Provider calls use fixed HTTPS origins with the OS-default TLS, certificate validation on, redirects disabled, short timeouts and response size caps. Responses are parsed into strict models and validated (coordinates inside Israel, plausible timestamps) before use.
- DPAPI `CurrentUser` protects settings for the current Windows profile. It is not a substitute for least-privilege ACLs, secure backups, or secret rotation.
- There is no installer or elevation manifest in this baseline. Keep installation in the user's profile and avoid machine-wide registry/file locations.
- Obfuscation is not enabled. Select a maintained, licensed obfuscator and validate signing, crash reporting, and false-positive behavior before adding a release pipeline.

## MCP Workflow Setup

MCP integrations are a workflow proposal, not configured servers in this repository. Add only trusted servers in the user's VS Code MCP configuration and review each server's filesystem, Git, network, and terminal permissions.

1. Use a filesystem server scoped to this repository for scaffold changes; review diffs before accepting generated files.
2. Use Git/GitHub servers for issue tracking and pull requests. Create separate feature branches for the four workstreams, then merge only through reviewed pull requests. Do not let an agent commit secrets or generated binaries.
3. Use terminal tooling for `dotnet restore`, `dotnet build`, `dotnet test`, and package auditing; capture results as CI artifacts.
4. Use fetch/documentation tooling against the selected agency's official SIRI/GTFS-RT documentation. Pin schema/spec versions and keep source URLs with generated models.
5. Keep credentials in user secrets or a credential store. Never pass production tokens to an MCP server unless its trust and retention policy has been reviewed.

## Delivery Plan and Assets

See [docs/PLAN.md](docs/PLAN.md) for the merged plan, decisions, status and next steps, [docs/audit.md](docs/audit.md) for the requirement audit, and [docs/assets.md](docs/assets.md) for tray icon and audio requirements. Icons (`bus_active`, `bus_muted`, `bus_alert`) and sounds (`arrival.wav`, `delay.wav`) are original, generated by `tools/generate_icons.py` and `tools/generate_sounds.py` into `BusPulse/Assets`.
