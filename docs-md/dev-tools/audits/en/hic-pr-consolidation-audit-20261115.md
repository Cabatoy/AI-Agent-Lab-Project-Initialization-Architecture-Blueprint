# HIC branch and PR consolidation audit

**Snapshot:** 15 November 2026, approximately 22:30–22:34 UTC+03. There
were 46 open PRs, all Draft. This is a proposal, not authorization to merge,
retarget, close, deploy, or enable execution.

## Executive summary

- The principal Relay ancestry is #94 → #96 → #98 → #118 → #121 → #125
  → #145 → #162. AWS staging is #145 → #156 → #158 → #160. The latest
  #96 commit is missing from #98 and #145; #162 is not an ancestor of #156.
- #160's description says documentation-only and cites an older SHA; current
  HEAD `15222274e389e81ac473d7d7195383a0208c31b1` adds runtime code,
  migration and tests across 49 commits after #158. Review the real diff.
- #145 and #156 fail GitGuardian on two historical findings; #156 also has
  a failing AgentBridge `STALE_SNAPSHOT` test in its Control Plane job.
  #162, #158 and #160 show only GitGuardian; required CI is not established.
- Protect #162
  `6a7e50ee928c96f28d407e7530d644b3b33e06a9` and the currently running AWS
  Control Plane revision. The latter was not independently observed and must
  not be guessed or changed.
- No PR is proven ready to merge: all remain Draft and require appropriate
  human review, branch protection and exact-head checks.

## Technical appendix

### Complete inventory and other stacks

The [canonical inventory table](../tr/hic-pr-consolidation-audit-20261115.md#canlı-pr-envanteri)
records all 46 PRs, base/head refs, full HEAD SHAs, commit counts and
check counts. `G/K/A/B` means success/failure/skipped-or-neutral/other.
Each linked PR's Commits and Checks tabs provide per-commit and per-check
detail. Snapshot counts are not proof that every required workflow ran.

Other direct stacks are #89 → #91 → #95; #98 → #101 → #103;
#81 → #84; #77 → #78; #69 → #71; #43 → #51 → #54 → #59 →
#61 → #66 → #73 → #85; and #59 → #117. #51 lacks two newer #43
test/documentation commits. #98 lacks #96's `508e04b...` CI-filter commit.
Both require explicit content comparison and integration decisions.

### Critical comparisons and CI

- #118, #121 and #125 are ancestors of #145. They are possible redundant
  review units only if the owner chooses and verifies a single composite
  integration; they are not automatically obsolete or closure candidates.
- #145 is the exact parent of protected #162. #156 contains #145 but not
  #162. #156 has 58 commits against `develop`; #158 is an incomplete UI
  slice; #160 contains actual Control Plane changes despite its stale body.
- #132 → #138 is a genuine backend/UI stack, not duplication. The approval
  endpoint can enqueue Worker work and must not be used for observe-only
  smoke. #132 has three skipped checks; #43 has one.
- #154 remote HEAD `631a7c09df1378987d6fa6001847dec029129fce` descends
  from the historical operator-reported checkpoint
  `2d6cf0920be3f8acbfd4a39c3920470df0b7bd33` by 50 commits. Neither the
  current AWS checkout nor a live local/remote divergence was observed.
  #154 is based on `main` and its HEAD is not an ancestor of #156 or #160.
  Its AgentBridge fixture fix is not in those branches by ancestry; its
  effectiveness there has not been tested.
- #145 and #156 have two historical GitGuardian findings. #145 describes
  them as synthetic/false positives, but owner/security triage and an
  acceptable protection result are still necessary. Do not rewrite history
  or force-push as part of this audit. #156's failing test reports
  `Hic.AgentBridge.BridgeException: STALE_SNAPSHOT`.
- #127 and #155 both modify README; that is overlap, not proof of
  equivalence. #140 and #143 add different smoke documents. No PR is
  classified obsolete solely from a title or age.

### Dependency-ordered proposed actions

1. Refresh this snapshot and preserve #162, the running AWS revision and all
   existing dirty worktrees. Obtain an authorized read-only runtime inventory
   before identifying AWS deployment or image revisions.
2. Have the owner choose incremental #94 → #96 → #98 → #118 → #121 →
   #125 → #145 review versus a single fully reviewed composite. Integrate
   or prove equivalence of #96's missing CI-filter change first. Do not
   close or merge anything automatically.
3. Review #162 separately against #145; require Windows/Linux Relay and .NET
   exact-head evidence and security review. Preserve #162 without rewrite.
   Resolve #145's GitGuardian triage and #156's failing job.
4. After the approved Relay integration, bring #156 forward
   non-destructively; decide whether its base should be retargeted or a
   separate clean integration used. Review #156 → #158 → #160 in order,
   including #160's migration, authorization, idempotency and actual tests.
   Supply the missing exact-head CI for stacked bases.
5. Review #154 separately, including `main`/`develop` base selection and
   runtime divergence only after the actual AWS checkout is observed. Review
   #132 → #138 separately; neither authorizes worker execution.
6. Compare other PRs by real diff and retained content, not title or age.
   Any merge, retarget, close, rewrite or deployment remains a separate
   owner decision and approval.

For the incremental option, review #94 against `develop`, then retarget #96
to `develop` after its parent is accepted. Safely carry or establish
equivalence of `508e04b...` in #98, then review and retarget #98, #118,
#121 and #125 in parent-first order. Review #162 against #145 and, only
with approval, integrate it into #145 before reviewing #145 against
`develop`. After #145 is accepted, bring #156 forward non-destructively,
then review #156 → #158 → #160 parent-first and retarget each accepted
child as appropriate. Recheck each actual diff and exact-head CI after
retargeting; squash merges do not preserve ancestry automatically.

### Gates for a Relay → AWS no-agent smoke

Identify the actual read-only Watch and inbox under #161; do not assume the
legacy synthetic ACK watcher is the staging transport. Validate least-privilege
authentication, rate-limit/backoff, redaction and single-flight behavior
offline. Implement and independently review the missing authenticated AWS
transport, correlated response, replay/expiry checks and provenance.

Review #160's actor/ownership boundary, default-OFF handoff, transactional
idempotency and isolated PostgreSQL tests; complete the #158/UI path only if
the chosen smoke requires it. Obtain explicit approval for a disposable,
isolated Windows → AWS → Windows synthetic observe-only test. Keep Worker
and Agent OFF, queue unchanged and `executionAllowed=false`; do not call
the task approval endpoint. A live watcher or deployment requires its own
separate authorization. Missing evidence remains **BLOCKED**.

### Proposed environment manifest

| Component | Revision / status | Evidence and limitation |
|---|---|---|
| Windows root checkout | `b95bec235a1634bfed0c38079b0fff52ce32007b` | Local HEAD; unrelated untracked files preserved. |
| Windows repair | `6a7e50ee928c96f28d407e7530d644b3b33e06a9` | #162, separate clean worktree; protected. |
| Windows Relay source | `abfefdaa31065c34a2cd15058f39cc07207f22f3` | Local source worktree HEAD, not proof of a running process revision. |
| #154 remote sandbox | `631a7c09df1378987d6fa6001847dec029129fce` | GitHub branch HEAD, not an AWS runtime SHA. |
| Running AWS Control Plane commit | **UNKNOWN — PROTECTED** | No AWS API/SSH inspection; no restart or deployment. |
| Running Control Plane image tag/digest | **UNKNOWN — PROTECTED** | Container metadata not inspected; do not infer from Git. |
| Other running container revisions | **UNKNOWN — PROTECTED** | No runtime inventory was collected. |
| Historical synthetic probe image ID | `sha256:4076016aea5420e5c87eb52280edfc527ace1ce31dc1c9288b1cc4ef9a0e8905` | Operator-reported #154 probe image, not the running Control Plane image or independently verified provenance. |
| Prior staging observations | Four `observed-untrusted` | Historical #159 operator report; not a current runtime inventory. |

Future authorized read-only inventory should capture observation time, scoped
identity, full commit, immutable image digest and evidence source without
recording credentials. This manifest changes no deployed resources.
