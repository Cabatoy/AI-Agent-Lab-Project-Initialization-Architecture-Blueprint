# HIC Native Approval — Revocation and Task Revision Contention CI

**Status:** disposable CI tests of a manually gated schema v3 SQL primitive. Not deployed, not wired to the runtime, and not evidence of a human's actual approval.

## Purpose

The prior 24-client test demonstrated unique claims against an unchanged grant. This test probes a different race: an approval revocation or task edit is in progress while an independent runner attempts to claim that approval. A test that merely revokes *before* invoking the claim could miss a broken lock-ordering implementation.

## What the GitHub runner actually executes

1. Create a new disposable PostgreSQL 16 instance without publishing host ports and apply existing fresh schema v2 followed by the manual-only schema v3. This is never run against the user's R2 database.
2. Insert three synthetic Draft tasks and corresponding synthetic approval rows.
3. In database session A, lock and revoke an approval, then hold the transaction open. Check PostgreSQL `pg_stat_activity` shows the holder inside `PgSleep`. Start session B's claim and explicitly observe the `Lock` wait state. Only after A commits may B finish, and B must return a denial.
4. Repeat with session A mutating a task, forcing the existing task revision to increment while B's claim waits on its task row. After commit, B must reject the now-stale approval.
5. On a separate unchanged task, successfully claim a valid synthetic approval exactly once, then reject its replay.
6. Assert durable PostgreSQL state: one claim, one audit row, zero stale/revoked claims, three still-Draft tasks and the expected task revision/revoked state.

## Trust and limitations

The harness is an isolated CI-only Python script using Docker-owned PostgreSQL and standard-library subprocesses. It explicitly checks GitHub Linux runner identity and deletes only the test-owned container.

These tests do NOT prove authenticated human identity, cryptographic signing key lifecycle, cancellation *after* a claim has already been consumed, lease/heartbeat behavior, worker crash reconciliation, or safe native execution. If a grant is revoked after a claim succeeds, later cancellation and execution stopping semantics remain separate mandatory work. The currently unwired v3 migration must never be installed automatically as part of legacy R2 recovery.

## Coordination with Fusion

Only new files are added. The script discovers the canonical migration folder by filename, so it can tolerate the ongoing `agent-lab` to `src` relocation. It does not modify the existing solution, build scripts, APIs, database migrations or local Windows worktrees.
