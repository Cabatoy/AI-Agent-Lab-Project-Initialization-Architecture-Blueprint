# HIC portable host port plan — new installations only

HIC host port values are **configurable**, not hard-coded unique reservations. Default scheme:

| Service | Host endpoint | Container/internal endpoint |
| --- | --- | --- |
| Control Plane | `127.0.0.1:19480` | `control-plane:8080` |
| Windows Native Host | `127.0.0.1:19481` | native Windows process |
| HIC Web UI | `127.0.0.1:19482` | Vite loopback |
| PostgreSQL | **not published** | `postgres:5432` |
| Redis | **not published** | `redis:6379` |

Host overrides: `HIC_CONTROL_PLANE_PORT`, `HIC_NATIVE_PORT`, `HIC_WEB_PORT`.
These must be distinct decimal ports in 1024..65535 and available on the host. You can set them as process/user environment variables before running `hic.cmd`. The Launcher, Native Host, UI dev server and proxies share these settings. Native Host remains hard-bound to 127.0.0.1; no LAN listening.

**Safety / existing installation:** The code change does **not** relocate or restart any running service, or update its current port bindings. The current Windows machine may still have Control Plane 8080, Native Host 5248 and UI 5180. `hic.cmd status` can identify HIC Control Plane's legacy 8080 health signature when no explicit override is configured, and shows legacy Native Host/UI. `hic.cmd up` does not automatically replace any existing legacy native session. To keep the legacy Control Plane target explicitly, set `HIC_CONTROL_PLANE_PORT=8080` on that machine. Do not execute `podman compose up` with the changed compose file against existing R2 volumes/containers until an independently authorized backup, migration and rollback review.

PostgreSQL and Redis **must not be published to the host in new installations**. Their internal ports are unchanged. The docker-compose file is still a development bootstrap with example credentials: it must not be treated as a safe production migration or used to replace existing DB containers. The Worker now has the `manual-worker` profile so unqualified compose startup does not auto-start it; do not enable the profile without separate approval.

Before deploying a new installation, inspect port availability and possible local firewall/service conflicts; high ports can still be in use. For existing environments with a healthy 8080 Control Plane, keep the current runtime unchanged until the Issue #13 cutover is separately approved. No data or service migration is authorized by this document.
