---
name: new-build
description: "Build and start the Agent Lab local Podman stack while preserving existing task data"
disable-model-invocation: true
argument-hint: "Optional: explain what you want to check after startup"
---
Prepare the Agent Lab local stack using the repository lifecycle script.

- On Windows, run `./scripts/local.ps1 new-build` from the repository root.
- On macOS or Linux, run `./scripts/local.sh new-build` from the repository root.
- If Windows reports that user-mode networking must change, explain that the setting can affect networking for all WSL distributions while the Podman machine is active. Ask for explicit approval and wait; only then rerun with `-AllowWslNetworkingChange`.
- Do not use Docker Desktop directly, run global Podman prune commands, or delete volumes.
- Report whether the services started and the exact Control Plane URL printed by the script.
- If startup fails, inspect the relevant Podman/Compose logs and report the concrete blocker. Do not silently change machine settings beyond the repository script.