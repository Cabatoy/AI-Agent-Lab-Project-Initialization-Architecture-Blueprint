# HIC Native Host — first native process (preview)

This is the first **independent local Windows/.NET 10 process** in Human In Control. It removes the need to send a **fixed, read-only workspace inspection** to VS Code Fusion. It is **not** a general-purpose agent runner and must not be described as full Issue #10 completion.

## What it actually does

- `Hic.NativeHost` runs on **127.0.0.1:19481**. The binding is hard-coded to loopback, independent of `ASPNETCORE_URLS`.
- The HIC UI's **Inspect local workspace** button sends `POST /api/native/actions` with the single accepted body `{"action":"workspace.inspect"}`.
- The Vite proxy checks same-origin, host, a dedicated request header, JSON body size and exact allowlist. It injects a per-launch **256-bit secret** that is never sent to the browser. All user-supplied fields are discarded before the native request.
- Native Host requires the secret on `POST /v1/actions/run`, rejects unrecognized fields, and performs *only* file-existence checks inside its fixed `HIC_WORKSPACE_ROOT`. It checks whether the configured root exists, whether Git checkout metadata exists, and whether `HumanInControl.sln` is present. It returns booleans, an ephemeral run ID and UTC time, **not the local path or file contents**.
- No shell, process launch from requests, arbitrary file reads, dynamic path, model calls, paid API, GitHub write, new database table, or task-state transition exists in the host.

## Windows first-use preview (requires a trusted local checkout of this PR after merge)

**Preflight:** Windows, .NET 10 SDK, Node/npm, existing Control Plane on 127.0.0.1:8080 if you want the other dashboard panels to work. Review `hic.cmd resource status` and confirm ports **19482** and **19481** are unused.

1. Update an **existing, clean or separately-worktreed** local checkout to the approved `develop` commit. Do not reset worktrees or destroy uncommitted changes.
2. Run `hic.cmd profile plan -Profile Light -Task ui` and review the plan ID and protected listeners. The default `hic.cmd up` only reuses an existing verified preview or prints this no-mutation Light plan.
3. Run **`run-hic-native.cmd -PlanId <reviewed-plan-id> -Apply`** or `hic.cmd preview -PlanId <reviewed-plan-id> -Apply`. These entry points route through the plan gate and fail closed if listeners or inventory change.
4. The PowerShell launcher checks local ports, builds only the new project, installs locked UI npm dependencies if absent, generates an ephemeral token (not written to disk), starts the Native Host and runs a **new Vite UI on 127.0.0.1:19482**. Do **not** download or run similarly named untrusted launchers.
5. Open **http://127.0.0.1:19482**, choose Command Center → **Inspect local workspace**. The result comes from the native local host, not from ChatGPT or Fusion.
6. Press **Ctrl+C** in the launcher to stop the preview Native Host and the foreground Vite UI. Existing 5173/5174 UI, Postgres, Redis, Podman and Control Plane are untouched.

This intentionally does **not** automatically start at Windows login or replace existing services. Autostart, secrets at rest, packaged installation, watchdogs and self-healing belong to later separately reviewed milestones.

## Security limitations

- This is a **loopback + per-launch shared-secret preview**, *not* durable user login, authenticated task approval, or a sandbox for arbitrary instructions. Another sufficiently privileged local process may inspect process environment; do not enable powerful actions until a stronger identity/isolation boundary is built.
- `runId` is an **ephemeral result correlation ID**, not an authoritative Control Plane execution identity or audited execution. No second task engine was created.
- This preview does not approve/dispatch/execute a Control Plane task, so R3/R4 lease/recovery needs remain unmet and the legacy Worker stays stopped.
- The Native Host must not be exposed to LAN or public networks. The UI is a Vite development server; use a hardened production build/hosting before use outside personal development.
- Keep Issue #10 open for durable task-backed approval, authenticated operator identity, runner-scoped capabilities, idempotency, audit, R3/R4 and policy-gated local model execution.
- A safe local script may still need to be launched **one time**. ChatGPT's GitHub connector cannot directly boot your Windows process.

## Verification
- GitHub PR CI must pass full `HumanInControl.sln` build, Native Host allowlist/policy tests, UI API/component and HTTP proxy abuse tests.
- Before first local preview, inspect the branch/worktree being launched and verify the legacy Worker remains stopped and no containers were replaced.
- During UI test: verify native result and repeated denied shell/path bodies; no task-status changes in the Control Plane.
- **No local runtime test or deployment has been performed from this chat.**


**Port migration note:** Prior PR #15 previews used 5248/5180; they keep running unchanged. New installations use 19481/19482 and CP 19480 by default. Override `HIC_CONTROL_PLANE_PORT`, `HIC_NATIVE_PORT`, `HIC_WEB_PORT` as necessary. See [portable ports](PORTABLE_PORTS.md). Do not start a second host over a running legacy session.
