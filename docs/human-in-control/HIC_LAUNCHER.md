# HIC Launcher — degraded native mode and read-only doctor

`hic.cmd` is the Windows entry point for HIC's **read-only Native Host preview**. On a new, isolated HIC-managed installation it can start the full safe control infrastructure, and on subsequent boots it can resume the exact registered managed containers. Existing legacy R2 resources are never restarted, migrated or replaced. The Worker remains stopped.

## Commands

- Double-click `hic.cmd` or use `hic.cmd up`: reuse an existing verified HIC Native session; otherwise show a default **Light / UI-only** resource plan. It does not start containers, create databases or launch a second preview until an exact plan is reviewed and passed back with `-Apply`.
- `hic.cmd status`: report actual HTTP health, Podman machine, expected container states, configured host ports and Worker state. Read-only.
- `hic.cmd doctor`: report the same facts and safe suggestions, without changing runtime state.
- `hic.cmd recovery-plan`: inspect container states, the current Control Plane image ID (when available) and host-port exposure, and explain mandatory preflight blockers. It **never** starts/restarts/recreates containers, even if all infrastructure appears healthy. Use `hic.cmd recovery-plan -Json` for machine-readable diagnostics. Structured output: `powershell -NoProfile -File scripts/hic.ps1 doctor -Json`.
- If existing HIC 5248/5180 is running, the launcher **reuses it** instead of creating a second Native Host. A partial old/new session or occupied configured ports cause a **fail-closed** error; it doesn't terminate another process.
- `hic.cmd resource status` and `hic.cmd resource doctor`: bounded read-only inventory of host memory, top process working sets, the Podman/WSL VM process when identifiable, container stats by service, volumes, Worker state and protected listeners. JSON is available with `-Json`; unavailable measurements remain `unknown`.
- `hic.cmd profile plan -Profile Light -Task ui`: create a no-mutation service plan. Use `-Profile Development -Task api` for the core API/data dependencies; optional `-MemoryBudgetGiB`, `-TestParallelism` and `-InferenceParallelism` values tune the advisory plan.
- The plan includes the exact host processes, existing managed containers, new container/volume names, loopback ports and whether the guarded schema bootstrap would run. Review this before passing its `planId` to any start command.
- `hic.cmd resource start -Profile Light -Task ui -PlanId <plan-id> -Apply`: opt in to starting the isolated Native Host/UI after reviewing the plan. `hic.cmd up -PlanId <plan-id> -Apply` is the equivalent entry point. The plan ID is re-evaluated against current containers, volumes, listeners and preview state before any start.
- A Development start can resume an existing stack only when its per-user manifest, container IDs, Compose project/service labels, mounts and credentials digest verify. A clean machine may initialize a new isolated managed stack only when the inventory proves there are no existing containers, volumes, database listeners or stale ownership files. External/legacy resources are protected and are never adopted; an existing external PostgreSQL listener blocks bootstrap until an explicit reviewed reuse path exists.
- `hic.cmd resource stop` is intentionally blocked in Phase 1. Persistent databases are not stopped; model unload and in-flight request coordination are Phase 2 work.
- `hic.cmd preview` remains the explicit independent-preview command, but now requires the same `profile plan` result and `-Apply` before launching processes. A running verified legacy preview remains reusable and is not replaced.

The profile graph is data-driven in `deploy/hic-resources/profiles.json`:

| Profile | Planned services | Safety behavior |
|---|---|---|
| `Light` (default) | UI task: Native Host + Web UI. API task adds Control Plane, PostgreSQL and Redis. | Starts only the selected host preview or exact verified HIC-owned stack. |
| `Development` | Core UI/API/PostgreSQL/Redis; tasks and inference add only their declared dependencies. | Reuses a verified stack or bootstraps only on a fully clean Podman inventory. |
| `FullAgent` | Future model + Worker + executor plan. | Unavailable; security gate #63 is required. A profile never grants execution permission. |

Memory ranges are planning budgets, not capacity guarantees or hard container caps: Light 3–6 GiB, Development 8–14 GiB and Full Agent 20–30 GiB. The inventory keeps Windows host totals, per-process working sets, VM working sets and Podman container statistics in separate scopes; these figures must not be summed. Podman reports of zero/unavailable memory and unidentified local-model memory remain `unknown`. Parallelism and memory-budget overrides affect the plan only; test and model lifecycle are not activated by Phase 1.

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


## Managed stack ownership

The existing managed-stack manifest and Compose project continue to identify the resources created by HIC. Resource-profile activation never starts a Podman machine, runs a `compose up` against an existing project, adopts external databases or stops services. A reviewed clean-install plan may use the existing guarded bootstrap path only when no containers, volumes, database listeners or prior ownership files exist. Later starts require the durable ownership record and exact container identity checks; legacy R2 resources stay untouched. See [managed stack](MANAGED_STACK.md).


## Frozen WSL fast-path (existing native session)

`hic.cmd up` now checks for a **verified existing Native Host + Web UI pair** on configured (19481/19482) or legacy (5248/5180) ports **before calling Podman**. If both endpoints match HIC identity, it prints the existing UI address and returns without any WSL or container query. A partial or unexpected port occupant remains a blocker. This keeps an existing read-only HIC preview accessible when `wsl -l` and `podman machine list` hang after sleep. It does not restore PostgreSQL/Redis or Control Plane, and `hic.cmd status`, `hic.cmd doctor` and `recovery-plan` still use Podman inspections that may hang on a broken WSL instance; a separate bounded diagnostics change is required. No WSL restart is authorized by this code.


## Frozen WSL / Podman probe timeout

Read-only Podman probes (`machine list`, selected `inspect`, `ps -a`, volume listing and `port`) are now run through a bounded Windows child process with a default 2.5–5s timeout per probe. If WSL blocks, the launcher reports an unresponsive/unknown state rather than waiting indefinitely; diagnostic failures cannot authorize any infrastructure mutation. HIC only terminates its **own newly launched probe process tree**, not `wslservice`, `vmmemWSL`, existing containers or unrelated Podman/WSL sessions. Existing healthy Native Host/UI reuse remains the first fast path before any Podman inspection. Container start/Compose mutation still require existing strict guardrails and are **not** managed by this timeout wrapper.

## Independent updated Native input preview when legacy WSL is frozen

Use **hic.cmd preview** from an up-to-date develop worktree to launch the NEW Native Host on 19481 and Web UI on 19482 without Podman/WSL inspection. This is distinct from hic.cmd up, which deliberately reuses the verified legacy 5248/5180 session. The new and old sessions coexist; neither one is terminated or migrated. Unknown/occupied configured ports or an incomplete new session cause a fail-closed error.

If the older 5248/5180 HIC pair is verified and no explicit Control Plane port override exists, preview mode targets legacy CP 8080 for later reconnection. While 8080 remains unavailable, the new Command Center can retain UNSENT local browser text across refreshes, but cannot persist a Control Plane Draft until the user explicitly submits after recovery. No automatic task submission, Worker start, model execution or Podman restart is performed.

This source change alone does NOT install code on the user's Windows machine. The separate worktree must be updated and hic.cmd preview invoked in a real local session to make the updated UI visible; the GitHub connector cannot run Windows processes.
