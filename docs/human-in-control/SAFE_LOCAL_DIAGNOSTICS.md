# HIC — Safe Local Diagnostics (Issue #10: first vertical slice)

## Purpose

**Humans are not message queues.** The operator can trigger a *fixed* local health check from the HIC Command Center without copying a prompt into Fusion or opening a shell. This is an intentionally narrow, deterministic preview. It is **not** arbitrary command execution, the final Local Runner, or proof that agent execution governance is complete.

## Current contract

- Control Plane `GET /diagnostics/local` runs only `system.health`.
- The handler executes `SELECT 1` against its existing PostgreSQL connection and `PING` against its existing Redis connection, each with a bounded timeout.
- Response fields: `action` (`system.health`), `checkedAtUtc`, `controlPlane` (`ok`), `postgres` and `redis` (`ok` / `unavailable`), `worker` (`not-checked`).
- No credentials, connection strings, raw exception text, task contents, model prompts or process metadata are returned.
- The Web UI's loopback-only dev proxy permits **only GET** `/api/diagnostics/local` in addition to its prior allowlist. It does not expose any new POST action, task approval, shell runner or model endpoint.
- The Command Center displays a **Run local diagnostics** button, a real result and explicit unavailable/unknown states. It runs only on a user click; a dashboard refresh does not trigger this probe.

## Safety boundaries

- No Worker start, LLM inference, arbitrary shell, mutation, task creation, DB migration, container restart, GitHub write or deployment.
- Results are transient: **no task ID, execution ID, durable audit event, cancellation or replay guarantee**. Do not label this an approved agent execution. This operation is a bounded health query, not an execution engine.
- `/health` and this probe cannot prove Worker or external model readiness; Worker is always `not-checked` until a trustworthy R3/R4-backed signal exists.
- The Control Plane presently lacks authentication; expose this endpoint **only on explicit loopback-only bindings**. A Vite dev proxy is not an auth boundary. Do not deploy via the repository's generic broad-port compose mapping.
- The source change does not automatically update existing containers. Updating the running Control Plane image and local UI is a **separate operator-approved deployment** with existing backup/rollback protections.

## Verification gates (must run before merge/deployment)

1. `dotnet build` Control Plane and relevant offline tests under .NET 10; verify no integration fixture is pointed at user data.
2. HIC Web UI: `npm ci`, `npm run build`, `npm test`.
3. Frontend tests must prove no automatic invocation, no task creation, negative/error state, strict JSON guard, and GET-only proxy allowlist.
4. In an isolated runtime, verify PostgreSQL/Redis available/unavailable paths and bounded timeouts. Capture safe status-only evidence.
5. After independent deployment approval, verify existing HIC UI and tasks unchanged; no Worker started; diagnostics results correct.

**Not yet verified in this PR:** local .NET/npm tests, live endpoint or the running environment. Do not merge or deploy solely based on static inspection.

## Next stages — still required to resolve Issue #10

1. Versioned, explicit operator identity and action-scoped approval stored in existing authoritative Control Plane, with audit trail.
2. A separate local runner process/adapter consuming only approved, typed action contracts; deny arbitrary shell instructions; no second task authority.
3. R3 leases/heartbeat and R4 crash/restart reconciliation plus idempotency and negative tests.
4. HIC task execution UI, progress/result view, controlled cancellation, and local-first model routing behind independent authorization and isolation.
5. Independent bootstrap / self-healing, so Fusion/ChatGPT is never a runtime prerequisite.

**The diagnostics slice does not fulfill these stages and should not close Issue #10.**
