# HIC #79 — Authoritative API and permission matrix

## Executive Summary

This inventory distinguishes **existing evidence / unmerged contracts /
data gaps**; it defines no new endpoint or grant. Read with the
[IA/responsive proposal](dashboard-design.md) and [fixture](../dashboard-contract.json).
Browser paths prepend `/api`; paths below are Control Plane paths.
Baseline `/tasks`, `/plans`, `/health` and `/diagnostics/local` do not enforce
operator or tenant authorization. They belong to the local loopback prototype,
not an approved cloud/multi-user deployment. `X-HIC-Request` is not authentication.

## Technical Appendix

### Immutable evidence reviewed

- **D:** [develop App](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/blob/bcfa97679e235f75483135d19a64654228fecc27/src/WebUI/src/App.tsx),
  [Program/API](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/blob/bcfa97679e235f75483135d19a64654228fecc27/src/ControlPlane/Program.cs#L53)
  and [client](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/blob/bcfa97679e235f75483135d19a64654228fecc27/src/WebUI/src/api.ts#L355).
- **W / #59:** [App](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/blob/0601242279d3a55bd39a2fc278ea5c0df429cf0e/src/WebUI/src/App.tsx),
  [GeneralPlans](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/blob/0601242279d3a55bd39a2fc278ea5c0df429cf0e/src/ControlPlane/GeneralPlans.cs#L45)
  and [LocalModelStatus](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/blob/0601242279d3a55bd39a2fc278ea5c0df429cf0e/src/WebUI/src/LocalModelStatus.tsx).
- **B / #61:** [Draft-to-bounded-Git workflow](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/blob/c8df5725beb981bb3e9438d44a366dcae7899ccb/src/WebUI/src/GitRunnerWorkflow.tsx);
  UI wiring, not a general executor or global approval API.
- **G / #66:** [GitEditEndpoints](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/blob/ff4337578df67b1905e52abd664b5548efcbc9de/src/ControlPlane/GitEditRunner.cs#L289)
  and [durable decision store](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/blob/ff4337578df67b1905e52abd664b5548efcbc9de/src/ControlPlane/GitPlanDecisions.cs#L46).
- **I / #73:** [Issue preview/import](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/blob/07a49f9d511ad7dfd55b9f258f759568061e7046/src/ControlPlane/GitHubIssuePreview.cs#L25),
  [publisher reads](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/blob/07a49f9d511ad7dfd55b9f258f759568061e7046/src/ControlPlane/GitHubPublisherEndpoints.cs#L32),
  [session](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/blob/07a49f9d511ad7dfd55b9f258f759568061e7046/src/ControlPlane/LocalHealthRunner.cs#L557)
  and [proxy](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/blob/07a49f9d511ad7dfd55b9f258f759568061e7046/src/WebUI/vite.proxy.ts#L38).
- **R / #76/#78:** [read-only resource/Phase 2 policy](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/blob/fa8dd6e1f78fd1d6dee3d241d9c336d576e41e74/scripts/hic-phase2-policy.ps1),
  not a web endpoint.

### Existing read surface

IDs match the fixture. **O** = valid operator session; **L** = local access
boundary only, no operator/tenant isolation. “Stacked” requires PR acceptance
and deployed schema/capability verification. A source snapshot is not deployment.

| ID | Method / Control Plane path | Auth / evidence | Authoritative fields and limitation |
|---|---|---|---|
| `health` | GET `/health` | L / D | `status,service`; not DB/Worker probe |
| `tasks` | GET `/tasks` | L / D | `id,title,status,attemptCount,result,errorMessage,createdAtUtc,updatedAtUtc`; complete list, no pagination |
| `task` | GET `/tasks/{id:guid}` | L / D | Same task details; 404 means missing/deleted |
| `plans` | GET `/plans` | L / D | `id,projectKey,requestTitle,status,planSummary`, timestamps; execution plans, not general plans |
| `diagnostics` | GET `/diagnostics/local` | L / D | `checkedAtUtc,controlPlane,postgres,redis,worker=not-checked`; no memory measurement |
| `session` | GET `/local-runner/session` | O / I stacked | `actorId,expiresAtUtc,expiresInSeconds`; read restore, not login |
| `general-conversations` | GET `/local-runner/general-conversations` | O / W stacked | Actor-scoped summaries, not pending decisions feed |
| `general-plan` | GET `/local-runner/general-conversations/{id:guid}/plan` | O / W stacked | `revision,source,createdAtUtc,tasks,approved,rejected,taskIds`; 404 for absent plan |
| `git-decisions` | GET `/local-runner/tasks/{taskId:guid}/git-decisions` | O / G stacked | `decisionId,actorId,action,scope,intentDigestSha256,decision,decidedAtUtc,runId,plan`; maximum 50, `hasMore`, `limit=50` |
| `run` | GET `/local-runner/runs/{runId:guid}` | O / I stacked | Actor-scoped run, plan/result/events; not task state; no unknown-ID enumeration |
| `model` | GET `/local-runner/model` | O / W stacked | `configured,provider,model,timeoutSeconds,readiness`; no prompt, inventory listing does not guarantee generation |
| `github-status` | GET `/local-runner/github/status` | O / I stacked | `configured,repository,missing`, optional revision information; not connectivity/CI health |
| `publication` | GET `/local-runner/github/publications/{id:guid}` | O / I stacked | Known actor publication `state,plan,commitSha,remoteSha,prNumber,prUrl,errorCode,updatedAtUtc,events`; not live PR/CI state |
| `issue-preview` | POST `/local-runner/github/issues/preview` | O / I stacked | `{reference}` → repository/issue numeric IDs, title/body, `updatedAtUtc,revisionSha256`; provider GET, no task write |

`git-decisions` lists **recorded** decisions only; `hasMore=true` means
incomplete history. Plan `revision=1` is protocol revision, not task revision;
task digest and exact plan are independently verified. There is no global
pending inbox, cross-actor decision enumeration or total approval count API.
General plan GET lacks approval actor/timestamp audit feed. Unified provenance
needs a separately reviewed read contract.

Model readiness values are `not-configured`, `not-checked`, `available`,
`unavailable`, `model-not-listed`; unknown values must not become successful.
The check reads configured local provider `/v1/models` inventory, not remote
LLM health, token usage, actual cost or latency telemetry.

### Existing human actions, forbidden during Dashboard refresh

| ID | Method / Control Plane path | Payload and actual effect |
|---|---|---|
| `general-plan-approve` | POST `/local-runner/general-conversations/{id:guid}/plan/approve` | `{expectedRevision}`; W/O, creates only Draft tasks for the exact revision |
| `general-plan-reject` | POST `/local-runner/general-conversations/{id:guid}/plan/reject` | `{expectedRevision}`; W/O, records rejection, not a run |
| `git-preview` | POST `/local-runner/tasks/{taskId:guid}/git-preview` | `{repositoryId,baseCommit,file,content}`; G/O, writes actor/task-bound server preview; not read-only |
| `git-approve` | POST `/local-runner/tasks/{taskId:guid}/git-approve` | `{plan,intentDigestSha256}`; G/O, durable decision + reserved run; 202 is not execution/publication |
| `git-reject` | POST `/local-runner/tasks/{taskId:guid}/git-reject` | Same exact preview; G/O, durable rejection, `runId=null` |
| `issue-import-preview` | POST `/local-runner/github/issues/import-preview` | `{reference,revisionSha256,title,description}`; I/O, five-minute actor-bound DB preview, no task |
| `issue-import-confirm` | POST `/local-runner/github/issues/import-previews/{id:guid}/confirm` | Empty body; I/O, source revision recheck, atomic Draft/dedup/audit; `{taskId,replayed}` |

#73 supports the installed/authorized **public** repository only; private
repositories are denied before fetching an issue. Treat issue text as untrusted
data, never automatic prompts/instructions. Source is reread, but GitHub read
and local transaction commit are not atomic. The current broker uses
publisher-scoped credentials; issuing GET does not make it an independently
read-only credential. Do not grant that credential to Dashboard; a restricted
broker is a separate gate. Publication GET reads local records; live branch,
PR lifecycle and exact-head CI require a new authorized read-only feed.

`POST /tasks/{id}/approve`, retry/status/claim, execution-plan approve,
local run execute, model message/generate and GitHub approve/execute/reconcile/
complete-draft are **excluded** from automatic or new Dashboard actions.
Never infer read-only behavior from “preview”: publisher preview can persist
records and call a provider.

### Gaps and permission gates

| Widget | Evidence today | Proposed boundary |
|---|---|---|
| `resources` | CLI `resource status` / `phase2-plan`, no HTTP | No shell; wait for separately approved authenticated read-only bridge |
| `usage` | No token/price/spend ledger | Unknown; no amount without currency, price timestamp and provider measurement |
| `agents` | No persisted agent registry | “Not connected”; never derive agents from tasks |
| `decisions` | Selected task history / separate general plan | Global inbox/provenance absent; no claim of empty inbox/0 pending |
| `github` | Issue preview + local publication | No live issue list/branch status/PR lifecycle/CI; unknown |

Future memory samples need `source,scope,measuredAt,value,unit,availability`;
separate host/VM/container/process, label estimates and never sum scopes.
Each adapter needs auth, redaction, cancellation/timeout and private-repository
tests. This PR closes no gap through implementation or migrations.
[Turkish counterpart](../tr/api-mapping.md).
