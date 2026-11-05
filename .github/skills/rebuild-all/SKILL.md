---
name: rebuild-all
description: "Remove the Agent Lab local data and images, then build the stack from scratch"
disable-model-invocation: true
argument-hint: "This deletes local task data; confirmation is required"
---
Rebuild the Agent Lab local Podman stack from scratch.

Before running anything, explain that this permanently deletes the PostgreSQL task records, Redis data, and images belonging to this Compose project. Ask the user for explicit confirmation and wait for it; invoking this prompt alone is not confirmation.

After confirmation:

- On Windows, run `./scripts/local.ps1 rebuild-all -ConfirmDataRemoval` from the repository root.
- On macOS or Linux, run `./scripts/local.sh rebuild-all --confirm-data-removal` from the repository root.
- If Windows also reports that user-mode networking must change, explain its WSL-wide effect, get separate explicit approval, then add `-AllowWslNetworkingChange`.
- Never run `podman system prune`, `podman volume prune`, or remove resources outside this repository's Compose project.
- Report the build result and Control Plane URL.