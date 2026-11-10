# HIC #79 — Operator Dashboard design proposal

## Executive Summary

**Status: proposed, not an approved implementation or product decision.**
This branch contains documentation and offline contract tests only.
Dashboard must reuse the Control Plane task state machine, not create another.
The first slice proposes a read-only landing page over existing Overview,
Tasks, Plans and diagnostics; it does not change the UI on this branch.
Worker, executor, lifecycle operations, migrations, merge/deploy and paid model
calls are excluded. [Personalization ADR](adr-079-personalization.md) is a
future proposal, not delivered tenancy.

Evidence snapshots: develop `bcfa97679e235f75483135d19a64654228fecc27`;
#59 `0601242279d3a55bd39a2fc278ea5c0df429cf0e` → #61
`c8df5725beb981bb3e9438d44a366dcae7899ccb` → #66
`ff4337578df67b1905e52abd664b5548efcbc9de` → #73
`07a49f9d511ad7dfd55b9f258f759568061e7046`. These are open Draft
PR snapshots, not proof of deployment. #76/#78 resource policy snapshot is
`fa8dd6e1f78fd1d6dee3d241d9c336d576e41e74` on another branch.
This design starts from develop without merging the stack.

### Information architecture

| Area / contract ID | Content and behavior | Slice |
|---|---|---|
| Tasks — `tasks` | Authoritative status counts, recent work, Tasks detail link | A1 |
| Plans — `plans` | Existing execution plans and Plans link; separate from general plans | A1 |
| Health — `health` | Control Plane / PostgreSQL / Redis; Worker not checked | A1 |
| Decisions — `decisions` | Actor-scoped history for selected task with revision/provenance; not global inbox | A2, dependent |
| GitHub — `github` | Configured target, known publication reference, explicit issue preview; no CI feed | A2, dependent |
| Model — `model` | Authenticated local configured-model inventory check, not inference guarantee | A2, dependent |
| Resources — `resources` | No web-safe API; “Not connected”, no historical RAM shown as live | A3, gap |
| Usage/cost — `usage` | No verified token/price telemetry; no invented totals or savings | A3, gap |
| Agents — `agents` | No persisted registry; neither zero agents nor running agents inferred | Future |

Proposed navigation: Dashboard (future evolution of Overview), Tasks, Plans,
Conversations, Planning, Local Runner and Isolated Git. Preserve existing
workflows rather than adding another editor or approval system. Agents /
Models & Providers / Approvals differ between develop and the stack; verify
deployed capability and version instead of assuming equivalence.
Header: active context, session, read-only boundary, **per-panel** successful
fetch time and Refresh. Do not derive “waiting for human decision” from
Draft, Review or Approved task status.

## Technical Appendix

### Wireframes and responsive contract

Proposed desktop (≥1200 CSS px):

```text
Nav | Dashboard · context · session · Read-only · Refresh
    | Error/stale notice associated with the affected panel
    | Task summary      | Plan summary     | Health
    | Recent tasks (2 columns)             | Decisions / detail
    | GitHub            | Model            | Resources / usage
```

768–1199: two columns, collapsible navigation, selection detail below list.
320–767: one column, menu button/drawer and full-width detail. Reading order:
error/session, tasks, decisions, health, plans, GitHub, model, resources,
usage. Drawer closes on Escape and returns focus to its opener. Desktop
placement must not reorder DOM/keyboard navigation. At 200% zoom and 320 px,
no page-level horizontal overflow. Long SHAs/repositories/untrusted text
wrap; narrow task tables become labeled cards. Only technical diff containers
may scroll horizontally.

- Landmarks, one `h1`, labeled sections and skip link; reuse existing
  [components](../../../src/WebUI/src/components.tsx).
- Visible keyboard focus, text/icons beyond color, normal-text contrast
  ≥4.5:1 and proposed touch targets ≥44×44 CSS px.
- Loading/refresh use polite `role=status`; errors use `role=alert`.
  Summarize updates without announcing every polling counter change.
- Detail focuses its heading and returns focus to its source row on close.
  Approval/destructive actions are never default focus. Honor reduced motion.
- This specification is not browser evidence. Implementation needs actual
  320/768/1200/1440 viewport, keyboard and screen-reader acceptance.

### Authority and state semantics

[API matrix](api-mapping.md) separates method, permission and effect.
The tasks table owns task state; local run and publication states remain
separate badges. Running task grouping is InProgress/Executing; Approved
and Retrying do not prove a running Worker. Unknown future task states retain
the raw label as “Unknown status”, never silently becoming Completed.
There is no DeadLetter endpoint/status; omit that tile rather than show zero.

Each panel supports `loading`, `empty`, `ready`, `stale`, `offline`,
`unauthenticated`, `forbidden`, `invalid-contract`, `unavailable`.
Successful `[]` means empty; timeout/401/403/503/malformed JSON never means 0.
Retain last successful data only with a stale timestamp; erase private data
immediately on auth/actor change. Partial failure must not hide healthy
panels. Successful `/health` does not prove DB/Redis/model/Worker health.

### Proposed refresh and session behavior

- Only the four A1 GETs: one cycle per 30 seconds, maximum 2 concurrent
  requests and 4 requests per cycle; 8-second timeout each. No automatic
  retries; wait for next cycle or explicit Refresh. Mark data stale after
  60 seconds. Coalesce Refresh with any existing cycle.
- AbortController on unmount, hidden, offline, session-ended and context-change;
  generation ID prevents late results overwriting new context. Resume with
  one coalesced cycle when visible/online; no background-tab polling.
- Lists are currently **unpaginated**. Rendering at most 50 rows does not bound
  response bytes or memory. Count the complete successful response and label
  it “returned tasks”. Large installations need a separately approved backend
  pagination/aggregation contract; do not invent `?limit=`.
- No automatic polling of stacked/provider endpoints. Require explicit panel
  selection/request, a valid session and an accepted contract. Read history
  only for the selected task, never N+1 fan-out. Restore session once through
  GET; never log in automatically. On 401 clear all private panels; 403 is denied.
- Reuse HttpOnly, SameSite=Strict cookie/proxy behavior. No tokens in URLs,
  localStorage, analytics, logs, errors or exports. Current client helpers
  lack caller cancellation signals; supporting them is a future tested UI
  requirement, not a claim about current implementation.

### Human decision UX boundary

A1 links to existing review workflows only. Mount/refresh must not issue
previews, decisions or imports. A future inbox must show task/conversation ID,
actor, action/scope, exact revision, digest, source time, expiry, server-issued
plan and durable decision. Inspect → exact diff/plan → separate deliberate
Approve/Reject, without bulk approval. Task/revision/actor/repository/digest
change invalidates review. No automatic POST retry on 401/409/expiry/unknown
outcome; inspect durable history first. No global pending list or uniform
provenance feed exists; completed history is not a pending inbox.
Git approval may reserve a run but grants no execution/publication permission;
general plan approval creates Draft tasks only. Never assume an older runtime
enforces newer gates.

### Slices, tests and acceptance

1. **A0 (this PR):** IA/API/responsive proposal, future ADR, offline document
   contracts; no runtime or browser acceptance claims.
2. **A1 (separate approval/PR):** reuse Overview/Tasks/Plans/diagnostics;
   test typed payloads, partial failure, unknown≠zero, cancellation/late results,
   offline/hidden state and duplicate refresh.
3. **A2:** acceptance of #59/#61/#66/#73, deployed capability/schema checks and
   negative session/actor/revision tests. Registry and global inbox gaps remain.
   No GitHub Dashboard integration before #73 authorization/read-only acceptance;
   no publisher privilege.
4. **A3:** separately approved read-only resource bridge and verified usage/cost
   telemetry. Never invoke shell/Podman/WSL from the browser or add overlapping
   host, WSL/cgroup, container and process memory.
5. **Acceptance:** exact-head CI, actual responsive/keyboard browser checks and
   operator feedback. Matrix: 200 `[]`, malformed JSON, timeout, 401/403/404/409/503,
   late results, actor/revision changes, `hasMore=true`, private issues,
   unauthorized repository and missing telemetry. Tests must not access shared
   previews or real databases. Later implementation uses synthetic fixtures/HTTP
   mocks and never performs lifecycle actions without separate approval.

Operations/cost: A1 should add no service, package or model call. Refresh
budgets bound proposed request load, not proven RAM/energy savings. Defer A2
fan-out and broker privilege costs. A3 must not make a local device a mandatory
cloud dependency. No AWS/Azure identity, storage or pricing choice is made;
provider comparison and migration remain open.

The [machine-readable contract](../dashboard-contract.json) is a test fixture
only, not loaded by the application. From `src/WebUI`, run
`npm test -- tests/dashboard-design.test.ts` for offline document tests; these
do not verify real UI/polling/authz behavior. Route evidence was additionally
compared against declarations at the pinned Git SHAs.
[Turkish counterpart](../tr/dashboard-design.md).
