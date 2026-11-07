# HIC P0 — Local runner authority and admission checkpoint (v1)

**Status:** design and pure validation code only. Not an executable local runner. Neither a browser nor GitHub is an authority to run a task. No existing `/approve` endpoint is made safe or enabled by this document.

## Existing implementation inventory (develop, 2026-11-07)

| Surface | Actual behavior | Security boundary |
| --- | --- | --- |
| Control Plane `GET /tasks`, `GET /tasks/{id}` | Read task rows from PostgreSQL | API presently has no durable user authentication |
| Control Plane `POST /tasks` | Create PostgreSQL `Draft` | Vite Command Center proxy restricts origin/method/size, but direct CP loopback API is not an authenticated authority |
| Control Plane `POST /tasks/{id}/approve` | Transition task to Approved and push a task ID to Redis `agentlab:task-queue` | **Currently unauthenticated** — MUST NOT be treated as human approval for the new runner |
| Control Plane `POST /tasks/{id}/retry` | Transition Failed task and enqueue in Redis | Same unresolved authentication/authorization boundary |
| Control Plane `POST /tasks/{id}/status` | Task transition with optional execution identity | No authenticated caller identity; R2 protects ownership/generation but not caller authority |
| Control Plane `POST /tasks/{id}/executions/claim` | Atomic execution claim/generation validation | Existing R2 worker-facing path; not a human approval receipt |
| Control Plane `GET /diagnostics/local` | Read-only PostgreSQL/Redis health, Worker not checked | No command/agent execution |
| Native Host `POST /v1/actions/run` | Ephemeral local token, fixed `workspace.inspect` only | Read-only inspection; **no TaskId, approval, durable claim, arbitrary command or LLM execution** |
| Web UI Command Center | Browser-local unsent text; explicit POST creates Draft only | No UI execution/approval flow |
| `agent-worker` | Legacy Worker runtime | Deliberately stopped; **not** the proposed native runner |

## Authority model to implement next

1. **Identity:** Control Plane receives a verifiable local human identity; never accept arbitrary actor IDs or `approved=true` fields from browser JSON as proof. Authenticate using a dedicated local authority boundary before enabling approval endpoints. Native proxy token is *not* a human credential.
2. **Plan and scope:** Human sees exact `taskId`, canonical action, configured workspace scope, expected task version, no unbounded arguments and a short expiry. The initial admissible action is **`workspace.inspect`**, scope **`workspace:configured:read`** only.
3. **Persist approval:** A new, separately reviewed **schema v3** will need an immutable, auditable approval ledger: `approvalId`, `taskId`, `runId`, `taskVersion`, `actorId`, action, scope, canonical intent digest, issued/expiry, decision, authority key identifier and revocation epoch. This is a **proposal**, not an existing table. Do not mutate schema v2 automatically.
4. **Authenticity:** The local runner must receive an **authenticated, audience-bound approval receipt** from the authoritative CP, not from an unchecked UI field, GitHub Issue/PR comment, webhook, model response or the unauthenticated legacy `/approve`. Dedicated signing key storage/rotation/verification and local loopback boundary are still to be designed and reviewed. Never log raw receipts or secrets.
5. **Single-run claim:** Within the same authoritative store, atomically compare task version/approval digest and claim exactly one `runId` / execution generation. A replayed, expired, cancelled, revoked or mismatched claim returns a deterministic rejection. R3 heartbeat/lease, R4 crash reconciliation and transactional queue/outbox recovery must be designed before broader execution.
6. **Executor:** Only after authority and durable replay boundaries pass may a local runner call the existing fixed, read-only action. No shell string, parameterized command, arbitrary path, env access, repository mutation, file export, network transfer, model call or Worker queue dispatch in v1.
7. **Audit:** Append decision/denial, authenticated actor reference, task/run/version, action digest, scope, approval epoch, outcome and sanitized timing. Never include task text, raw credentials or machine environment. Revocation and cancellation must have defined, tested race semantics.

## Contract prototype in source (not wired)

`src/NativeHost/NativeRunAdmissionPolicy.cs` defines a **pure function** accepting `ProposedNativeRun` and a *hypothetical trusted internal* `VerifiedNativeApproval` snapshot. It checks fixed action/scope, no arbitrary arguments, nonempty task/run/actor IDs, positive task version, matching binding, identity/proof/persistence indicators, a five-minute maximum grant lifetime, and an exclusive run claim indicator. Results are **structural eligibility codes, never permission to execute**. The Host does not expose this as an HTTP endpoint and no code constructs these snapshots from live inputs.

The four evidence booleans are NOT evidence by themselves. A future implementation must compute them from actual verified credentials, persisted approval and atomic claim under an authenticated authority, not deserialize them from caller-controlled JSON. Until then, **task execution stays disabled**.

## Proposed API, NOT implemented

| Future endpoint | Preconditions and intended result |
| --- | --- |
| `POST /tasks/{id}/run-proposals` | Authenticated read-only-scope proposal bound to current version; no execution |
| `POST /tasks/{id}/run-approvals` | Explicit human confirmation; authenticated actor + durable audited receipt; **not** legacy `/approve` |
| `POST /tasks/{id}/native-runs/claim` | Internal authenticated single-use claim after rechecking approval, task version and expiry |
| `GET /tasks/{id}/native-runs/{runId}` | Authenticated state/output metadata; sanitized logs |
| `POST /tasks/{id}/native-runs/{runId}/cancel` | Authenticated best-effort cancel + durable cancellation record |

Proposed state progression: **Draft → proposed read-only action → human approval receipt → claimed → running → completed/failed/reconciled**, preserving the original `tasks` authority and modeling runner-specific audit separately. A read-only proposal is never an Approved/Executing task in the legacy Worker queue.

## Adversarial acceptance gates before wiring native execution

- No valid human auth → reject; browser origin checks and an ephemeral native token alone are insufficient.
- Unverified approval, replay, stale task version, wrong task/run/actor, wrong scope or wrong action → reject.
- Invalid argument, arbitrary shell/path, case-changed action name or unexpected JSON field → reject.
- Expired/future-dated grant or grant longer than five minutes → reject.
- Duplicate delivery, concurrent claim, Worker crash, Redis/CP outage, cancellation race, log injection and disclosure → deterministic fail-closed and durable reconciliation.
- User task data and PostgreSQL schema v2 are preserved; no migration, Podman restart, background agent, PR merge or paid API call is permitted by a proposal.
- PR review must verify **native status still says `taskExecutionEnabled=false`** and `workspace.inspect` remains the only current Native Host action.

**Next engineering slices:** authenticated local human identity + durable approval schema v3 design, persistent replay/claim storage and local crash recovery, then a genuine UI consent flow and a runner that can perform only `workspace.inspect` for an approved task. This work can continue in GitHub without requiring PC access for the design/build/test phase; actual legacy R2 rollout needs separate on-device validation.
