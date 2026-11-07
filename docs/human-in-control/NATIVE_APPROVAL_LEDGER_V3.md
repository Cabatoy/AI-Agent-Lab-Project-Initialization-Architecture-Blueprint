# HIC Native Approval Ledger v3 — migration proposal and CI safety

**NOT DEPLOYED. NOT AUTOMATIC.** The migration file `agent-lab/control-plane/Migrations/003-native-run-approval-ledger.sql` is not embedded, referenced by `ExecutionSchema`, called from startup, or included in the dedicated new-installation `schema-init` path. It MUST NOT be applied to the user's existing R2 PostgreSQL without a distinct future migration approval, verified immutable backups and on-device dry run.

## Why this is a separate schema

R2's `task_executions` and `POST /tasks/{id}/approve` serve the legacy Worker. The latter is presently unauthenticated and enqueues task IDs. The native runner must have a separate **authenticated** human grant and durable single-use claim without turning Draft tasks into legacy Worker queue items. PostgreSQL is the one authoritative store; GitHub issues/comments and browser fields never create authority.

The proposed v3 schema contains:

- `tasks.native_run_revision` plus a trigger that increments it on **every** task UPDATE, invalidating approvals bound to prior versions.
- `native_run_approvals` immutable identity/binding metadata: task, run, task revision, authenticated actor ID (to be supplied by a future service), fixed read-only action and scope, canonical intent/receipt digests, key ID, short expiry, revoke state.
- `native_run_claims`: a unique run/approval record, with approved task/run foreign-key binding and an exclusive claimed/completed/failed/cancelled lifecycle.
- `native_run_audit`: event type and optional cryptographic digest, no prompts, raw commands or secrets. UPDATE and DELETE are rejected by a trigger. Database administrator privileges can still change triggers, so separate operational audit retention is necessary.
- `hic_try_claim_native_readonly_run`: DB-atomic single-use claim, checking current task revision, Draft status, digest, scope, revocation and expiry; concurrent/replayed calls cannot claim again. This function does **not** authenticate the caller or validate signature proof. A future authenticated CP component must do so before invoking it.

## Security and trust boundaries

A row marked `approved` does **not** prove real human approval: the DB function is deliberately a low-level primitive and is NOT a safe public API. Real identity provider/session, signed receipt, restricted SQL privileges, transactional actor binding, signature verification, issuer key rotation, exception audit, cancellation semantics and R3/R4 crash recovery are still pending. No Native Host execution endpoint or model route is enabled by this work. The native HTTP status must continue returning `taskExecutionEnabled=false`.

## CI coverage

`hic-native-ledger-ci.yml` spins up PostgreSQL 16 **without a host port** on a disposable GitHub runner, applies the existing fresh schema v2 then the manual v3 migration using `--single-transaction`, and checks: first approved synthetic claim succeeds once; replay fails; task change increments revision and rejects stale approval; expired and revoked grants fail; wrong digest/runner fail; audit UPDATE/DELETE is denied; rejected migration re-application preserves the claim and synthetic Draft task. This is NOT a local R2 migration or proof of an end-to-end authenticated approval.

## Future gated rollout

1. Implement real human identity and approval issuance in Control Plane with restricted DB rights and verifiable signed receipts. Authenticate via dedicated local authority; do not reuse Native Host's ephemeral proxy token.
2. Verify historical R2 data/schema and a rollback-safe backup in the user's Windows/Podman environment. Review and explicitly authorize a future schema v3 cutover. No mass Compose/Worker restart.
3. Build a runner that can consume only signed, audience-bound, single-use claims and initially perform the already allowlisted `workspace.inspect`. UI must show proposed scope and demand an explicit human click; no automatic approvals.
4. Prove concurrent claims, cancellation/replay, crash/restart recovery and R3/R4 lease behavior under real integration tests before enabling any file writes, model invocation, network access or shell commands.
