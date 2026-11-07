# HIC Launcher — degraded native mode and read-only doctor

`hic.cmd` is the Windows entry point for HIC's **read-only Native Host preview**. It is **not yet a complete stack launcher**. It never starts Podman, databases, the Control Plane, or the Worker.

## Commands

- Double-click `hic.cmd` or use `hic.cmd up`: reuse an existing verified HIC Native session, or start the Native Host and Vite UI if their configured ports are free. A missing Control Plane does **not** block the Native Host. It starts **degraded mode**: workspace inspection works while Tasks/Plans show Control Plane unavailable.
- `hic.cmd status`: report actual HTTP health, Podman machine, expected container states, configured host ports and Worker state. Read-only.
- `hic.cmd doctor`: report the same facts and safe suggestions, without changing runtime state. Structured output: `powershell -NoProfile -File scripts/hic.ps1 doctor -Json`.
- If existing HIC 5248/5180 is running, the launcher **reuses it** instead of creating a second Native Host. A partial old/new session or occupied configured ports cause a **fail-closed** error; it doesn't terminate another process.

The new-installation defaults are CP `19480`, Native Host `19481`, UI `19482` on loopback. Use `HIC_CONTROL_PLANE_PORT`, `HIC_NATIVE_PORT`, `HIC_WEB_PORT` overrides. An identity-verified older HIC Control Plane on 8080 remains supported; no existing service is migrated. See [portable ports](PORTABLE_PORTS.md).

## Safety boundaries

- All startup actions are limited to the existing `scripts/hic-native.ps1` launcher. Native API remains restricted to fixed `workspace.inspect`, without shell or arbitrary file reads.
- `doctor` inspects only selected container **state** via Podman read-only queries; it does not inspect task contents, query the database, load credentials, or capture logs.
- A stopped/unavailable Podman machine, missing PostgreSQL or Redis, and a nonresponding Control Plane **must not trigger automatic recovery**. Inspect logs/backup/rollback first, follow [Issue #13](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/issues/13), and obtain a separately approved Control Plane cutover.
- If Worker is running/unknown, the Native preview still only provides read-only workspace inspection; launcher warns about Worker state but does not interact with it.
- Source `docker-compose.yml` is not yet authorized for use against the existing R2 environment. Never perform Compose `up/down`, schema migrations, container rebuilds, `podman machine start` or port migration automatically.
- This is still a development preview, not a hardened always-on Windows service.

## Acceptance checks

The Windows CI test uses synthetic snapshots to confirm that a disconnected Control Plane permits **only** a read-only native launch decision, while partial native sessions and occupied/unknown ports cause refusal. `status` and `doctor` run against the CI host without starting containers.

Next milestone: **explicitly approved, rollback-safe infrastructure start** using verified R2 state and backup; then native task approval/audit and R3/R4 before autonomous agent execution.
