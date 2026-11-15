# Issue #159 — implementation gate and test matrix

Status: **Design checkpoint; NOT an implemented or enabled handoff**.
Companion: `docs-md/dev-tools/hic-159-relay-handoff-adr.md`.

## Confirmed source behavior (branch feature/hic-159-relay-handoff-contract)
- `src/WebUI/src/CommandCenter.tsx`: `api.createTask` creates a Draft only; uncertain network writes block blind retries.
- `src/ControlPlane/Program.cs`: `POST /tasks` persists Draft; `POST /tasks/{id}/approve` enqueues `agentlab:task-queue`. Do **not** call approve/retry or reuse that queue for handoff.
- Local operator sessions are scoped to local runner; they are not evidence of task ownership or generic dispatch authorization.
- Existing staging observations are untrusted and non-executable.

## Evidence from local legacy developer relay (user-inspected 2026-11-15)
- `tools/dev-handoff/zero-prompt-relay/src/relay.mjs` `githubContent` uses an unauthenticated GitHub Contents GET. HTTP 403/429 maps to `github-rate-limited-or-forbidden`.
- `Run-DevRelay.ps1 -Mode AckWatch` is a synthetic Issue #97 acknowledgement workflow; it is **not** the production HIC task handoff.
- Windows scheduled task `HIC-Developer-Relay-Ack-OptIn` last ran 2026-11-12 and is Ready, not Running. Local files show four ACK and four staging-observed records. This is not proof of current Relay delivery.
- Do not require a personal PAT or restart the legacy watcher as part of #159.

## Implementation sequence (default OFF)
1. **Resolve transport**: identify the actual staging Relay inbox, authenticated principal, and receipt source. Do not infer from legacy Issue #97 synthetic ACK. Record trust boundaries and supported failure cases.
2. **Server-side ownership and immutable snapshot**: establish authenticated actor and authorization to hand off the Draft; validate Draft status, task ID, canonical title/description digest, repository allowlist, base ref, expiry. If actor/ownership cannot be established, leave submit disabled.
3. **Transactional persistence**: add a separate handoff/outbox record (not a task status) with unique `(actor_id, task_id, idempotency_key)`, canonical digest, correlation ID, created/expiry timestamps, and state. Duplicate same key+digest returns the original receipt; conflicting digest returns 409. The DB commit precedes delivery; retries must not create a second intent.
4. **Narrow API**: add authenticated, feature-flagged `POST /tasks/{id}/handoffs` and read-only `GET /tasks/{id}/handoffs/{handoffId}`. With feature disabled, no write or delivery occurs. Avoid adding this route to a publicly exposed unauthenticated task API.
5. **Delivery adapter**: deliver only `staging:observe-only` envelopes via the verified transport. No Redis Worker queue, execution endpoints, arbitrary commands, AWS mutation, or untrusted receipt promotion to success.
6. **UI**: after confirmed Draft creation, display a separate preview/confirm action only when enabled and authenticated. Show pending/observed-untrusted/blocked/unavailable distinctly. Preserve uncertain-write lock; allow reconciliation by idempotency key.
7. **Isolated verification**: test on disposable loopback stack with Worker stopped. No production enablement, merge, or deploy.

## Mandatory tests
| Scenario | Expected outcome |
|---|---|
| Feature flag absent/false | no handoff created, no delivery |
| Missing/invalid actor session | 401/403, no DB write |
| Actor not authorized for Draft | 403, no DB write |
| Non-Draft or nonexistent task | 409/404, no delivery |
| Invalid GUID/key/unknown fields/oversize/expired request | 400, no delivery |
| Same actor/task/key/digest replay | same receipt, one intent |
| Same key, different digest | 409, original intent preserved |
| Two concurrent identical submissions | one durable intent |
| DB unavailable | 503, no success claim |
| Relay unavailable or GitHub 403/429 | pending/blocked, no execution |
| Untrusted/stale/replayed receipt | never shown as verified completion |
| No Worker queue writes | `agentlab:task-queue` unchanged |
| Existing staging observations | four-record read-only regression remains valid |
| Ambiguous UI response | no blind duplicate submission |

## Open blockers
- Actual staging Relay transport and authentication not yet inspected.
- Task ownership/actor authorization currently not proven by the local operator session.
- Database migration, endpoint implementation and integration tests **not yet performed**.

Do not turn the feature flag on until these blockers and tests are resolved.
