# HIC — Execution Provider Contract v0 (proposal, no executable implementation)

**Status:** design/protocol sketch, 2026-11-11; issue #106; depends on architecture #105. This is a new *execution* provider interface, not the existing language-model provider transport. No live endpoint, production schema or migration is authorized.

## API boundary

Proposed operations: DescribeCapabilities, ProbeIsolation, PrepareAttempt, StartAttempt, InspectAttempt, CancelAttempt, CollectEvidence, CleanupAttempt. Backend-specific implementation details (OCI instance OCID, SSH tunnel, Podman ID, Kubernetes pod, CI workflow) belong behind adapters and may be returned only in redacted, provider-scoped IDs. The trusted Control Plane is the sole authority for task/run/attempt status.

| Operation | Mutability | Prerequisite | Result |
| --- | --- | --- | --- |
| DescribeCapabilities | Read-only | Tenant-scoped provider installation | Declared architectures, tools, allowed regions, capacity and cost class; **not** security proof |
| ProbeIsolation | Probe-only, separately scoped | Disposable, authorized synthetic fixture | Fresh, externally checked OS enforcement evidence; no live agent |
| PrepareAttempt | Creates bounded disposable resource | Current signed approval + ledger claim + verified capabilities | Idempotent reservation and ownership marker |
| StartAttempt | Executes | All gates and separate operator enablement | Running handle only; cannot complete HIC task itself |
| InspectAttempt | Read-only | Exact attempt + actor/tenant | Bounded status/evidence, freshness, error class |
| CancelAttempt | Constrained mutation | Authenticated owner/authority | Fence/stop with measurable acknowledgment |
| CollectEvidence | Read-only and quarantined | Exact attempt + output rules | Size/hash-bound sanitized evidence and artifacts |
| CleanupAttempt | Deletes scoped resources | Exact ownership and valid lease/fencing | Confirmed scoped cleanup; no wildcard/prune |

The contract must be versioned; unknown major versions fail closed. Contracts are *not* promises that any provider currently supports these operations.

## Proposed canonical request manifest

Example only. IDs, digests and values are placeholders and MUST NOT be used as actual grants. The issuer of each trusted field is the authoritative Control Plane, not a browser, model or GitHub message.

~~~json
{
  "contractVersion": "hic.execution-request/0",
  "tenantId": "tenant-placeholder",
  "actorId": "authorized-human-placeholder",
  "taskId": "task-uuid",
  "runId": "run-uuid",
  "attemptId": "attempt-uuid",
  "taskRevision": 1,
  "approvalId": "approval-uuid",
  "approvalEpoch": 1,
  "manifestDigestSha256": "64-hex-placeholder",
  "repository": {
    "installationId": "trusted-repository-installation-reference",
    "fullName": "approved-owner/approved-repo",
    "baseCommitSha": "40-hex-placeholder",
    "sourceTreeDigestSha256": "64-hex-placeholder"
  },
  "executionPolicy": {
    "allowedActions": ["offline-synthetic-fixture"],
    "allowedInputDigests": ["64-hex-placeholder"],
    "allowedOutputPaths": ["output/result.json"],
    "cpuMillicores": 500,
    "memoryMiB": 256,
    "pidsMax": 64,
    "timeoutSeconds": 30,
    "maxOutputBytes": 1048576,
    "networkPolicy": "deny-all",
    "privacyClass": "nonsecret-synthetic",
    "allowedRegion": "operator-approved",
    "maxEstimatedCharge": "0",
    "currency": "USD"
  },
  "providerSelection": {
    "mode": "manual",
    "installationRef": "user-owned-provider-installation",
    "backendType": "remote-linux-vm"
  },
  "idempotencyKey": "opaque-stable-attempt-key",
  "expiresAtUtc": "illustrative-utc-expiry"
}
~~~

**Illustrative numeric limits** are only for an eventual synthetic probe; they are not product defaults or permission to start a workload. A real implementation must define a strict schema for digest encodings/algorithms, canonical byte serialization, UUIDs and time validation before signing. No user-supplied object may claim to be signed because it merely includes a signature field. Credentials are referenced by a trusted broker outside the execution environment, never serialized into this manifest.

## Proposed capability and evidence envelope

~~~json
{
  "contractVersion": "hic.execution-attestation/0",
  "providerInstallationRef": "opaque-reference",
  "providerInstanceRef": "opaque-reference",
  "hostArchitecture": "aarch64",
  "imageDigest": "sha256:verified-image-digest",
  "observedAtUtc": "illustrative-utc-time",
  "expiresAtUtc": "illustrative-utc-time",
  "probeId": "synthetic-unique-id",
  "policyDigest": "sha256:verified-policy-digest",
  "evidence": {
    "cgroupCpuLimitEnforced": "unverified",
    "cgroupMemoryLimitEnforced": "unverified",
    "cgroupPidsLimitEnforced": "unverified",
    "filesystemIsolation": "unverified",
    "hostCredentialIsolation": "unverified",
    "networkAndDnsDenied": "unverified",
    "cleanupOwnership": "unverified"
  },
  "trustedVerifier": "not-enrolled",
  "executionAllowed": false
}
~~~

Each evidence field is an **observation** with tri-state VERIFIED/BLOCKED/UNVERIFIED, not a boolean invented by a backend. Attestation must bind an unpredictable challenge/nonce, exact host identity, OS image digest, attempt policy, observed enforcement, trusted verifier identity and expiry. Provider-supplied telemetry by itself is never equivalent to independent verification. A read-only cache is not a durable claim ledger.

## Invariants and error behavior

1. A provider cannot call the legacy Control Plane /tasks/{id}/approve to authorize a developer task; that endpoint can enqueue the old Worker. Provider status NEVER maps directly to canonical Completed.
2. Every mutating operation is bound to existing task/run/attempt, active claim/generation, approval revision/revocation epoch and exact manifest hash. Stale retry or changed approval is rejected without side effects.
3. Idempotency deduplicates Prepare/Start/Cleanup, including concurrent retries and crashes; same key with different manifest hash returns CONFLICT. Cancellation and expiry must fence stale providers.
4. If exact host, architecture, CPU/RAM/PID enforcement, filesystem, secrets, egress, resource caps, attestation freshness, free-tier eligibility or budget is unknown: no Start.
5. Error codes are typed (AUTHORITY_UNVERIFIED, ATTESTATION_STALE, CAPABILITY_MISMATCH, COST_NOT_ALLOWED, QUOTA_UNAVAILABLE, REGION_NOT_ALLOWED, APPROVAL_REVOKED, CONFLICT, CANCELLATION_UNCONFIRMED, CLEANUP_UNVERIFIED). Network errors are not successful completion.
6. Provider routing is tenant-scoped; manual selected instance cannot be silently replaced. Auto cannot cross price, trust, data region or cloud/local boundaries without renewed approval.
7. GitHub App publisher credentials, provider admin credentials, model API keys and Control Plane storage are unavailable inside the untrusted sandbox. Artifacts are quarantined and validated by #67 before output authorization.

## Offline test matrix (issue #106)

- Correct fake provider (local and remote) and compatibility negotiation; incompatible major version, CPU architecture and tool capability denied.
- Manual provider failure, no arbitrary fallback; Auto filters only signed, fresh and eligible evidence; paid/cloud/region boundary demands new user consent.
- Forge or replay capability proof, mutate source/base SHA/approval after selection, supply old nonce, wrong tenant, revoked key, same idempotency key with changed manifest: blocked.
- Simultaneous Prepare/Cancel, crash/restart, duplicate callback, stale completion, double cleanup, timeout, unknown ownership: fenced/reconciled or blocked.
- Quota failure, unknown cost, billing-unit ambiguity, resource overcommit and unexpected egress: never run.
- Output traversal, symlink/reparse, huge logs/archives, fake success, session.idle and malicious provider error text: sanitize and quarantine.
- In all offline tests: zero HIC live DB writes, zero Worker/GitHub/cloud/SDK/model calls, zero new host resources.

## Release gates

1. Documentation review (#105, this contract, threat model).
2. Pure offline fake adapter + deterministic tests on exact Git SHA (#106).
3. Account/host readiness with account-owner nonsecret evidence (#107); no credentials in public files.
4. Separate approved remote synthetic probe (#108) and trusted independent attestation, including running security checks.
5. Canonical signed live ledger claim/lease/revocation and publication safety gates (#102, #63, #67) **plus explicit one-shot model/tool and cost consent**.
6. Only then begin a single live developer agent task. No automatic merge/deploy.
