# HIC Native Approval Ledger — Crash / Restart Persistence Acceptance

**Scope:** disposable PostgreSQL v3 prototype only. This is not a production native runner, and it does not modify the user's actual WSL/Podman, R2 database, existing tasks or Worker.

## Tested behaviors

1. Start a disposable PostgreSQL 16 container with no published ports on a GitHub-hosted Linux runner; apply canonical fresh schema v2 then the **manual-only** native approval ledger v3. All approvals and tasks are synthetic.
2. Commit exactly one read-only native run claim for Task A and record its claim/audit event. Restart only the test container's PostgreSQL. Once healthy again, the identical approval must deny replay. The claim and audit row must persist.
3. For separate Task B, start a PostgreSQL transaction, insert a claim and audit through the v3 claim function, and confirm the claim is visible inside the uncommitted transaction. Deliberately hold the transaction open, then force-kill **only the ephemeral CI test container**. Start that same test container again to invoke PostgreSQL crash recovery.
4. Assert Task B has **no committed claim or audit event**, Task A's committed claim remains, both tasks remain Draft and Task B's synthetic approval state is unchanged.

## Deliberate safety distinction

PostgreSQL atomic rollback of an uncommitted transaction does **not** prove external tools or agent execution are reversible. A future Native Runner could perform a side effect before its database commit, then crash. Therefore **never automatically re-execute after an ambiguous result** merely because the DB shows no committed claim. R3 heartbeat/lease, R4 reconciliation, idempotent action design, user-visible uncertainty and compensating/manual recovery remain separate prerequisites.

The current Native Host remains read-only and taskExecutionEnabled=false. No signing issuer, verified human identity, approval endpoint, schema v3 local migration, model call or Worker dispatch is introduced. The test explicitly refuses to run outside a Linux GitHub Actions runner and removes only its own ephemeral PostgreSQL container.

## Coordination with Enterprise workspace refactor

Only new test/CI/documentation files are added. Canonical migration discovery is independent of the old agent-lab or future src directory layout. Fusion may continue moving the local Enterprise solution without this PR modifying or resetting any active worktree.
