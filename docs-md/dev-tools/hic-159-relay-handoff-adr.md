# ADR: HIC task handoff is not task approval (Issue #159)

Status: **Proposed — contract only; no runtime authorization**

## Evidence from the current code
- `src/WebUI/src/CommandCenter.tsx` calls `api.createTask` and creates a **Draft** only. Ambiguous write results lock blind retries.
- `src/WebUI/src/limits.ts` accepts only `title` and `description`, capped at 200 and 8000 characters.
- `src/ControlPlane/Program.cs`: `POST /tasks` creates a Draft; `POST /tasks/{id}/approve` transitions to Approved **and enqueues the task in Redis `agentlab:task-queue`**. It must not be used for handoff. `/retry` also enqueues.
- `LocalOperatorSessions` authenticates local runner sessions, not generic task-dispatch authority.
- Issue #157 verifies read-only observations; `observed-untrusted` does not imply execution.

## Decision
Build a **separate, default-OFF, authenticated handoff** from an existing Draft task to a staging Relay inbox. The handoff must never call `/tasks/{id}/approve`, `/retry`, `/status`, `/executions/claim`, or push to `agentlab:task-queue`. The Worker stays stopped. Do not alter task status to Approved.

The Control Plane remains the source of truth for task identity and content. Store delivery/receipt as separate *non-execution metadata* tied to the existing task ID, not a second execution state machine. Existing task/attempt execution states remain authoritative.

## Proposed v1 handoff envelope
```json
{
  "version": 1,
  "taskId": "<existing-draft-guid>",
  "correlationId": "<guid>",
  "idempotencyKey": "<opaque-per-intent-key>",
  "createdAtUtc": "<utc-iso>",
  "expiresAtUtc": "<utc-iso>",
  "issuerActorId": "<authenticated-operator-guid>",
  "intentDigestSha256": "<64-hex>",
  "scope": "staging:observe-only",
  "repository": "<explicit-allowlisted-repository>",
  "expectedBaseRef": "<explicit-base-ref>"
}
```

This is a *proposal*, not an implemented API. Resolve authoritative task snapshot, digest canonicalization, actor-to-task ownership, permitted repository/base, and expiry server-side. Reject unknown fields, replay with different payload, oversize payload, missing authentication, and expired requests. Never include raw secrets, token values, shell commands, or cloud credentials.

## Required invariants
1. **No implied execution:** Draft → handoff submitted → Relay observed remains untrusted and non-executable. Explicit later human authorization is a different workflow.
2. **Idempotency:** unique (actor, task, idempotency key) enforced transactionally; same key + same digest returns same receipt; changed digest returns conflict. No automatic duplicate dispatch on timeout.
3. **Fail closed:** no relay, stale/invalid observation, DB error, expired token, or missing feature flag means no successful receipt claim.
4. **Provenance:** bind to an immutable task snapshot/digest and authenticated actor. Relay receipt must correlate to that digest and be presented as an observation, not as verified completion.
5. **Isolation:** loopback-only dev test, opt-in disposable stack, no Worker, no AWS mutation, no merge/deploy.
6. **UI:** separate Preview/Submit action after Draft creation, with confirmation; distinguish submitted, observed, unavailable and blocked. Never use the existing approve endpoint.

## Open design questions before implementation
- What is the **actual Relay inbox/ack transport and authenticated identity contract**? Inspect source and runtime evidence before selecting filesystem, API or queue.
- Is there an existing durable outbox/receipt table that can be reused, and which schema migration is safe for the isolated test DB?
- What is the authorization boundary for creating Draft tasks and for handoff in a future multi-user HIC? Current local operator session is not sufficient evidence of ownership.
- How are stale, replayed, and untrusted staging markers mapped to a receipt without trusting user-controlled payload?

## Verification gate
First ship tests for disabled-by-default, unauthorized, malformed/expired, duplicate and conflicting keys, relay offline, missing observation, redaction and 4-observation regression. Only then enable isolated observe-only smoke. **No code in this ADR authorizes execution.**
