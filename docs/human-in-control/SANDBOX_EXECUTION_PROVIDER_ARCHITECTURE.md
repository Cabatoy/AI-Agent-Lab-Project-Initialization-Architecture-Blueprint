# HIC — Provider-Neutral Sandbox Execution Architecture

**Decision:** ADR-EXEC-001, proposed for review on 2026-11-11. Parent issue #105. **Status: design only; no deployed provider, VM, credential, or agent execution.**

## Why this exists

HIC must let each authorized user choose where an agent task is executed. Oracle Cloud Always Free may be the first proof of concept but cannot become a mandatory product dependency. A personally administered local machine, isolated remote Linux host, future cloud vendor, and explicitly eligible ephemeral CI can be alternate adapters. The current development PC is organization/domain-managed; do not treat its Hyper-V/WSL or corporate credentials as a supported personal sandbox or attempt to circumvent enterprise controls.

A **host VM is not the sandbox**. The host is a place to provision and attest independently constrained *per-attempt execution environments*. Even a cloud instance requires real filesystem, credential, network, process and resource isolation evidence.

This decision is distinct from **LLM provider routing**. A model may run locally or behind an API while a task's tools operate in a separate sandbox. The model provider never receives permission to choose or expand the tool-execution boundary.

## Responsibility diagram

~~~text
User / tenant-specific HIC UI
       | task, user choice, policy and explicit approval
       v
Trusted HIC Control Plane (canonical task/run/attempt/lease/audit)
       | immutable admitted execution manifest + exact task authority
       v
Trusted Execution Admission and Provider Router
       | capability + budget + locality + verified isolation filter
       | signed/authorized per-attempt handoff; NO direct command string
       +--> LocalRootless provider (approved personal host only)
       +--> RemoteLinux provider (OCI adapter initially optional)
       +--> EphemeralRunner provider (future, separately qualified)
       v
Per-attempt sandbox: synthetic/private staged inputs; no secrets; default no network
       | bounded quarantined artifacts + sanitized evidence
       v
Trusted verifier / canonical ledger / separately authorized publisher
~~~

**Ownership:** HIC task, run, attempt, claim/lease and audit truth stays in the existing Control Plane. Provider operations may have their own transient reservation/resource state, but NEVER a second task status machine or independent authority to mark a HIC task Completed. GitHub issue comments, model output, host metadata, user-supplied provider name and unsigned capability claims do not authorize execution.

## Stable terms

| Term | Meaning |
| --- | --- |
| Execution provider | Versioned adapter capable of preparing and inspecting a particular execution backend. |
| Provider installation | Tenant/user-scoped configuration, with environment class, region, permissions, and credential *reference*, never credential material in UI or GitHub. |
| Execution host | VM or machine under the chosen provider. Must be independently identified and attested. |
| Sandbox attempt | Ephemeral, task/attempt-bound isolated environment; immutable source and bounded writable output. |
| Capability attestation | Time-bounded, verifier-consumable evidence that OS boundaries and quotas were **actually enforced**. |
| Admission decision | Trusted policy evaluation of exact manifest, approval, credentials, budget, data locality, architecture and current attestation. |

## Selection policy

- The UI offers **Manual** (one selected provider installation) and **Auto** (rank only an allowlist of verified, eligible installations). Neither mode bypasses specific human approval for the action, destination or cost.
- A task states architecture and tool requirements, source confidentiality/classification, network and data residency rules, CPU/RAM/PID/output/time limits and approved maximum cost. Selection intersects **all** constraints. Unknown, stale, contradictory or missing evidence results in BLOCKED or UNVERIFIED.
- A provider's marketing label (Free Tier, rootless, isolated, hosted or managed) does not satisfy capability evidence.
- No automatic switch to another cloud, paid tier, region, account, environment or less-isolated host when a provider fails; obtain a new scoped consent and fresh authority instead.
- User-defined provider integrations are private by default. Later shared/community adapters require review and signatures; do not load user-submitted adapters into the trusted core by default.
- Multi-tenant deployment requires identity-scoped provider configuration, per-tenant quotas, isolated artifact storage and an enforceable owner binding on every operation. A single-user local prototype is not proof of multi-tenant isolation.

## Execution boundary and lifecycle

1. **Inspect:** Read capability declarations and freshness of OS-level isolation evidence. Read-only; no run permissions.
2. **Admit:** Trusted service verifies human-signed reviewed intent, authoritative HIC task revision/approval/revocation, provider selection consent, exact Git repository/base SHA, immutable source manifest, model/tool limits, time and budget. An HTTP response from an untrusted provider is not the source of these facts. Admission must be tied to a current canonical ledger claim (not the legacy unauthenticated approve endpoint).
3. **Prepare:** Provision a unique and owned disposable sandbox reservation, stage exact digest-verified nonsecret fixture/source bytes. Prepare is idempotent by canonical attempt ID and manifest digest; accidental cross-tenant reuse is a hard failure.
4. **Run:** Only an explicitly approved, version-locked allowlisted execution program in a measured isolated environment; never a raw GitHub issue body or model-generated shell command. This stage is disabled until #92/#104/#108 and authority gates are demonstrated.
5. **Observe:** Capture bounded sanitized stdout/stderr, structured resource/runtime observations, signed attestations, exit classification. A session becoming idle is not success.
6. **Verify and publish:** Quarantine output. Trusted validator under #67 checks path, size, type, digest, scope, diff and tests. GitHub App broker stays outside sandbox; a separate publication approval binds the exact diff, repo/ref and PR target.
7. **Cancel/reconcile/cleanup:** Stop with fencing and bounded cancellation, recover idempotently after restart, delete **only** explicitly owned sandbox IDs. Never prune the host or reset shared Podman/WSL/VM resources.

## Required OS/security properties

- Verified CPU, memory and PID enforcement (read from cgroups and adversarial tests, not just CLI flags); hard timeout/output limits and controlled concurrency.
- Immutable image/digest and architecture compatibility, non-root process, least privileges/capabilities, seccomp and appropriately restricted syscalls, safe disposable rootfs and writable tmpfs.
- No home/host checkout/enterprise drives, daemon sockets, SSH agent, cloud metadata token, provider API key, local model secret, credential environment or arbitrary device passthrough.
- Default deny network and DNS. If a task needs egress later, use an explicit narrow network policy bound to its approval and a separately verified gateway; never infer egress disabled because a VM has no public IP.
- Immutable read-only staged inputs, file-descriptor-based no-follow path validation where available, safe archive extraction and quarantined outputs. String path checks alone are not TOCTOU protection.
- Independent trusted verifier of proofs and remote replies, anti-replay, revocation/cancellation race handling and resource owner identity; provider self-report is only supporting evidence.
- Corporate-managed local hosts are out of scope unless organizational permission and technical enforcement are independently established; do not copy corporate source/credentials to personal OCI.

## Tradeoffs and fallback

| Approach | Advantage | Risk / gate |
| --- | --- | --- |
| OCI Always Free ARM64 VM | May avoid enterprise-managed workstation and recurring VM fees | Capacity and idle reclamation, ARM64 compatibility, region/account quota, cloud trust, no automatic sandbox guarantee |
| Personal local Linux VM/rootless | Locality, potentially no cloud bill | OS controller delegation, host credentials and compliance must be verified |
| Alternate cloud/dedicated VM | Configurable reliability and regions | Billing, IAM, data locality and host isolation must be individually verified |
| GitHub-hosted ephemeral runner | Good for ordinary CI | Not assumed suitable for arbitrary hostile agent-produced code or private long-lived state; terms/limits and threat model differ |

## Related boundaries and milestones

- #105 parent; implementation candidates #106 (offline contract/fakes), #107 (OCI readiness), #108 (later synthetic sandbox test), #109 (later UX).
- #67 artifact trust; #63 executor safety; #92/#95 local sandbox history; #104 local diagnostic history.
- #102/#103 signed work-order/read-only authority are **not** live task claim or execution authority.
- Existing docs: ARCHITECTURE.md, INTEGRATION_CONTRACTS.md, RUNNER_ADMISSION_V1.md and PRODUCT_VISION.md.
- No existing working runtime, customer UI, model endpoint or credential should be modified by adopting this ADR.
