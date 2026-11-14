# HIC Self-Knowledge and Model Bootstrap — Design Proposal

Status: **design only**, not an implemented service. Date: 2026-11-08. Tracking: [#55](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/issues/55).

## Purpose

A newly connected/replaced LLM should understand what HIC is, which capabilities are actually available, the authorized user's relevant environment, and the current task without replaying months of chat logs. **The model is stateless from HIC's perspective; HIC retains durable memory and authority.**

## Proposed information scopes

| Scope | Examples | Authority / sensitivity |
| --- | --- | --- |
| Platform | Mission, architecture, API contracts, policies | Version-controlled, verified against source/tests |
| Installation / environment | OS, provider endpoints, installed capabilities, runtime health | Live discovery; avoid secrets and identifiers in broad prompts |
| Principal / user | Preferences, working style, prior approvals | Private, editable and deletable; never cross user boundary |
| Team | Shared conventions and approved playbooks | Membership and permission-scoped |
| Project | Repo structure, ADRs, decisions, current task references | Project access-scoped |
| Execution / task | Approved plan hash, allowed tool scope, run logs, outputs | Existing Control Plane remains authoritative; short-lived prompt context |

Do not turn memory into a second task state machine, approval store or credential vault.

## Trust and freshness rules

- **Verified:** signed/versioned source, validated configuration, explicit user confirmation, or test-backed live discovery; include provenance, observed time and expiry where relevant.
- **Observed / inferred:** model inference or partial telemetry, labeled uncertain; requires corroboration before operational decisions.
- **Proposed:** ideas, requirements and roadmap, never represented as installed capabilities.
- **Stale / conflicting:** provide uncertainty to the model; prefer current registry/runtime over old prose, and surface conflicts for operator review.
- A model may draft knowledge changes, but must not independently approve its own permission expansion or mark itself verified.

## Context bootstrap sequence

1. Identify model capabilities/context budget and requested task.
2. Resolve authenticated principal, authorized installation/project/team and allowed connectors; default deny unknown scope.
3. Load a tiny stable platform identity and non-negotiable safety/approval contract.
4. Retrieve versioned, verified capabilities and relevant architecture; provide current runtime status only from trusted discovery with timestamps.
5. Retrieve minimally necessary project, team and personal context within the task's permissions; redact sensitive fields.
6. Produce a bounded structured context package with section version, provenance, freshness, trust level and references for on-demand detail.
7. Keep execution policy outside prompt text as enforceable server-side controls. A clever prompt cannot authorize a tool.
8. On model switch, rebuild the context package from the same durable sources and log the source versions; do not copy hidden provider conversation state.

Conceptual package (illustrative, not an API commitment):

```json
{
  "schemaVersion": "proposal-v0",
  "platform": { "identityRef": "docs/human-in-control/PRODUCT_VISION.md" },
  "task": { "id": "<task-id>", "approvedScopeRef": "<server-side-scope-ref>" },
  "contexts": [
    { "kind": "architecture", "version": "<revision>", "trust": "verified", "asOf": "<time>", "ref": "<source>" },
    { "kind": "project", "version": "<revision>", "trust": "verified", "ref": "<authorized-source>" }
  ],
  "limits": { "tokenBudget": "<model-and-task-specific>", "secretsIncluded": false }
}
```

## First implementation increment (after #42/#52 milestone)

- Start with a small checked-in identity/architecture/capabilities reference plus a validated registry/runtime snapshot.
- Prototype deterministic context compilation and bounded token usage for **two distinct local/cloud-compatible models**, using synthetic non-sensitive projects.
- Demonstrate a model swap with stable task context and **unchanged tool permissions**.
- Validate missing, outdated, conflicting and malicious knowledge sources; never silently fall back to broad project disclosure.
- Document memory update approvals, backup/recovery and retention before introducing per-user/team memory.

## Deferred

Full vector database, RAG indexing, specialized fine-tuning, autonomous self-rewrite, community marketplace, production data migration and extension execution. Each needs its own threat model, acceptance gates and evidence.

## Risks to test

Prompt injection inside retrieved project docs, stale capabilities, cross-tenant memory leakage, secret exfiltration to external models, token/context overflow, unauthorized self-modification, hallucinated completion and unreliable recovery.

## Relationship to current development

Keep [#42](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/issues/42) and [#52](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/issues/52) as the first product milestone. This document is guidance only; it does not authorize live Worker startup, schema changes, shell execution, GitHub pushes, merges or deployment.
