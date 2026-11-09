# HIC Managed Stack — reviewed on-demand control infrastructure

`hic.cmd up` (or double-clicking `hic.cmd`) reuses a verified running preview, or prints a default Light profile plan. It does not initialize or start infrastructure without a separately reviewed plan ID and `-Apply`. This repository change does not deploy anything on its own.

## Behavior by environment

| Environment | HIC action |
| --- | --- |
| Existing healthy configured or legacy Native Host + UI pair | Reuse the verified preview; do not query or change Podman on the Light fast path |
| `profile plan -Profile Light -Task ui` | Show only Native Host + Web UI; no service starts |
| Reviewed Light UI plan + `resource start ... -PlanId <id> -Apply` | Start the isolated Native Host/UI; occupied or partial sessions block |
| Existing HIC-managed Development stack | Verify manifest/container IDs, project/service labels, mounts and credential digest; resume only exact owned services |
| Completely clean Podman inventory and reviewed Development/API plan | May create a new isolated HIC-managed PostgreSQL, Redis and Control Plane; no pre-existing containers, volumes, DB listener or ownership state may exist |
| Any external/legacy database, containers, volumes, or stale ownership state | Protected; do not adopt, recreate, stop or reset. External DB reuse is not configured in Phase 1 |
| Podman machine stopped, missing or unresponsive | Report unavailable; never start the machine or change WSL networking |
| Partial managed installation, changed images/identity, unknown port bindings | Fail closed; no deletion or recreation |
| Worker | Never started by managed Compose |

Dedicated manifest: deploy/hic-managed/compose.yaml. It defines exactly three always-on services (plus one explicit, profiled one-shot schema-init service) and publishes only Control Plane on 127.0.0.1 using HIC_CONTROL_PLANE_PORT (default 19480). PostgreSQL (5432) and Redis (6379) are available only inside its container network. Native Host defaults to 19481 and UI to 19482; these host ports remain configurable.

## Private state

A reviewed new, empty installation creates a random 256-bit database password and writes it outside Git under %LOCALAPPDATA%\HumanInControl\managed\runtime.env. After successful provisioning and health checks, the file manifest.json in the same folder records managed container/image IDs and CP port for identity-based resume. Never commit the credentials, post them to tickets or expose the runtime.env file to others. A resource plan checks credential/manifest file presence only; it never returns credential contents.

The initial **brand-new empty** database is initialized with base tables and the canonical v2 execution-ownership SQL inside a single PostgreSQL transaction, through the opt-in `schema-init` profile. The bootstrap SQL refuses any pre-existing public tables. It runs **only** during fresh managed provisioning, never on resume. This is NOT a migration of an existing R2 database. The script never runs broad Compose up/down against historic resources, never cleans up partially created volumes, never replaces the Control Plane image during resume, and never starts Worker.

## Prerequisites and limitations

Windows with .NET 10 SDK, Node/npm, Podman with Compose, and an already-running Podman machine are required for an explicit managed-stack start. A stopped Podman machine or absent tools are not silently initialized/installed. Port conflicts and unexpected legacy/partial resources block infrastructure startup. The user may still use Native Host/UI with reduced functionality after reviewing its Light plan.

Podman on Windows may have host/WSL networking behavior that differs from Compose metadata; verify actual exposure before treating the setup as hardened. The Control Plane API currently lacks durable authenticated task approvals, so this is a LOCAL DEVELOPMENT stack, not a production deployment or an arbitrary-agent sandbox.

Failures leave containers and persistent data unchanged for troubleshooting; no rollback or destructive cleanup is automatic. For pre-existing R2 environment changes follow Issue #13's separate read-only preflight and approval gate. The source CI checks launcher policy; real Windows/Podman runtime behavior has NOT been verified here.

Useful read-only inspection: `hic.cmd resource status`, `hic.cmd resource doctor`, `hic.cmd profile plan`, `hic.cmd status`, `hic.cmd doctor` and `hic.cmd recovery-plan`.


## Schema v2 first-start gate

The Control Plane intentionally rejects a PostgreSQL schema without `app_schema_versions` version 2. New managed PostgreSQL volumes therefore require the following sequence: **create new PostgreSQL/Redis containers → execute guarded one-shot schema-init → start Control Plane**. The canonical `src/ControlPlane/Migrations/002-execution-ownership.sql` is mounted read-only into the bootstrap container; no migration copy is maintained. Any failed bootstrap leaves new resources intact and the launcher fails closed rather than automatically deleting/retrying. In particular, an existing or legacy R2 database never receives this bootstrap SQL.
