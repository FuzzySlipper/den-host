# Task 3164 legacy route retirement audit

Date: 2026-06-22

## Scope

Audited den-host for legacy den-channels direct-agent/channel endpoint assumptions before den-channels retirement, and checked whether den-host can be retired now that Den Web no longer proxies FleetOps.

## Findings

- `den-host.service` (`den-host run`) is inactive. The worker wake supervision plane is not live.
- `den-host-fleetops.service` is the only active den-host user service on this host; it runs `/home/agents/local/den-host/bin/den-host serve` and listens on `0.0.0.0:5400`.
- Direct FleetOps endpoints still answer locally (`/api/host/health`, `/api/host/fleet-ops`), but Den Web no longer proxies `/den-host-api/*`; live LAN smoke to `http://192.168.1.10:18080/den-host-api/fleet-ops` returns `404 legacy_api_not_found`.
- Searches of active `den-web`, `den-desktop`, `pi-crew`, `rusty-crew`, and `den-services` sources found no current caller that requires den-host/FleetOps. `den-core` retains historical/runtime-substrate wording and worker records that mention den-host, but no active den-host launch dependency was found.
- Rusty Crew documentation already describes Rust-owned coordination/session/worker lifecycle, which matches the desired future direction better than reviving den-host as a generic supervisor.

## Code/docs changes in this branch

- Updated example config so normal operation does not use port `18081`, `/api/direct-agent-events`, channel subscriptions, or Gateway catch-all routes.
- Disabled legacy Channels event polling by default (`Runtime:ChannelsEventPollSeconds = 0`).
- Made Channels direct-agent/readback/lifecycle paths empty by default.
- Reworded README and code comments to describe remaining Channels client code as optional cold-history diagnostics, not active worker wake coordination.
- Added regression tests that default options do not enable legacy direct-agent routes or polling.

## Retirement recommendation

den-host appears safe to retire from the active Den Web/operator path after one final controlled service shutdown smoke:

1. Stop and disable `den-host-fleetops.service` on den-k8.
2. Confirm `ss -Htnlp sport = :5400` is empty.
3. Confirm Den Web still returns `404 legacy_api_not_found` for `/den-host-api/fleet-ops`.
4. Confirm no active Den task/worker orchestration depends on den-host-specific runtime launch state; current worker runs are `external` substrate records and Pi/Rusty Crew is the desired future coordination path.

This task does **not** stop the live service automatically because it is an operational cutover. The code/doc branch removes the active legacy-route assumptions so the service can start with den-channels unavailable, and the remaining retirement is a service-disable/deploy cleanup task.
