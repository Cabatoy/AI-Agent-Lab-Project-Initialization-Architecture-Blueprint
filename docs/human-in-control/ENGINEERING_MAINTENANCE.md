# HIC — Engineering Maintenance & Integration Playbook

**Status:** Maintainer procedure, not runtime authorization. **Cadence:** every 3–5 substantial feature iterations, before release and after architectural change. **Owner:** HIC maintainers. **Principle:** Humans are not message queues.

## Baseline and evidence

1. Inventory HEAD/base for `develop` and `main`, open PR dependencies, branch/worktree references, uncommitted files, current running processes, database identities and current tests. Never assume local HEAD equals a deployed binary.
2. Record build/test results with exact SHA, runtime environment, skipped cases, migration/schema version, and observed vs simulated approvals. Synthetic approval is not human consent.
3. For security-critical execution, distinguish CI green, synthetic container checks, real host adversarial tests, independent human review and actual preview runtime proof. None substitutes for another.
4. Preserve local HIC volumes, conversations, audit, approved GitHub test fixtures and previous preview snapshots. No implicit migration or operator credential reuse.

## Refactoring pass

- Prioritize cohesive boundaries and high-complexity methods/classes in Control Plane, AgentBridge, Worker, publisher/session and UI; do not enforce arbitrary line counts.
- Separate validation, authorization, persistence, external calls and response mapping without changing public API, task/run ownership or approval semantics.
- Maintain cancellation, explicit failure propagation, idempotency, immutable audit, content digest and external-write reconciliation.
- Small behavior-preserving commits, focused tests after each group, then complete regression and release builds. No new generic framework/parallel task engine.
- Verify real runtime against built SHA; do not represent preview-only changes as live behavior.

## Git/PR hygiene

1. List local + remote branches, open PRs, bases and merge ancestry. Group into protected, active PR, active worktree, unique unmerged, merged-and-unused, experimental and uncertain.
2. **Only delete** a branch after confirming its commits are reachable in the intended persistent target, no open PR uses it (head or base), no active worktree/run/session references it, and there are no local uncommitted or unique changes.
3. Protect `main`, `master` (if present), `develop`, current worktrees, open PR heads/bases and unclear owner branches. No force push, branch history rewrites or guessed remote deletes.
4. For each merge: pin head SHA; validate stacked dependencies, latest target, conflicts, required CI/test and independent security review. Only merge to `develop` when *all* gates pass; no automatic `main` merge or deployment. Re-test downstream stacked PRs after upstream integration.
5. Record deleted/preserved branches and evidence in a maintenance report. Empty cleanup is acceptable when reachability cannot be verified.

## Current integration baseline (2026-11-09 snapshot)

- Dependency chain: **#43** `feature/hic-approved-isolated-git` → **#51** `feature/hic-chat-approved-code-edit-pr` → **#54** `feature/hic-github-publisher` → **#59** `feature/hic-workspace-ux-v1`. Four draft PRs; treat *all* as protected pending release safety gates.
- Validated local user experience: HIC workspace multi-turn model conversation and human-reviewed structured plan → **two Draft tasks**, visible in Tasks/Overview. This does **not** authorize execution.
- Security blockers: verified OS sandbox for untrusted repository execution; host filesystem TOCTOU; safe pinned source import/export and publisher separation; cancellation/recovery; independent human security review; full integrated exact-head regression and verified launcher. Keep execution fail-closed.
- Separate synthetic GitHub Actions `experiment/hic-rootless-cgroup-ci` workflow can demonstrate preliminary ephemeral runner feasibility; it is **not** a permanent executor or general execution security certification.
- Remote branch inventory at snapshot: **42 branches**. The four PR branches are protected. Documentation and experiment branches are *candidates* only; don't delete without merged ancestry/worktree checks.

## Repeatable completion report

- Baseline HEADs/PR bases, working tree cleanliness, deployed runtime identity.
- Changed files/functions and why; proof of semantic equivalence.
- Full build, frontend, backend, integration, security results, explicit skips.
- Branches deleted vs preserved, proven merge/reachability evidence.
- PRs updated/merged (with exact SHA) vs blocked and why.
- Runtime/data preservation, residual risks and next approved action.

## Out of scope without separate explicit approval

Production/schema migration, app privilege expansion, paid infrastructure, public internet exposure, destructive cleanup, deleting active worktrees/volumes, auto-run of untrusted code, weakening execution gates, merging to main/master or deployment.
