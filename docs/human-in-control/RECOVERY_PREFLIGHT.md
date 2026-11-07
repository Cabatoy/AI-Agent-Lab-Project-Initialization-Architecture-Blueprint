# HIC Infrastructure Recovery — read-only preflight (phase 1)

This feature is a **preflight report, not recovery execution**. It never starts Podman, creates containers, restarts Control Plane, touches database/Redis data, or changes Worker state.

## Usage (approved local develop checkout only)

- `hic.cmd doctor` — service status, container states and safe warnings.
- `hic.cmd recovery-plan` — concise read-only preflight with blockers.
- `hic.cmd recovery-plan -Json` — structured report for a future explicit operator review.

No second Control Plane needs to be deployed to run these. When the Podman machine is stopped or missing, the report records unknown/unverified data and does not attempt to bring it up. All probes are localhost GETs or Podman's read-only `machine list`, `inspect` and `port` commands. No Podman `exec`, `logs`, `start`, `restart`, `compose`, network changes or data mutations.

## Report contract and guardrails

- `disposition`: `no-recovery-needed` when the current CP HTTP health is verified, otherwise `manual-preflight-required`.
- `restartAuthorized`: **always false** in this phase — even when the user sees green status.
- `observed`: verified health, container states, Worker state, current Control Plane image ID (only if hash is validated), and host port exposure. No environment variables, mount paths, database task records, log contents or credentials are printed.
- `blockers`: unverified Worker stop state, CP host binding or current image, unhealthy dependencies, plus **mandatory missing evidence**: R2 schema version, immutable task/status counts and execution state, Redis queue, database backup checksum, rollback image, candidate image and separate human approval immediately before replacement.
- `nextStep`: local read-only evidence verification and Issue #13 preflight; do not run broad Compose `up/down`.

The ability to read a container's image ID does **not** prove the rollback image still exists; finding an old backup file does **not** prove its integrity. Do not infer restart safety or consent from a healthy container status.

## Why no automatic restart yet?

Issue #13 documents a previously migrated R2 database with task state requiring preservation. This chat does not have live access to that Windows/Podman data or verified backup/checksum/candidate images. Automating recovery now would risk changing container port mappings, schema and task state. A later independently reviewed change can add a **separately authorized** minimal Control Plane cutover only after revalidating live state. It must keep Worker stopped and provide rollback without destructive DB restore.

## Tests

Windows CI exercises pure binding classification, plan composition under synthetic snapshots (including a running Worker), PowerShell parsing and the read-only CLI's JSON contract. CI cannot prove runtime recovery behavior on the user's local machine. No deployment from GitHub changes is implied.
