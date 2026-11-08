# HIC — PC Return Checklist (Archived 2026-11-07)

> Archived device report. All checkout paths, Git instructions, runtime observations, and preauthorizations below are historical and do not describe the current workspace or grant current runtime permission. Re-inspect the current worktree and obtain explicit approval before any runtime action.

Goal at the time: reach the **new Command Center input field** on Windows, retain an unsent local draft even with Control Plane offline, and only then recover the existing R2 Control Plane without data loss. This report is source-controlled; it does NOT mean a local agent is connected or a recovery was performed.

## Known last on-device evidence (from local VS Code Fusion report — not current verified state)

- The active feature checkout was on `feature/hic-reliability-r2` (clean at last report). This is historical and does not constrain current Git synchronization.
- An isolated old develop worktree was also prepared; its last confirmed commit then was `f6eb861` (PR #20). The location and commit are historical; inspect the current `git worktree list --porcelain` before relying on either.
- Prior native host `127.0.0.1:5248` and UI `127.0.0.1:5180` returned HTTP 200; UI 5173/5174 also had listeners. This may have changed.
- Control Plane `127.0.0.1:8080` disappeared after `wslrelay` stopped listening; `podman machine list` was hung because WSL `wsl -l --quiet --running` did not return.
- Historical R2 schema/task state (four tasks at last report) must be re-verified. Never assume old counts/images/backups are current.

## Ready in GitHub (source/CI work, not local installation)

- PR #21: Native Host/UI fast-reuse checks **before** WSL/Podman.
- PR #22: bounded read-only Podman probes; WSL timeout reports unavailable rather than blocking indefinitely.
- PR #23: one **browser-local unsent input buffer** persists across reloads; it is NOT a server-side Draft task. Unknown POST outcomes lock retry.
- PR #24: `hic.cmd preview` launches the new native UI on 19481/19482 beside legacy 5248/5180, without WSL probes.
- Upcoming Windows CI smoke: real Native Host + Vite + native proxy on an ephemeral runner with the Control Plane deliberately offline; no local deployment.

## Phase A — verify new Native input without repairing WSL (minimal scope)

1. On the actual PC, first verify whether old 5248/5180 listeners still exist and resolve the isolated worktree's current path with `git worktree list --porcelain`. Identify live processes by executable path and start time, **not stale PID alone**. Never kill an unrelated listener.
2. Verify the selected worktree's Git status and branch before using it. Preserve all local work and use only supported, non-destructive Git operations.
3. Run `hic.cmd preview` from that isolated worktree (new ports default 19481/19482). This does **not** call Podman and does not replace the old session. If ports are occupied/unknown, stop and diagnose instead of killing services.
4. Open `http://127.0.0.1:19482` and navigate to Command Center. Confirm the new *Unsent input stored locally* text. Enter a non-sensitive **test description** and refresh; verify it remains. Do NOT click `Create Draft task` yet, and do NOT run Worker. The expected Control Plane status can still be unavailable.
5. Verify the read-only `Inspect local workspace` action works via the new native proxy. Do not grant arbitrary shell or file action permission.

## Phase B — scoped recovery of legacy R2 Control Plane (only an actual local agent can execute)

6. Run bounded, read-only WSL/Podman checks only with current, explicit approval. If still frozen, identify the exact user-owned Podman VM/distro first; never make blind assumptions about process/distribution identity.
7. Attempt graceful targeted Podman stop, then targeted `wsl --terminate` of the **verified Podman distro only** if needed. Never `wsl --unregister`, `podman machine reset`, generic Docker/Compose `down`, volume cleanup or forced DB restore. Global `wsl --shutdown`, system-wide WSL service termination or Windows restart **are not implicitly approved**; they require a fresh impact assessment if necessary.
8. Verify the existing Podman machine identity before starting it. Verify old PostgreSQL/Redis containers/volumes and R2 schema, task IDs/statuses/counts, active attempts, Redis queue, current CP image and backup/rollback evidence. Do not migrate or recreate historic resources.
9. Only after consistent data and a stopped Worker are verified, restore **the existing unchanged containers**, starting stopped PostgreSQL/Redis only as necessary and the existing Control Plane only when safe. No rebuild/migration; no Worker start.
10. Verify CP health (`8080` for old installation unless explicitly reconfigured), task inventory, plans and UI proxy health. Check Worker remains stopped and no unexpected active attempts appeared.

## Phase C — final native input acceptance

11. Refresh the new Web UI at `19482`: type an unsent draft, refresh page, verify text survives. No automatic POST should occur.
12. Only on the operator's deliberate `Create Draft task` click should the Control Plane return HTTP 201 and a real Draft; do not create a sample task automatically. Ensure already-present R2 tasks remain intact.
13. Report the actual ports, running process identities, Control Plane image, DB/task counts (non-sensitive summaries only), Worker state, git HEAD/worktree and any blocked steps. Do not post secrets, task descriptions or local DB raw records in GitHub.

## What CI can and cannot prove

GitHub CI can compile and unit-test launcher/Host/UI and start a fresh isolated Windows runner to verify the preview's HTTP behavior. It **cannot** confirm a historical local checkout, frozen WSL, existing R2 database/Redis, or 8080 port mapping. Green CI never means that a local recovery or deployment occurred.

Further product work after this milestone: secure task-backed native execution and human approval/audit (Issue #10). Current native host still only permits `workspace.inspect`.


## Latest GitHub-side correction: greenfield schema v2

On **brand-new** HIC-managed installs, `schema-init` is a one-time profiled service that refuses a nonempty PostgreSQL schema and applies canonical v2 migration before the new Control Plane starts. It is not a legacy R2 migration command. GitHub ephemeral Docker integration testing must verify this with a Draft POST; it does not replace an on-device check of the existing R2 database.
