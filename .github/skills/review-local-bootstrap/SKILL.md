---
name: review-local-bootstrap
description: "Review the local Podman bootstrap, lifecycle scripts, and setup documentation without changing files or deleting data"
disable-model-invocation: true
argument-hint: "Optional: focus on scripts, networking, data safety, or documentation consistency"
---
Perform a read-only code review of the Agent Lab local Podman bootstrap changes in this repository. Do not edit files, stage/commit changes, install tools, change Podman/WSL machine settings, or provision cloud resources.

Review the relevant tracked and untracked files, including `docker-compose.yml`, `scripts/local.ps1`, `scripts/local.sh`, `.github/prompts/`, `.github/copilot-instructions.md`, `README.md`, the changed architecture documents, and the Podman ADR. Read the Control Plane and Worker implementation where needed to verify the documented workflow. Ignore generated `bin/` and `obj/` outputs.

Focus on:

- PowerShell and Bash portability, argument handling, exit-code propagation, paths, and Podman machine detection.
- cgroup `pids` setup and Windows user-mode networking behavior, including the WSL-wide networking impact and explicit-consent gate.
- Compose dependency readiness, host port discovery, Windows/macOS usability, and whether `new-build` preserves volumes.
- Whether `rebuild-all` and `delete-all` are scoped to this Compose project, require explicit confirmation, and avoid global prune commands.
- Consistency among scripts, Chat prompt instructions, README, Copilot instructions, decisions, ADR, and the actual runtime behavior.
- Any mismatch between the advertised task workflow and the Control Plane/Worker implementation.

Safe checks may include reading `git status`/diff, PowerShell parsing, `bash -n`, `podman compose config`, `podman compose ps`, project builds, and read-only HTTP health/task requests. Do not run `rebuild-all`, `delete-all`, any command with volume removal, `podman system prune`, `podman volume prune`, or commands that stop/reconfigure the Podman machine. Treat previously reported runtime state as stale and verify it before relying on it.

Return findings first, ordered by severity. Each finding must include a concise explanation and a workspace file/line reference. Then list checks performed and any limitations. If no findings are found, say so explicitly and identify remaining unverified behavior, especially any macOS-only runtime path that could not be exercised. Do not make fixes as part of this review.