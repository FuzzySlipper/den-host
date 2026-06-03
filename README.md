# den-host

The harness-agnostic machine-local Den agent/runtime host.

`den-host` adapts local runtime/harness reality to Den-owned Core/Channels
contracts. It does not define Den workflow truth, it does not own canonical
task/assignment/lease/run state, and it does not leak harness internals
(Hermes, Pi, Codex, Claude Code, OpenCode) into Core or Channels.

## Boundary

```
Core
  workflow truth: tasks, assignments, leases, runs, pool members,
  completion/block/failure, release/quarantine, binding projections

Channels
  operations conversation: rooms, messages, activity, direct-agent event
  creation, checkpoint/completion visibility, source context, target work

den-host                              (this repo)
  machine-local config, endpoint discovery, local agent inventory,
  harness modules, process/session/wake mechanics, heartbeat/status,
  run-dir/PID/log reconciliation, cleanup/quarantine evidence

Harness modules                       (separate assemblies, firewall below)
  Hermes, Pi-style helper/actor runtime, Codex CLI, Claude Code, OpenCode,
  future Den-native actor runtime
```

The host speaks to Core and Channels only over HTTP, using the generic
adapter-facing contract. It does not know about Hermes profiles, session
keys, plugin quirks, or any other harness-internal concept.

## Non-goals

- `den-host` is not a second Core. Workflow truth stays in Core.
- `den-host` does not own canonical task/assignment/lease/run/completion state.
- `den-host` does not move user-visible conversation policy out of Channels.
- `den-host` does not leak Hermes/Pi/Codex/OpenCode/Claude Code internals
  into Core or Channels.
- `den-host` does not require ordinary agents to know LAN IP/port topology.
- `den-host` does not move Gateway broker semantics wholesale into the host.

## Build

Requires .NET 10 SDK.

```bash
dotnet build
dotnet test
```

## Configure

Copy the example and adjust:

```bash
cp config/den-host.example.json den-host.json
```

Override the config path with `--config <path>` or the `DEN_HOST_CONFIG`
environment variable. The example uses loopback URLs so the host does not
require agents to know LAN topology.

## Run

```bash
# One-shot health check
dotnet run -- health

# One-shot health check, JSON
dotnet run -- health --json

# Run as a service (foreground; Ctrl-C to stop)
dotnet run -- run

# Print version
dotnet run -- version

# Show CLI help
dotnet run -- help
```

## Architecture

- `src/DenHost/` — main host. Generic Host / Worker Service style binary
  with a small CLI/health surface. Depends on `DenHost.Harness` only.
- `src/DenHost.Harness/` — the harness firewall assembly. Defines
  `IHarnessModule`. Future Hermes/Pi/Codex/Claude Code/OpenCode modules
  depend on this assembly; `DenHost` does not depend on any of them.
- `tests/DenHost.Tests/` — xUnit test project.

### Configuration layout (`den-host.json`)

| Section    | Purpose                                                         |
|------------|-----------------------------------------------------------------|
| `Adapter`  | Identity of this host as seen by Core/Channels.                 |
| `Core`     | Core endpoint: base URL, health path, binding path, timeout.    |
| `Channels` | Channels endpoint: base URL, health path, events path, timeout. |
| `Runtime`  | Local filesystem layout: run/state/log/quarantine dirs.         |
| `Harness`  | List of configured harness modules (name, kind, settings).      |

### Harness firewall

All concrete harnesses (Hermes, future Pi/Codex/Claude Code/OpenCode, future
Den-native actor runtime) implement `DenHost.Harness.IHarnessModule`. Each
concrete harness lives in its own assembly, depends only on
`DenHost.Harness`, and reads its own opaque `Settings` from configuration.
`DenHost` never references a concrete harness assembly.

## Status

This is the bootstrap deliverable for den-host task #1914. It ships:

- repo skeleton, build, test
- Generic Host / Worker Service entry point
- config loader with options binding + `ValidateOnStart`
- example non-secret config
- Core/Channels typed HTTP clients (health probe, binding register/readback,
  direct-agent event read)
- `den-host health` CLI command (text and JSON output)
- harness module firewall assembly with a stub implementation
- background service for `den-host run` (heartbeat log only for now)

Tasks #1915, #1916, #1917, #1918 add: live adapter binding heartbeat,
Channels direct-agent shadow reader, real Hermes harness module, and
worker run/process/session reconciliation with quarantine evidence.

## References

- `den-host/project-brief` — boundary, non-goals, design-smell checklist
- `den-core/direct-delivery-local-adapter-boundary` — Core contract
- `den-core/den-bridge-strangler-repo-proposal` — historical naming
- `den-core/gateway-simplification-planner-assessment-2026-06`
- `den-core/gateway-architecture-review-2026-06`
- `den-core/worker-pool-operating-model`
- `den-core/agent-runtime-terminology-glossary`
