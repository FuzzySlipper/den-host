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
`DenHost` references the firewall assembly (`DenHost.Harness`) and the
first concrete module assembly (`DenHost.Harness.Modules.Hermes`); the
factory in `AddDenHost` is the only place that names a concrete
`HermesHarnessModule`. Everything else uses `IHarnessModule`.

The interface covers the points required by den-host task #1917:

- `Name`, `Kind`, `IsAvailable()` — identity and cheap readiness check.
- `GetCapabilitiesAsync` — generic Den-facing role + capability tokens.
- `GetLocalInventoryAsync` — generic pool-member descriptors.
- `WakeAsync(envelope)` / `StopAsync(handle)` / `CollectEvidenceAsync(handle)` —
  launch and lifecycle hooks driven by a generic `WakeEnvelope` and
  `WorkerHandle`. The harness module is free to interpret the envelope
  internally (Hermes translates it to `hermes --profile ... --role ...
  --project ...`); den-host never sees those flags.
- `ResetSessionAsync(handle)` — module-specific session cleanup (Hermes keeps
  it as a no-op stub for now; its actual session reset is internal).
- `SmokeAsync` — bounded smoke used by `den-host smoke <module>`. Default
  reports "not implemented"; the Hermes module runs `hermes --version`.

The Hermes module lives in `src/DenHost.Harness.Modules.Hermes/`. It shells
out to the `hermes` CLI as a child process — the Python interpreter
never enters the den-host process. The silo is the OS process boundary.

### Adding a future harness module (Pi / Codex / Claude Code / OpenCode)

1. Create `src/DenHost.Harness.Modules.<Kind>/DenHost.Harness.Modules.<Kind>.csproj`
   with a single project reference to `DenHost.Harness.csproj`. Do NOT
   reference `DenHost.csproj` from the module.
2. Add a `public sealed class <Kind>HarnessModule : IHarnessModule` that
   implements the methods you support and throws `NotSupportedException`
   for the rest (the interface provides default impls where reasonable).
3. Add a project reference from `DenHost.csproj` to the new module
   assembly and a `case HarnessModuleKind.<Kind>:` branch in the factory
   inside `AddDenHost` that builds the concrete module. No changes to
   Core/Channels are required.
4. Add a `<Kind>` module to `den-host.json`:

   ```json
   "Harness": {
     "Modules": [
       { "Name": "<kind>-default", "Kind": "<Kind>", "Enabled": true,
         "Settings": { /* opaque to DenHost, parsed by the module */ } }
     ]
   }
   ```

5. Add a smoke that verifies the module can launch its underlying harness
   on this host. The Hermes module pattern (`SmokeAsync` runs the binary's
   `--version`) is a good template.

The harness firewall is preserved as long as Core/Channels-facing types
(`AdapterIdentity`, `ProbeResult`, `WakeEnvelope`, `WorkerHandle`,
`HarnessSmokeResult`, ...) stay generic. The concrete module can do
anything inside its own assembly; den-host does not look at it.

## Status

This is the bootstrap deliverable for den-host tasks #1914 through #1917.
It ships:

- repo skeleton, build, test
- Generic Host / Worker Service entry point
- config loader with options binding + `ValidateOnStart`
- example non-secret config
- Core/Channels typed HTTP clients (health probe, binding register/readback,
  direct-agent event read)
- `den-host health` CLI command (text and JSON output)
- harness module firewall assembly with the `IHarnessModule` interface
  (capabilities / inventory / wake / stop / evidence / reset / smoke)
- Hermes harness module in its own assembly, silo'd at the process
  boundary (no Python in den-host); `den-host smoke hermes-default` runs
  the bounded `hermes --version` smoke
- adapter binding heartbeat (`den-host binding`, `den-host run` background
  service) with blocker evidence when Core lacks the binding endpoint
- Channels direct-agent shadow reader (`den-host events tail`,
  `den-host run` background service) that never mutates Core/Channels
  and never launches a worker; logs migration-diff notes for cutover
  comparison

Task #1918 (worker run/process/session reconciliation and quarantine
evidence) is the remaining item in the den-host starting task set.

## References

- `den-host/project-brief` — boundary, non-goals, design-smell checklist
- `den-core/direct-delivery-local-adapter-boundary` — Core contract
- `den-core/den-bridge-strangler-repo-proposal` — historical naming
- `den-core/gateway-simplification-planner-assessment-2026-06`
- `den-core/gateway-architecture-review-2026-06`
- `den-core/worker-pool-operating-model`
- `den-core/agent-runtime-terminology-glossary`
