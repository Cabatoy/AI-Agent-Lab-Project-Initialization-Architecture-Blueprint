# HIC Launcher — degraded native mode and read-only doctor

`hic.cmd` is the Windows entry point for HIC's **read-only Native Host preview**. On a new, isolated HIC-managed installation it can start the full safe control infrastructure, and on subsequent boots it can resume the exact registered managed containers. Existing legacy R2 resources are never restarted, migrated or replaced. The Worker remains stopped.

## Commands

- Double-click `hic.cmd` or use `hic.cmd up`: reuse an existing verified HIC Native session, or safely bootstrap/resume a dedicated HIC-managed PostgreSQL + Redis + Control Plane on clean verified installations, then start the Native Host and Vite UI. A missing Control Plane does **not** block the Native Host. It starts **degraded mode**: workspace inspection works while Tasks/Plans show Control Plane unavailable.
- `hic.cmd status`: report actual HTTP health, Podman machine, expected container states, configured host ports and Worker state. Read-only.
- `hic.cmd doctor`: report the same facts and safe suggestions, without changing runtime state.
- `hic.cmd recovery-plan`: inspect container states, the current Control Plane image ID (when available) and host-port exposure, and explain mandatory preflight blockers. It **never** starts/restarts/recreates containers, even if all infrastructure appears healthy. Use `hic.cmd recovery-plan -Json` for machine-readable diagnostics. Structured output: `powershell -NoProfile -File scripts/hic.ps1 doctor -Json`.
- If existing HIC 5248/5180 is running, the launcher **reuses it** instead of creating a second Native Host. A partial old/new session or occupied configured ports cause a **fail-closed** error; it doesn't terminate another process.

The new-installation defaults are CP `19480`, Native Host `19481`, UI `19482` on loopback. Use `HIC_CONTROL_PLANE_PORT`, `HIC_NATIVE_PORT`, `HIC_WEB_PORT` overrides. An identity-verified older HIC Control Plane on 8080 remains supported; no existing service is migrated. See [portable ports](PORTABLE_PORTS.md).

## Safety boundaries

- Infrastructure startup is permitted only for verified isolated HIC-managed resources; legacy R2 data is never modified. Native preview uses the existing `scripts/hic-native.ps1` launcher. Native API remains restricted to fixed `workspace.inspect`, without shell or arbitrary file reads.
- `doctor` inspects only selected container **state** via Podman read-only queries; it does not inspect task contents, query the database, load credentials, or capture logs.
- In **existing legacy R2 installations**, a stopped/unavailable Podman machine, missing PostgreSQL or Redis, and a nonresponding Control Plane **must not trigger automatic recovery**. Only the verified isolated HIC-managed stack is eligible for first-time creation/resume. For legacy recovery inspect logs/backup/rollback, follow [Issue #13](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/issues/13), and obtain a separately approved Control Plane cutover.
- If Worker is running/unknown, the Native preview still only provides read-only workspace inspection; launcher warns about Worker state but does not interact with it.
- Legacy source `docker-compose.yml` is not authorized for use against an existing R2 environment. The dedicated new-installation Compose file is explicitly limited to initial empty-database provisioning. Never perform Compose `up/down`, image rebuilds, schema migrations, `podman machine start` or port migration against a legacy environment automatically.
- This is still a development preview, not a hardened always-on Windows service.

## Acceptance checks

The Windows CI tests use synthetic snapshots to verify degraded native startup, guarded HIC-managed bootstrap/resume decisions, and refusal for legacy data, partial sessions or occupied/unknown ports. The managed Compose file is independently validated without container startup. `status` and `doctor` run against the CI host without starting containers.

Next milestone for **existing R2 environments**: explicitly approved rollback-safe Control Plane recovery after verified local R2 evidence. Next product milestones: native task approval/audit and R3/R4 before autonomous agent execution.


## Single-command managed mode

For a clean Windows + Podman installation with an already running Podman machine, 'hic.cmd up' initializes an isolated database/cache/Control Plane and starts Native Host/UI. On later cold boots it may start only the uniquely pinned managed Podman machine with disabled user-mode networking; subsequent launches resume only previously registered managed containers after identity, image, volume mount and port checks. Existing legacy installations stay on native-only degraded mode if their Control Plane is down. For details see MANAGED_STACK.md.


## Frozen WSL fast-path (existing native session)

`hic.cmd up` now checks for a **verified existing Native Host + Web UI pair** on configured (19481/19482) or legacy (5248/5180) ports **before calling Podman**. If both endpoints match HIC identity, it prints the existing UI address and returns without any WSL or container query. A partial or unexpected port occupant remains a blocker. This keeps an existing read-only HIC preview accessible when `wsl -l` and `podman machine list` hang after sleep. It does not restore PostgreSQL/Redis or Control Plane, and `hic.cmd status`, `hic.cmd doctor` and `recovery-plan` still use Podman inspections that may hang on a broken WSL instance; a separate bounded diagnostics change is required. No WSL restart is authorized by this code.


## Frozen WSL / Podman probe timeout

Read-only Podman probes (`machine list`, selected `inspect`, `ps -a`, volume listing and `port`) are now run through a bounded Windows child process with a default 2.5–5s timeout per probe. If WSL blocks, the launcher reports an unresponsive/unknown state rather than waiting indefinitely; diagnostic failures cannot authorize any infrastructure mutation. HIC only terminates its **own newly launched probe process tree**, not `wslservice`, `vmmemWSL`, existing containers or unrelated Podman/WSL sessions. Existing healthy Native Host/UI reuse remains the first fast path before any Podman inspection. Container start/Compose mutation still require existing strict guardrails and are **not** managed by this timeout wrapper.
