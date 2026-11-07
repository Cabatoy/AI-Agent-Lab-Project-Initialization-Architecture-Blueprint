# HIC Launcher — guarded first phase

The HIC launcher is an incremental step toward one-click local operation. It **does not yet** start the entire stack. It deliberately avoids modifying Podman when a running Control Plane is unhealthy or missing; that case needs the previously planned Issue #13 diagnosis and a separately authorized recovery.

## Windows usage

- Double-click `hic.cmd` from an approved checkout to launch the read-only Native Host preview and HIC UI **only if** the existing Control Plane is healthy, Worker is confirmed stopped and ports 5248/5180 are free.
- Run `hic.cmd status` to display read-only service status.
- Run `hic.cmd doctor` for read-only status plus safe problem hints.
- Status is available for scripts through `powershell -NoProfile -File scripts/hic.ps1 status -Json` (reports availability only; never credentials or task data).
- If Native Host and 5180 UI are already ready, `hic.cmd` does not restart them.
- If 8080 is not healthy, or if 5248/5180 is partially occupied, the launcher **refuses** to start. Do not kill/restart existing services automatically.

## Important boundaries

- `hic.cmd up` (and double-click) **does not** run Podman/Compose, restore data, perform migrations, start Worker, call an LLM or execute arbitrary task text. It calls the existing `scripts/hic-native.ps1` only after the preflight passes.
- This is intentionally NOT yet `hic up` across every service. A safe all-stack version must first account for existing local R2 database state, the separate Podman machine, known rollback images and worker-disabled start. The repository's current `docker-compose.yml` publishes PostgreSQL and Redis host ports and contains development credentials: do not silently use it for unattended runtime reconciliation.
- A known offline Control Plane is **not** sufficient evidence to recreate/restart the container. The last local 8080 state should be verified from the latest Fusion diagnosis; GitHub does not have that report.
- The Worker remains intentionally stopped. Task execution needs scoped approval, durable audit and recovery gates described in Issue #10.
- The launcher is a developer preview, not a permanent Windows service or production runtime supervisor.
- Existing HIC UI 5173/5174, DB volumes and all existing worktrees remain unchanged.

Next separate milestone: review the Issue #13 Control Plane diagnosis and create a minimal, consent-gated and rollback-capable **infrastructure start** operation using existing containers without running unsafe Compose up/down; implement isolated Windows Native Host lifecycle management and a durable status dashboard before self-healing.


## Portable host ports

New defaults: Control Plane 19480, Native Host 19481, UI 19482 (loopback only), configurable with `HIC_CONTROL_PLANE_PORT`, `HIC_NATIVE_PORT`, `HIC_WEB_PORT`. Existing local 8080/5248/5180 services are not moved or restarted. See [portable ports](PORTABLE_PORTS.md).
