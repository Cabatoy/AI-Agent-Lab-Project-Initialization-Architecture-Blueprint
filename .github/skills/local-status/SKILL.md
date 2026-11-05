---
name: local-status
description: "Show the Agent Lab local Podman stack status and Control Plane URL"
disable-model-invocation: true
argument-hint: "Optional: name a service to inspect"
---
Inspect the Agent Lab local Podman stack using the repository lifecycle script.

- On Windows, run `./scripts/local.ps1 status` from the repository root.
- On macOS or Linux, run `./scripts/local.sh status` from the repository root.
- Do not build, change networking, or delete resources. The script may start the Podman machine.
- Summarize service states and the Control Plane URL. If a service is down, inspect only its relevant logs and report the reason.