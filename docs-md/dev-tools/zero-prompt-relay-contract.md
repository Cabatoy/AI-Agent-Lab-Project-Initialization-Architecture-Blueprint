# Zero Prompt Relay — developer work-order proposal contract

**Status:** proposal-only documentation for [Issue #93](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/issues/93). This is **not executable**, not a signed approval, not a HIC customer feature, not an auto-runner. No local process, GitHub webhook, Copilot SDK session, credential or billing entitlement is created by this PR.

## Why this boundary exists

The desired development workflow is ChatGPT → reviewable GitHub order → developer-only local HIC coordinator → safe SDK runner → exact-head tests and Draft PR → GitHub evidence → ChatGPT verification. A GitHub issue title, comment, model output or mutable issue body is **untrusted input**, not executable instructions. ChatGPT cannot reach the developer's localhost from GitHub, and an hourly GitHub monitor cannot dispatch a local Fusion task.

## P0 proposal envelope

`dev-work-order-proposal.schema.json` is the JSON Schema for a **review-only, unsigned** work order proposal. It is explicitly non-executable: `mode=review-only`, `review.status=unsigned`, `review.executionAllowed=false`, `review.signature=null`. A local consumer may validate and show such a proposal or produce a sanitized *untrusted-proposal* acknowledgment; it MUST NOT start a model, write files or claim a task from this envelope. It is not a security token.

Fields are deliberately narrow:
- Immutable `orderId`, `createdAt`, `expiresAt`, source Issue/queue/revision.
- Existing real HIC `taskId` (never invent one for live use); canonical allowlisted repository, exact 40-hex `baseSha`, branch, narrow `tools/dev-handoff/` file paths.
- A bounded human-curated `objective` for display only, never an unreviewed prompt.
- `requiresHumanApproval=true`, no credential, command, shell, URL redirection, token, environment variable, model/provider selection, arbitrary file path or PR publish instruction.
- JSON Schema `format:date-time` and `format:uuid` are **annotations in some validators**. The actual coordinator must explicitly validate timestamps, UUIDs, source identity, GitHub state/revision, expiry and real HIC task existence; schema validation alone cannot authorize dispatch.

### Proposed non-executable example

```json
{
  "version": "hic.dev-work-order.proposal/1",
  "kind": "DeveloperWorkOrderProposal",
  "mode": "review-only",
  "orderId": "123e4567-e89b-42d3-a456-426614174000",
  "createdAt": "2026-11-10T15:00:00Z",
  "expiresAt": "2026-11-10T15:05:00Z",
  "source": {
    "repository": "Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint",
    "queueIssue": 75,
    "issueNumber": 93,
    "issueUpdatedAt": "2026-11-10T14:59:00Z"
  },
  "task": {
    "taskId": "123e4567-e89b-42d3-a456-426614174001",
    "repository": "Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint",
    "branch": "feature/dev-work-order-demo",
    "baseSha": "0000000000000000000000000000000000000000",
    "allowedPaths": ["tools/dev-handoff/README.md"],
    "objective": "Display a synthetic, non-executable development order",
    "requiresHumanApproval": true
  },
  "review": { "status": "unsigned", "executionAllowed": false, "signature": null }
}
```

All IDs, SHA and timestamps in this example are illustrative placeholders, **not** a real HIC task or a valid current branch/head. This example is NOT a runnable fixture.

## Future handoff transitions (not implemented)

1. **GitHub proposal:** ChatGPT writes a versioned reviewed plan to a designated GitHub path; only an explicit GitHub commit SHA and exact source repository may be trusted as provenance (not as permission). The local coordinator polls read-only with backoff and remembers the last observed ref without depending on the ChatGPT session.
2. **Local discovery/ack:** The disabled-by-default coordinator shows a proposal with provenance, records an idempotent acknowledgment linked to the canonical HIC task ID and remote revision, and does nothing else. Acknowledgment requires a separately authorized return transport; it cannot assume GitHub write permissions from an Issue reader.
3. **Separate approval envelope:** Design a distinct, authenticated and signed `ApprovedDevWorkOrder` in a later PR, with a trusted Ed25519 public-key registry, clear reviewer authority, expiry, scope and replay protection. NEVER reinterpret an unsigned proposal as approved.
4. **Executor:** Only after #92 sandbox proof and SDK login/entitlement/quota guardrails, start at most one reviewed sandboxed docs-only SDK task, with a real HIC ledger claim/lease; no new task engine. `session.idle` must never be treated as successful code/test/CI completion.
5. **Verifier and continuation:** Confirm exact repo/branch/SHA, independently executed tests and checked GitHub CI, preserve any task/PR failures for human handling; commit/push/Draft PR only through separately approved least-privilege publisher. No auto-merge, deploy, migrations, privileged tool access or Worker activation.

## First acceptance target

On a disposable synthetic GitHub branch and local developer machine, with initial one-time human installation and consent:
- A **non-executable** proposal is created on GitHub by ChatGPT (no copy-paste).
- Local coordinator reads it without reading GitHub Issue prose as commands, records a durable `proposal-seen` acknowledgment through an explicitly approved channel, and can survive restart/replay.
- No model SDK, credentials, production repo, Worker, DB migration or user interaction is involved during a subsequent ordinary read cycle.
- On invalid signature/status/expiry/branch/SHA, the process fails closed; the final UI displays `untrusted` or `blocked`, not `completed`.

This document is a first remote-delivered **contract**, not implementation of the coordinator, signing service, runtime sandbox or GitHub acknowledgment. Follow-up development can happen from this issue/PR without having the developer relay another prompt.

**Dependencies:** #93 (main goal), #83 (prior manual Fusion handoff), #89/#91 (SDK adapter), #92 (sandbox). **Product boundary:** #63. *Humans are not message queues.*
