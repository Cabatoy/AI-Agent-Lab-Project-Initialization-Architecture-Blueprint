---
name: delete-all
description: "Delete only this repository's Podman stack, images, network, and data volumes"
disable-model-invocation: true
argument-hint: "This permanently deletes local task data; confirmation is required"
---
Delete the Agent Lab local Podman Compose project.

Before running anything, explain that this permanently deletes the PostgreSQL task records, Redis data, and project images/network. The Podman machine and unrelated Podman resources will remain. Ask the user for explicit confirmation and wait for it; invoking this prompt alone is not confirmation.

After confirmation:

- On Windows, run `./scripts/local.ps1 delete-all -ConfirmDataRemoval` from the repository root.
- On macOS or Linux, run `./scripts/local.sh delete-all --confirm-data-removal` from the repository root.
- Do not change Podman machine networking as part of deletion.
- Never run `podman system prune`, `podman volume prune`, or remove resources outside this repository's Compose project.
- Report what was removed and confirm the Podman machine was left intact.