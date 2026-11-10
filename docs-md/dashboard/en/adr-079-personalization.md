# ADR-079 (proposed): Future tenant-scoped Dashboard personalization

## Executive Summary

- **Status:** Proposed; not an accepted decision.
- **Date:** 2026-11-10; context: Issue #79 Phase B.
- **Scope:** Design only. No login, tenancy, storage, extension runtime,
  migration or executor implementation in this PR.
- **Proposal:** Fixed first-party panels first; then versioned declarative
  layouts and capability manifests behind server-enforced tenant/user boundaries.
  Extensions default to private; community promotion requires explicit opt-in
  and independent review.
- Related: [Dashboard](dashboard-design.md), [API matrix](api-mapping.md).

## Technical Appendix

### Context and invariants

A local operator session is not a cloud tenant identity. Baseline tasks/plans
are global; UI filtering cannot enforce isolation. Phase A must retain the
Control Plane task ID/state authority and separate plan/run/publication
provenance. Local Podman/devices must not become mandatory cloud dependencies;
resource adapters stay behind portable read contracts. Historical approval
is not transferable blanket permission for another extension/version.

### Options and operational/cost tradeoffs

| Option | Benefit | Risk / cost |
|---|---|---|
| A: Fixed first-party Dashboard | No new service/DB for Phase A, smallest attack surface | No personalization; proposed for MVP |
| B: Server-authorized declarative layout + registry | Data rather than packages; versioning/rollback and user isolation | Login/policy/audit/storage operations; proposed for Phase B |
| C: Arbitrary same-origin JS plugins | Maximum flexibility | Cookie/DOM/data exposure and supply chain risk; not recommended |
| D: Remote service per widget | Independent deployment | Network/auth/tenant routing cost; premature without evidence |

Database selection, shared schema + RLS versus separate databases, identity
provider and sandbox technology remain **open**. Compare AWS/Azure managed
identity/storage and portable alternatives using security, operations and
verified current prices. No cloud price or provider is selected now.

### Proposed identity/authorization contract

The server derives `tenantId,subjectId,roles,capabilities` from verified login.
Client-supplied tenant/user IDs cannot grant access. Apply tenant+subject
authorization to every read/write, list/detail, cache, audit, search and export.
Check resource ownership inside database queries; cross-tenant IDs, guessed
objects and actor changes fail closed. Authz cache context includes
tenant+subject+resource+policyVersion; clear caches and in-flight requests
on identity change. First-party panels are not exempt.

Layout sketch (not a new endpoint or database schema):

```text
DashboardLayout:
  schemaVersion, layoutId, revision
  owner: tenantId + subjectId (server authoritative)
  panels: stablePanelId + widgetId + widgetVersion + placement + safeSettings
  updatedAt, previousRevision
```

Use exact revision/ETag for writes; conflicts require 409/412 and explicit
review, never silent last-write-wins. Rolling back revision N creates N+1,
preserving audit and newer capability revocations. Layout contains no
approval, prompt, credential, tool or execution grants. Quarantine invalid/
unsupported widget versions; core Dashboard may display an explicit error
and safe fixed layout, never an authorization fallback. Do not automatically
share private layouts or panels across users/tenants.

### Extension manifest and trust boundary (future contract)

```text
ExtensionManifest:
  extensionId, version, manifestSchemaVersion, contentDigest, publisher
  compatibleDashboardVersions, panelIds, dataSchemaVersions
  requestedCapabilities, networkOrigins, settingsSchema
  isolationRequirements, rollbackTarget, distribution=private
```

Example capability names are proposals: `tasks.read`, `decisions.read`,
`model.inventory.read`. Deny wildcard/network access by default. Requesting
permissions in a manifest does not grant them. Each tenant/user reviews
version and permission diffs; server policy enforces least privilege.
Panel slots never imply tasks.approve, publish, lifecycle or execution.
No arbitrary code extensions, host shell/filesystem or same-origin cookie
access in MVP. Future code extensions would require a separate origin,
narrow schema-validated message broker, CSP and independently reviewed
OS/network boundaries; an iframe alone is not untrusted execution isolation.
Remote HTTP content is untrusted data, never rendered as HTML/scripts.

Private installation belongs only to its owner's HIC. Community proposal:
explicit opt-in → secret/personal-data screening → licensing/provenance →
independent security/product review → versioned digest-pinned registry entry.
Never include private layouts, memory, model settings or credentials in a
package. Community acceptance does not auto-install or grant access to users;
installation remains a separate explicit choice. Require withdrawal, pinning,
quarantine and rollback audit. Existing approval cannot expand to new permissions.

### Memory, model policy and privacy

Scope durable memory/model policy to tenant+subject, with explicit retention,
delete/export, sensitive-content redaction and provider egress consent.
Model/provider selection is not a layout setting: use separately versioned
policy and budget/egress permissions. Secrets remain server-side secret-store
references, never browser/layout/extension state. Deny cross-tenant retrieval,
training and memory sharing by default. Enforce revocation on the next request;
old caches/grants/rollbacks cannot restore revoked access. Never auto-select
a paid provider.

### Acceptance gates and deferred choices

Before Phase B: identity/login threat model, tenant enforcement (including
DB/cache/audit), retention/deletion, capabilities and extension signing/
provenance, revocation/rollback protocol and AWS/Azure/portable cost comparison.
Negative tests: tenant A list/detail/export seeing B objects, forged tenant,
late responses/caches after session switch, permission escalation, revoked
grants, manifest downgrade/tamper, layout conflicts, private-to-community leaks,
sandbox escape/egress and model-memory isolation.

These are future acceptance requirements, not tenant runtime tests executed
here. Phase A preserves stable panel IDs and typed read-adapter boundaries;
it adds no tenant columns, login service or plugin loader. Until acceptance,
this proposal does not change [current decisions](../../02-current-decisions.md).
[Turkish counterpart](../tr/adr-079-personalization.md).
