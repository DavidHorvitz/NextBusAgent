# BusPulse Agent Execution Plan

The workstreams are isolated by ownership. Each agent opens a feature branch and submits a reviewed pull request; shared contracts are versioned in `BusPulse.Contracts` (or agreed interfaces) before parallel implementation. A human maintainer owns integration and release decisions.

## 1. Core Engine and API

**Owns:** `BusPulse.Core`, transit provider adapters, schedule and walking-time calculation.

**Tasks:** Integrate the OpenBus endpoint `https://open-bus-stride-api.hasadna.org.il/siri_rides/list` using `stop_code` and `limit=5`; verify its live response schema and canonical line-name fields; replace provisional field-name matching with a provider-specific model; calculate departure reminders from arrival time, walking duration, and configurable safety buffer; implement ETA-aware polling and inactive service windows; add retry/backoff, cancellation, and stale-data handling.

**Handoff:** Provider-neutral arrival contract, provider documentation/version, polling policy, and configuration fields. Never log raw tokens or full location data.

**Acceptance:** Contract tests cover the documented OpenBus response and line filtering; deterministic tests cover walk buffer boundaries, stale/late data, schedule changes, and polling intervals; malformed and oversized responses fail safely; cancellation promptly stops requests.

## 2. Security and Infrastructure

**Owns:** configuration protection, transport policy, packaging, CI/release hardening.

**Tasks:** Review DPAPI storage and file ACLs; decide whether credentials belong in Windows Credential Manager instead of app config; enforce TLS policy and endpoint allowlists as required; threat-model feed parsing and logs; create a per-user installer without elevation; add SBOM/dependency scanning and signing; evaluate a maintained, licensed obfuscator and configure it only for release builds.

**Handoff:** Threat model, configuration migration/versioning plan, installer identity/AUMID requirements, and documented release pipeline.

**Acceptance:** No secrets in source/build logs; tampered config fails closed with recoverable UX; install/uninstall works without admin; signed artifact and dependency report are reproducible. Obfuscation must not break WPF resources, toast activation, or diagnostics.

## 3. UI/UX and Localization

**Owns:** WPF dashboard, route/profile workflows, tray interactions, resources, toast experience.

**Tasks:** Complete origin/destination selection and named saved places such as Home; add address/stop search so users do not need to enter provider stop codes manually; display arrival, walk duration, and reminder time; add profile/settings management; verify dynamic English LTR and Hebrew RTL across the full shell; deliver active/muted/alert icon states; register desktop toast identity in per-user setup and define accessible notification behavior.

**Handoff:** Localized resource keys, tray/toast lifecycle, keyboard/accessibility behavior, and asset source files with licensing.

**Acceptance:** UI starts in tray, tray menu and double-click work, language changes without restart, Hebrew reading order is correct, and notifications respect mute/profile/time-window state.

## 4. QA, Testing, and Performance

**Owns:** `BusPulse.Tests`, integration test harness, performance and reliability gates.

**Tasks:** Add xUnit unit tests; use WireMock.Net for HTTP success, timeout, TLS/transport, status, malformed, oversized, and retry cases; test DPAPI on supported Windows runners; audit subscriptions/timers/disposal; profile idle and active memory/CPU; test offline recovery and repeated wake/sleep cycles.

**Handoff:** CI test matrix, baseline measurements, regression thresholds, and reproducible profiling notes.

**Acceptance:** Unit/integration suite passes on supported Windows versions; no unbounded response or retry behavior; idle CPU is effectively zero between scheduled work; working set target under 20 MB is measured rather than assumed, with documented OS/runtime variance.

## Integration Sequence

1. Confirm the OpenBus schema, line identity fields, and refresh guidance against a reachable development endpoint.
2. Core agent replaces provisional parsing with a typed contract; QA adds contract tests using recorded, sanitized sample responses.
3. UI wires the route/profile workflows and localized state to the contracts.
4. Security reviews provider, config, toast registration, and installer paths before packaging.
5. QA runs the full matrix and captures resource measurements; maintainers resolve findings before a signed release.

Branches are suggestions for a Git-enabled workflow, not created by this baseline-generation session.