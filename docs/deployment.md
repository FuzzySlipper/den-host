# Den Host deployment

This document describes the **current live deployment model on den-k8plus** and the safe way for agents to update Den Host without rediscovering the old privileged install path.

## Current live layout

Den Host now runs as `agent` user services, not as root-owned system services.

| Item | Current path / state |
| --- | --- |
| Binary | `/home/agents/local/den-host/bin/den-host` |
| Release copies | `/home/agents/local/den-host/releases/<timestamp>/` |
| Current release symlink | `/home/agents/local/den-host/current` |
| Runtime root | `/home/agents/runtime/den-host/` |
| Runtime dirs | `/home/agents/runtime/den-host/{run,state,log,quarantine}` |
| User units | `/home/agent/.config/systemd/user/den-host.service` and `den-host-fleetops.service` |
| Config | `/home/dev/den-host/den-host.json` |
| Secret env | `/etc/den-host.env` (`root:agents`, readable by `agent`; do not print contents) |
| FleetOps endpoint | `http://127.0.0.1:5400/api/host/fleet-ops` |
| Health endpoint | `http://127.0.0.1:5400/api/host/health` |

The old system units should remain disabled on den-k8plus:

```bash
systemctl is-active den-host.service den-host-fleetops.service || true
systemctl is-enabled den-host.service den-host-fleetops.service || true
```

Expected state for both system units: `inactive` and `disabled`.

## Why this layout exists

Den Host primarily hosts and manages Hermes fleet operations for the `agent` account. FleetOps needs to see and control `systemctl --user` units such as `hermes-gateway@den-mcp-runner.service`. Running Den Host/FleetOps as a system service lacked the `agent` user bus environment and caused FleetOps discovery to return `serviceUnits: []`.

Running Den Host as `agent` fixes discovery and removes the need for sudo during normal deployments.

## Normal no-sudo deployment flow

Use this for routine Den Host updates after a build has been reviewed/approved.

### 1. Build the publish artifact

From the repo root:

```bash
cd /home/dev/den-host
scripts/deploy-den-host.sh --build-only --with-fleetops
```

The script prints a publish directory like:

```text
/tmp/den-host-live-publish.XXXXXX
```

Caution: publish directories created by another profile may be mode `700`. If the deploying agent cannot read the directory, have the artifact creator stage a readable handoff copy or ask sysadmin to stage one. Do **not** loosen secrets or print env files.

### 2. Stop the user services before replacing the binary

Stopping first avoids `Text file busy` and ensures the new binary is used after restart.

```bash
uid_agent=$(id -u agent)
sudo -n -u agent env \
  XDG_RUNTIME_DIR=/run/user/$uid_agent \
  DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/$uid_agent/bus \
  systemctl --user stop den-host.service den-host-fleetops.service
```

If you are already running as `agent`, the command can be shortened to:

```bash
systemctl --user stop den-host.service den-host-fleetops.service
```

### 3. Install the new binary into the agent-owned live path

Replace `/tmp/den-host-live-publish.XXXXXX` with the actual publish directory.

```bash
publish=/tmp/den-host-live-publish.XXXXXX
stamp=$(date +%Y%m%d-%H%M%S)

sudo -n -u agent mkdir -p \
  /home/agents/local/den-host/bin \
  /home/agents/local/den-host/releases/$stamp

sudo -n -u agent cp -a "$publish"/. /home/agents/local/den-host/releases/$stamp/
sudo -n -u agent install -m 0755 "$publish/den-host" /home/agents/local/den-host/bin/den-host
sudo -n -u agent ln -sfn /home/agents/local/den-host/releases/$stamp /home/agents/local/den-host/current
```

### 4. Start and verify the user services

```bash
uid_agent=$(id -u agent)
sudo -n -u agent env \
  XDG_RUNTIME_DIR=/run/user/$uid_agent \
  DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/$uid_agent/bus \
  systemctl --user daemon-reload

sudo -n -u agent env \
  XDG_RUNTIME_DIR=/run/user/$uid_agent \
  DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/$uid_agent/bus \
  systemctl --user start den-host.service den-host-fleetops.service

sudo -n -u agent env \
  XDG_RUNTIME_DIR=/run/user/$uid_agent \
  DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/$uid_agent/bus \
  systemctl --user is-active den-host.service den-host-fleetops.service
```

Expected output:

```text
active
inactive
```

Then verify HTTP health and FleetOps discovery:

```bash
curl -fsS http://127.0.0.1:5400/api/host/health
curl -fsS http://127.0.0.1:5400/api/host/fleet-ops \
  | python3 -c 'import json,sys; d=json.load(sys.stdin); print(len(d.get("serviceUnits") or [])); print([a.get("actionId") for a in d.get("actions") or []])'
```

Expected:

- health returns JSON with `status: healthy`;
- FleetOps `serviceUnits` is non-empty on den-k8plus;
- FleetOps actions include `fleet-status`.

Optional non-mutating FleetOps smoke:

```bash
curl -fsS -X POST http://127.0.0.1:5400/api/host/fleet-ops/actions/fleet-status/runs \
  -H 'Content-Type: application/json' \
  --data '{"actionId":"fleet-status","dryRun":true,"args":{}}' \
  | python3 -m json.tool
```

Expected: `status: completed`, `exitCode: 0`, and a dry-run listing of discovered Hermes gateway units.

## One-time cutover and rollback scripts

The 2026-06-05 migration from system units to user units staged helper scripts here:

```text
/home/agents/local/den-host/scripts/cutover-to-user-service.sh
/home/agents/local/den-host/scripts/rollback-to-system-service.sh
```

Use the rollback script only if the user-service deployment is broken and you need to temporarily return to the old system units:

```bash
/home/agents/local/den-host/scripts/rollback-to-system-service.sh
```

After rollback, verify health and remember that FleetOps user-unit discovery may regress to `serviceUnits: []` because the service is no longer running inside the `agent` user manager.

## Legacy deploy script warning

`scripts/deploy-den-host.sh --install-from ...` was originally written for a root-owned system install:

- `/usr/local/bin/den-host`
- `/var/lib/den-host`
- `/etc/systemd/system/den-host*.service`
- `/etc/den-host.env`

Do **not** use that old install mode on den-k8plus for normal deployments unless you intentionally want to revert to a privileged system-service deployment.

A future improvement should add an explicit script mode such as:

```bash
scripts/deploy-den-host.sh --install-from /tmp/den-host-live-publish.XXXXXX --user-service --with-fleetops
```

Until that exists, use the manual user-service install steps above.

## Endpoint note

FleetOps-only den-host needs Core only for health/binding diagnostics. Do not
configure legacy den-channels direct-agent, channel subscription, or Gateway
catch-all routes for active operation. If old Channels evidence must be
inspected, set the `Channels` paths deliberately for that cold-history session
and keep `Runtime:ChannelsEventPollSeconds` at `0` unless running a bounded
diagnostic.

## Known non-deployment warning

`den-host run` is retired/inactive in the normal FleetOps-only deployment. Do not diagnose its inactivity as a failed deployment unless a task explicitly revives the worker-supervision plane.

## Operational checklist

Before marking a deployment complete, capture:

1. publish directory and binary checksum;
2. active/enabled state of `den-host-fleetops.service` and inactive state of retired `den-host.service`;
3. inactive/disabled state of the two legacy system units;
4. health endpoint output;
5. FleetOps service-unit count;
6. result of the `fleet-status` dry-run action;
7. rollback path if anything fails.
