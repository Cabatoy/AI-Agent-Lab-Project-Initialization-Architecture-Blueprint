# HIC Portable Identity & Workspace — Architecture Direction

**Status:** Product vision and architectural constraints, **not an implemented feature**. Date: 2026-11-09. Related vision: [PRODUCT_VISION.md](./PRODUCT_VISION.md). Tracking: [#55](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/issues/55).

## Desired experience

A person signs in to their HIC identity from a second computer and finds their approved workspace, conversations, agents, tools, preferences, projects and model-independent memories. HIC adapts to the person and team over time. Their local model running on their home computer is only usable when that host is online and explicitly accessible; on another device HIC can use a different authorized compute endpoint without losing identity or context.

**Identity and knowledge should follow the person; runtime and secrets do not implicitly follow them.**

## Separate four planes

1. **Account & Identity:** portable account, authentication, MFA/passkeys, sessions, device enrollment, revocation and account recovery. No single local bootstrap token as permanent identity. Local-only accounts should remain usable offline.
2. **Workspace & Knowledge:** durable, versioned, appropriately scoped user/team/project/installation memories, preferences, agents and configuration. Model-independent self-knowledge and provenance, as defined in [SELF_KNOWLEDGE_DESIGN.md](./SELF_KNOWLEDGE_DESIGN.md). Distinguish synced metadata from data that stays on one host.
3. **Compute & Capabilities:** locally hosted LLMs/tools/workers, optional remote/cloud execution endpoints, availability/capacity and per-node capabilities. Keep the existing Control Plane authoritative for tasks; no second state machine.
4. **Trust & Policy:** independently trusted devices, explicit connector grants, resource-specific access, origin/device session checks and operation-specific approval. A familiar login is never blanket permission to read local files, remotely run tools, spend API credits, share private data or deploy.

## Data and synchronization contract

- **Local-first, opt-in sync:** local-only and self-hosted operation remain valid; centralized HIC cloud service is not required.
- **Scoped:** personal, team, project and device-local information have different visibility, ownership and retention.
- **Portable:** export/import and backup/recovery support documented formats and version migrations; a model-provider change cannot erase durable information.
- **Secure:** encrypt in transit and at rest according to deployment risk; use explicit trusted sync endpoints, minimum necessary replication and key lifecycle/recovery.
- **No credential copying by default:** connector credentials, model keys and machine-bound secrets stay in the authorized secret store/host; remote clients receive limited capabilities, not raw secrets.
- **Conflict aware:** use versions, revision/ETag-style concurrency controls and human review for conflicting edits; do not silently choose which approval/memory wins.
- **Deletion and privacy:** signed-in users can inspect/correct/export/delete scoped personal knowledge according to policy; team data follows its own governance and ownership rules.
- **Offline:** read cached allowed state and queue only explicitly safe operations; remote privileged writes require current authentication, network proof and scoped approval.

## User journeys to validate later

1. Log into HIC on PC A, create a project and configure an agent with a local model.
2. Log in from PC B, confirm the same *permitted* workspace and context are available, without uploading PC A's private files or credentials.
3. PC A goes offline; the UI explains model/tool unavailability rather than fabricating execution. User may explicitly select a permitted alternate compute provider with clear data egress and cost information.
4. Revoke PC B: its sessions/remote tool grants are invalidated, including attempted replay; existing approvals do not silently broaden to PC B.
5. Change LLM while keeping authorized project context, history and permissions.
6. Add a team member: only team-approved memories and agents are visible; private personal memory remains private.

## Architecture compatibility requirements **now**

The immediate P0 operator session fix should be evaluated for future federation:
- Keep **bootstrap/onboarding credential** distinct from a durable user identity and a short-lived authenticated session.
- Track actor, workspace, device/session context and permission scope separately where feasible; do **not** force a cloud account or introduce premature multi-tenant database migrations.
- Use standard, replaceable authentication/session boundaries; never embed the local operator token into URL, logs or model prompt.
- Preserve existing scoped, authenticated human approvals and audit; remote sign-in alone cannot approve a dangerous operation.
- Do not expose a local-only Control Plane publicly or enable cross-device access without independent security review, transport authentication, authorization, threat modeling and operator approval.

## Delivery order — no P0 distraction

**Current P0:** consolidate #43 → #51 → #54 after the proven HIC-generated [sandbox draft PR #1](https://github.com/Cabatoy/hic-agent-sandbox/pull/1), finish security/backfill checks and the existing local operator session issue. Do not change working local execution or add a new platform identity service now.

**Later (discovery/design):** evaluate account modes (local-only, self-hosted sync, optional managed sync), device trust, data classification, storage/encryption boundaries, offline conflict resolution, cross-device UX and governance.

**Later (implementation, independently gated):** portable authenticated workspace, consent-based sync and device enrollment/revocation, then optional remote compute/connector access. Build only after a dedicated threat model, test plan and human approval.

## Explicitly out of scope

Implementing OAuth/OIDC today, public hosting, automatic synchronization of conversations or secrets, new account backend, cloud provisioning, device-to-device remote code execution, federated organization auth, migrating existing tasks/users or production deployment. This document is an architectural compass only.
