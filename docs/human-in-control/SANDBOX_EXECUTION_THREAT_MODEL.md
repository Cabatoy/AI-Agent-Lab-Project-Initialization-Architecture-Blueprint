# HIC — Execution Plane Threat Model and Verification Gates

**Status:** proposed security requirements; Issue #105; 2026-11-11. No sandbox implementation or approval. Relevant: #67 artifact boundary, #92/#104 local measurements, #102/#103 review authority, #108 later remote probe.

## Assets, actors, trust boundaries

Assets to protect: operator and tenant identity, HIC task approval/lease/audit ledger, source repository and history, cloud account, billing quota, SSH/GitHub/LLM credentials, local Windows/WSL files and business systems, other users' artifacts, network and provider host.

Trust the **reviewed and authenticated Control Plane authority** only after a real server-side proof; do not inherently trust GitHub issue text, hosted VM, agent model, third-party adapter, guest kernel telemetry, task-specific source code, installer image or stdout. Treat all repository content and generated artifacts as untrusted inputs.

Trust boundaries:

- User/browser -> Control Plane: authentication, tenant ownership, approval revision and audit binding.
- Control Plane -> provider adapter: authenticated transport, scoped credentials, signed manifest and admission decision, anti-replay, revocation and TLS peer validation.
- Provider host -> per-attempt sandbox: OS-enforced processes/namespace/mount/network/resource isolation; cannot be inferred from a VM label.
- Sandbox -> quarantined artifact store: no direct writes to production repository or ledger; hostile outputs sanitized.
- Artifact verifier -> GitHub broker: explicit separate human approval, exact hash/branch/base SHA and idempotent publishing.

## Threat/mitigation register

| Threat | Required prevention and observable negative test |
| --- | --- |
| Prompt injection in issue/repo content | Sign independent reviewed work-order; never execute raw issue/comment/model command; malicious prompt fixture cannot change allowed paths/tools |
| Stale/replayed approval or provider callback | Server-side immutable task revision, epoch, nonce, atomic single claim/lease and fencing; replay across restarts rejected |
| Forged provider safety claims | Measured OS checks with challenge bound to provider instance and verified witness; self-report alone UNVERIFIED |
| Host/enterprise credential access | No home/host mount, .git, SSH socket, API metadata token, cloud keys, service account or corporate bind; negative canary files (no actual secrets) |
| Filesystem escape or TOCTOU | Read-only digest-checked input, safe descriptor/openat/no-follow approach, separate writable output, symlink/junction/hardlink and archive-negative tests |
| Network exfiltration | Default no DNS and no network namespace egress, observable denied connection attempts; management route separate and controlled |
| Resource exhaustion and fork bomb | Actual cpu.max/memory.max/pids.max enforcement, runtime deadline, output cap, quotas, concurrency cap; test enforcement and OOM/kill |
| Tenant-to-tenant or provider-crossing data | Tenant/owner binding on every request, separate paths/identities and isolation checks; no silent cross-provider/region fallback |
| Cost/Free Tier overspend | Real eligibility/quotas, max spend and block on unknown; alerts are advisory and NOT a hard stop; no paid fallback |
| Malicious container image or dependency | Trusted image provenance, digest pin, architecture check and offline synthetic inputs for initial probe; mutable tags rejected |
| Untrusted output published as a PR | Quarantine + independent typed verifier + separate scope/commit/diff approval; broker credentials never enter executor |
| Cancellation or cleanup race | Fence prior generation, exact owned disposable resource ID, verified removal, never wildcard/prune; crash/restart test |
| Fake 'success' from provider/SDK | Match exact task/run/attempt and artifact evidence, tests and independently observed terminal state; idle/HTTP 200 alone not success |
| Cloud/host control-plane compromise | Minimize admin privileges, separate management channel, enforce cryptographic identity, document residual VM/hypervisor trust; no blanket claim of total isolation |

## Evidence levels

- **DESIGNED:** written policy only.
- **OFFLINE_TESTED:** fake provider/contract regression. Does not prove runtime enforcement.
- **OBSERVED:** locally observed OS metadata without negative test. May still be UNVERIFIED for security.
- **VERIFIED:** directly measured OS control plus adversarial negative test, independent witness, freshness and artifact recorded.
- **BLOCKED:** known failure, missing prerequisites, untrusted claim, or inconsistent evidence.

Every important security property must be recorded independently with verifier, time, host/image, exact manifest hash, test ID and result. A provider's remote status cannot upgrade a HIC task from Draft to Executing without the canonical claim. A documentation check or CI-only suite does not produce production Verified.

## Initial OCI probe boundaries (future, not authorized)

Only a synthetic disposable input on an explicitly authorized personal OCI tenancy and *separate* host with no enterprise data. Private host administration and workload egress are separate network boundaries; choosing a private subnet alone is insufficient to prove workload has no network or cloud metadata access. Test cloud metadata (169.254.169.254) denial explicitly; avoid any live credentials in canaries.

No direct GitHub write, external tool installs, SDK/model, long-lived daemon, unrestricted SSH command or access to HIC persistent database. Stop if CPU/memory/PID controllers are absent (as measured on the current local rootless Podman guest), if user namespace isolation is incomplete, or if cleanup cannot be limited to a single owned fixture.

## Gate owner and approval

- Operator: selects account/region/provider, allows bounded cost/egress and explicitly approves each live provisioning or real task.
- HIC trusted authority: verifies task intent, claim, reviewer, revocation and replay at the canonical ledger.
- Provider adapter: reports constrained capability and measured evidence; does not approve tasks or create second state machine.
- Security reviewer: checks isolation, artifact boundary and negative cases before live development.

**No approval provided here for cloud instance creation, model use, provider key storage, filesystem mutation or deployment.** The owner will independently register for a free cloud account, then supply only non-sensitive readiness information.
