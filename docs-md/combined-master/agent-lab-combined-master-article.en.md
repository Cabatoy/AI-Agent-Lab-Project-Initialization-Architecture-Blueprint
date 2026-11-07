# AI Agent Lab — Combined Master Document (EN)

> Historical overview: source paths reflect the reorganized layout; technical status and proposed next steps are not current approvals. See `docs-md/continuation.md`.

## Executive Summary

This master document consolidates three perspectives into one narrative:
1. Current architecture assessment,
2. Project vision and roadmap story,
3. Management-oriented transition framing for implementation.

Its purpose is to align technical and decision-making stakeholders around one shared view.

---

## Section A — What exists today?

The repository currently provides a local prototype-level task execution core.

**Implemented:**
- .NET 8 Control Plane API
- .NET 8 Worker (`BackgroundService`)
- PostgreSQL + Redis task processing path
- Repeatable local bootstrap through Podman lifecycle scripts

**Current limits:**
- Multi-agent orchestration is still early
- Product chat layer is not yet implemented
- CI/test infrastructure is not yet mature
- Advanced dependency-aware task governance is not yet implemented

---

## Section B — What is it evolving into?

The target product is a platform that manages software delivery from natural-language intent:
- request analysis,
- task planning,
- agent assignment,
- controlled execution,
- test/validation,
- PR flow with human approval.

This transition should be delivered in controlled phases, not as a single jump.

---

## Section C — MVP-to-Product Roadmap

1. **Harden the core** (state model + tests)
2. **Add orchestrator** (planning + dependency handling)
3. **Establish agent workforce** (role/instance split)
4. **Expose product interface** (chat + dashboard)
5. **Prepare cloud transition** (provider independence + cost visibility)

---

## Technical Appendix

## Technical anchors
- API: `src/ControlPlane/Program.cs`
- Worker: `src/AgentWorker/Program.cs`
- Compose: `docker-compose.yml`
- Scripts: `scripts/local.ps1`, `scripts/local.sh`
- Guided launcher: `tools/run-me/Program.cs`
- Architecture docs: `docs-md/00..06`, `docs-md/decisions/*`

## Suggested domain expansions
- Project
- TaskDependency
- Approval
- AgentDefinition / AgentInstance
- ExecutionRun / Artifact / ValidationResult
- AuditEvent

## Security and governance frame
- No critical publish/deploy without explicit human approval
- No secrets in logs/prompts/repository
- Task tools must run through allowlisted permissions
- Provider selection and migration must be policy-driven

---

## Transition note for the next document

The next separate document will detail: local development operations, phase-2 cloud transition (AWS/Azure and similar providers), hosting + monthly maintenance cost model, and provider re-evaluation/migration criteria in executive-summary + technical-appendix format.
