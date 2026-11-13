# HIC Relay Task Completion Evidence Checklist

This document outlines the required evidence to claim that a HIC Relay task has been completed successfully.

## Definition of Task Completion

Relay task completion is demonstrated when ALL of the following are present and verifiable:

### 1. Source Issue (GitHub)
- [ ] Explicit GitHub issue number exists in the origin repository
- [ ] Issue title and description clearly define scope and acceptance criteria
- [ ] Issue milestone or assignee (if required by workflow) is set
- [ ] Issue is in the current project/milestone on GitHub

### 2. Task Branch (Git)
- [ ] Fresh task branch created from verified upstream base (`origin/develop`, `main`, etc.)
- [ ] Branch naming convention follows project guidelines (e.g., `task/<issue-number>-<short-desc>`)
- [ ] No uncommitted changes remain on the task branch
- [ ] Branch is cleanly rebased or fast-forwarded from base (no merge conflicts)

### 3. Exact Commit SHA
- [ ] Commit hash is recorded in full (40-character SHA)
- [ ] Commit message references the issue (`Refs #NNN`)
- [ ] Commit author/timestamp is verifiable via `git log`
- [ ] Commit contains only changes in scope (verified via `git diff --check` and file list)

### 4. Pushed Remote Branch
- [ ] Task branch is pushed to remote (GitHub) under the same name
- [ ] Remote branch is verified with `git ls-remote origin task/<branch-name>`
- [ ] No local-only commits remain; remote head matches local head
- [ ] Branch protection rules (if any) are acknowledged

### 5. Linked Draft PR
- [ ] Draft PR is opened against the target branch (e.g., `develop`)
- [ ] PR title references the issue (`Refs #NNN` or similar)
- [ ] PR description includes:
  - Files changed (summary of scope)
  - Verification method (tests run, manual check, diff review)
  - Link to source issue
- [ ] PR head commit SHA exactly matches pushed commit
- [ ] PR is explicitly marked as **Draft** (not ready for merge)

### 6. CI Status at Exact Commit SHA
- [ ] GitHub Actions or configured CI pipeline has processed the commit
- [ ] Status check results are recorded for that exact SHA (not a later commit)
- [ ] Check status is visible via GitHub PR checks or `gh pr checks`
- [ ] Any failures are documented (must not be ignored; must be explained or fixed)

### 7. Known Blockers (If Any)
- [ ] Any blocker preventing completion is recorded with reason
- [ ] Examples:
  - Relay watcher not active (documented with date/time)
  - Task marked ineligible (reason documented)
  - Build/test failure (root cause and remediation plan documented)
  - Safety boundary violation detected (specific boundary noted)

---

## Three Distinct Completion Stages

### Stage 1: Handoff Accepted ✋
**Signal:** Relay dispatcher confirms task was queued and relay watcher is monitoring.
- Relay task assigned a tracking ID or queue entry
- Task branch name and source issue logged
- No automated action has been taken yet

### Stage 2: Agent Reported 🔄
**Signal:** Agent (human or automated) reports actions taken.
- Task branch created and pushed
- Commit SHA logged
- PR opened as Draft
- Status is **not yet verified** by independent observer

### Stage 3: Verified Completion ✅
**Signal:** Independent observer confirms all required evidence is present.
- PR checks pass (or documented as expected failure)
- Code review confirms scope compliance
- All checklist items above are satisfied
- Task may now proceed to next stage (e.g., merge, deploy per separate approval gates)

---

## Example Evidence Report

```
GitHub Issue: https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/issues/139
Task Branch: task/hic-relay-smoke-139
Commit SHA: abc123def456789... (full 40-char hash)
Remote Pushed: ✓ Verified 2026-11-13T12:00:00Z
Draft PR: https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/pull/456
PR Head Commit: abc123def456789... (matches task branch)
CI Status: ✓ Passed (GitHub Actions run #789 on this commit)
Files Changed: docs-md/dev-tools/relay-smoke-evidence.md only
Blockers: None
```

---

## Notes

- **No CI bypass:** If checks fail, they must be fixed or documented as expected failures before verification.
- **No manual force-push:** Task branch must be clean; force-push history indicates workflow confusion.
- **No credential leakage:** Evidence must not include credentials, machine IDs, internal URLs, or local paths.
- **Issue link bidirectional:** Both issue→PR comment and PR→issue reference must be present.
