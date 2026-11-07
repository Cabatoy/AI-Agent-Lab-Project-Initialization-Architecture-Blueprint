# HIC Managed Stack — one-command safe control infrastructure

Run **hic.cmd up** (or double-click hic.cmd) in an approved local Windows checkout. This repository change does not deploy anything on its own.

## Behavior by environment

| Environment | HIC action |
| --- | --- |
| Existing healthy Control Plane at configured port or legacy 8080 | Reuse it; launch/reuse Native Host + Web UI |
| Clean, new Podman environment with an already-running machine | Provision an isolated HIC-managed PostgreSQL, Redis and Control Plane, then start Native Host + UI |
| Prior successful managed installation | Verify manifest/container IDs/images/data volumes/CP loopback port, then start only stopped registered containers; never rebuild |
| Legacy agentlab containers or prior DB volumes, stopped or running | Skip infrastructure changes; allow Native Host/UI degraded mode |
| Podman machine stopped/missing | Automatically start only the single exact previously pinned HIC-managed machine if WSL user-mode networking is explicitly false; otherwise open degraded mode |
| Partial managed installation, changed images/identity, unknown port bindings | Fail closed; no deletion or recreation |
| Worker | Never started by managed Compose |

Dedicated manifest: deploy/hic-managed/compose.yaml. It defines exactly three services and publishes only Control Plane on 127.0.0.1 using HIC_CONTROL_PLANE_PORT (default 19480). PostgreSQL (5432) and Redis (6379) are available only inside its container network. Native Host defaults to 19481 and UI to 19482; these host ports remain configurable.

## Private state

A new, empty installation creates a random 256-bit database password and writes it outside Git under %LOCALAPPDATA%\HumanInControl\managed\runtime.env. After successful provisioning and health checks, the file manifest.json in the same folder records managed container/image IDs and CP port for identity-based resume. Never commit the credentials, post them to tickets or expose the runtime.env file to others.

The initial fresh database necessarily creates its new schema. This is NOT a migration of an existing R2 database. The script never runs broad Compose up/down against historic resources, never cleans up partially created volumes, never replaces the Control Plane image during resume, and never starts Worker.

## Prerequisites and limitations

Windows with .NET 10 SDK, Node/npm, Podman with Compose, and an running Podman machine, or a previously pinned/registered stopped Podman machine with safe networking. A stopped Podman machine or absent tools are not silently initialized/installed. Port conflicts and unexpected legacy/partial resources block infrastructure startup. The user may still use Native Host/UI with reduced functionality.

Podman on Windows may have host/WSL networking behavior that differs from Compose metadata; verify actual exposure before treating the setup as hardened. The Control Plane API currently lacks durable authenticated task approvals, so this is a LOCAL DEVELOPMENT stack, not a production deployment or an arbitrary-agent sandbox.

Failures leave containers and persistent data unchanged for troubleshooting; no rollback or destructive cleanup is automatic. For pre-existing R2 environment changes follow Issue #13's separate read-only preflight and approval gate. The source CI checks launcher policy; real Windows/Podman runtime behavior has NOT been verified here.

Useful read-only inspection: hic.cmd status, hic.cmd doctor, hic.cmd recovery-plan.
