# HIC — AWS Rootless Podman Sandbox Preflight

## Executive Summary

This is a disabled-by-default, offline-first preflight contract for a future
Linux rootless Podman execution boundary. It evaluates evidence fail-closed;
it does not launch a
container, execute agent-generated code, create AWS resources, or authorize a
HIC task. `AgentCodeExecutionAllowed` is always false.

The implementation provides manifest/evidence validation, sanitized tri-state
results, and interfaces for a read-only Podman inspector, an explicitly
authorized synthetic probe, and a trusted evidence verifier. It has no concrete
runtime adapter. The unit tests use fake evidence and a fake verifier; even a
passing offline test therefore proves only the validator's behavior, not
Podman, cgroups, AWS, or host isolation.

## Technical Annex

### Current implementation boundary

- [RootlessPodmanSandboxPreflight.cs](../../src/NativeHost/RootlessPodmanSandboxPreflight.cs)
  defines the immutable manifest, fixed security policy, tri-state evidence
  contract, interfaces, digest validation, freshness/binding checks, replay
  rejection contract, and sanitized report.
- `IReadOnlyPodmanInspector`, `IAuthorizedSyntheticPodmanProbe`, and
  `ITrustedSandboxEvidenceVerifier` are contracts only. No implementation
  launches `podman`, a shell, a container, or a network request.
- No Control Plane, Worker, task status, approval endpoint, execution ledger,
  launcher, or Relay path is wired to this component. It does not create an
  independent HIC task state machine or an execution grant.
- The manifest contains immutable image/input digests, explicit positive CPU,
  memory, PID, timeout and output limits, a fixed relative output root, a
  challenge nonce, policy digest, issue/expiry time, and its own canonical
  digest. It rejects mutable image tags, modified/stale manifests, and any
  manifest claiming agent-code execution is allowed.
- Evidence must match the exact manifest, probe ID, nonce and image digest,
  remain fresh, pass an injected trusted verifier, and carry the exact required
  runtime measurement plus negative-test evidence codes for each property.
  CLI-configuration evidence alone cannot satisfy a check.
- Each signal may carry a bounded raw measurement for the trusted verifier to
  compare with the manifest limits. It is never copied into the report or
  logged by this component; the production verifier must validate numeric
  cgroup/runtime values rather than trusting evidence codes alone.
- A production verifier must validate the authentic origin, challenge,
  manifest binding, actual cgroup/runtime measurements and negative-test
  results. There is deliberately no built-in verifier or self-attestation
  shortcut in this slice.

### Evidence states

| State | Meaning |
|---|---|
| `verified` | Reserved for future independent runtime verification; this offline slice downgrades apparently verified checks to `unverified` and does not produce this status. |
| `blocked` | Trusted evidence demonstrates a failed control, or an explicit security policy denies execution. |
| `unverified` | Evidence is missing, rejected, replayed, stale, conflicting or not independently verified. No execution follows. |

The sanitized report contains only the probe ID, valid image/manifest digests,
per-check states, fixed reason codes, and `AgentCodeExecutionAllowed=false`.
It omits raw measurements, environment values, mount paths, credentials,
socket names, and provider-specific resource IDs.

### Required checks

The evidence contract independently covers rootless Podman and user namespace
behavior; non-root identity; CPU, memory and PID cgroup enforcement; read-only
root and staged input; restricted output; capability removal,
no-new-privileges and seccomp; network namespace, outbound, DNS and cloud
metadata denial;
environment/credential isolation; SSH/cloud credentials and container socket
exclusion; host, home and sibling-worktree isolation; symlink/traversal denial;
exact-owner bounded cleanup; timeout and output limits; and immutable image
digest plus manifest validation.

Most checks require both the defined runtime measurement and its corresponding
negative test; cleanup requires an ownership receipt and negative test, while
immutable image/manifest requires digest validation and a runtime measurement.
The injected verifier is responsible for ensuring those codes correspond to
real observations rather than caller assertions.
This slice has no independent runtime verification, so checks are not reported
as `verified` even when the contract evidence is complete.

### Offline test evidence and limits

The operator independently reported the focused .NET test result in the AWS
workspace: build succeeded (4.2 seconds); **23 passed, 0 failed, 0 skipped**
(test duration 2.0 seconds). This is an offline contract-test result, not
evidence of real runtime isolation.

The focused .NET tests exercise missing/forged/replayed evidence, stale and
mismatched manifests, CLI-flag-only claims, writable input/output/workspace
evidence, credential/socket exposure, network/DNS availability, path traversal,
duplicate observations, missing cgroup evidence, incomplete cleanup, and
sanitized output. A fake verifier's accepted result tests only aggregation and
validation code. It does **not** make any real property verified.

No real Podman probe was proposed or run. Rootless/user namespace operation,
cgroup delegation and enforcement, filesystem/mount restrictions, seccomp,
network/DNS denial, host credential isolation, cleanup and AWS host readiness
remain **UNVERIFIED**. No real environment property is currently **BLOCKED** by
this implementation; runtime observations are absent.

### Remaining gates

**Issue #144**

1. Independently review this code, its policy, canonical serialization and
   evidence-verifier contract.
2. Implement and review a trusted verifier and safe rootless-Podman observer;
   bind evidence to an independently authenticated host, challenge, exact
   manifest and freshness window.
3. Obtain separate operator approval before any Podman probe. Use only a
   synthetic disposable fixture on the personally administered AWS EC2 host;
   do not alter EC2/IAM/network/SSH/SSM configuration or existing HIC
   resources.
4. Measure all required controls and adversarial negative tests, including
   actual cgroup quota/throttling, memory/OOM, PID limits, read-only mounts,
   network/DNS denial, credentials/sockets, traversal, replay, timeout, output
   caps and exact-owned cleanup. Record gaps as blocked/unverified.
5. Add independent security review and artifact/output quarantine before
   considering any future executor integration.

**Issue #153**

- Agent-generated code execution and unattended Relay mode remain disabled.
- A verified preflight alone is not approval. #153 remains blocked until #144's
  runtime gates are independently verified and its own authoritative
  Control-Plane approval, claim/replay, cancellation/recovery, artifact,
  credential, and operator-consent gates are reviewed.
- Production execution, model/tool invocation, publishing, merge and deploy
  are outside this slice and require separate approvals.

The only execution claim remains in the existing Control Plane's authoritative
ledger. Duplicate execution prevention, approval, task status and audit must be
implemented there; the preflight component has no local task/run state store.
