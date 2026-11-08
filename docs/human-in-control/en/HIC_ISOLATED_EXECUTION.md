# HIC agent execution: threat model and isolation plan

## Executive Summary

**Status: security gate open; implementation and release are not ready.**
The proposed smallest boundary for #42 is one rootless Podman container per
run, governed by the existing Control Plane approval, durable run and audit
records. The agent and tests run without network access; the trusted Control
Plane retains GitHub publishing. This is a proposal, not an approved production
architecture.

A no-host-mount, network-disabled, privilege-reduced container and an offline
synthetic build were tested on the Windows Podman VM. Its `memory`, `pids` and
`cpu` cgroup controllers are unavailable. A production backend must not run
without verified resource limits, and must fail closed rather than fall back to
host execution. The existing #43 and #51 host execution paths do not yet meet
this boundary.

## Technical Appendix

### Threats and trust

Repository authors and model outputs control untrusted source, paths, build
definitions, tests, logs and artifacts. A concurrent host actor may replace
paths using symlinks, Windows junctions, renames or swaps between checking and
opening. The Control Plane, its approved plan, broker and validator form the
trusted side; the repository, container and exported bytes do not. Podman is
controlled separately and its API/socket must never be exposed to the agent.

A same-OS-user adversary able to alter the Control Plane process or credentials
is outside what a container alone can protect; separate service identities and
OS ACLs are required. The host administrator, VM administrator and kernel are
trusted. The existing host Git worktree and compiler, and #54's later worktree
read, have check/use races; static path checks do not establish OS isolation.

### Backend comparison and proposed profile

Host worktrees are insufficient. Per-run rootless Podman containers reuse the
existing VM and can provide network, mount, privilege and resource controls,
but share the VM kernel and retain kernel/runtime escape and denial-of-service
risk. A per-run VM or microVM gives a stronger kernel boundary at greater
operational cost and is not yet demonstrated here.

Use an authenticated, digest-pinned image; no host bind mount, socket,
credentials or inherited host environment; a non-root identity; read-only root
and bounded private scratch; no network; dropped capabilities;
no-new-privileges; verified seccomp and available LSM policy; enforced CPU,
memory, PID, disk, time and output limits. Verify effective controls, not just
arguments. Refuse execution when any required control is unavailable. The
Windows/WSL2 VM's filesystem sharing, cgroup delegation and user namespace
configuration must be evaluated separately. No VM configuration was changed.

### Integration plan

1. Reuse the existing actor-bound approval, claim and audit transaction; do not
   create another task state machine.
2. A trusted limited importer verifies repository identity and base commit,
   reads immutable Git objects without exposing a host bind mount, validates
   tree/content identities and rejects links, submodules, special files,
   traversal, path collisions and excessive sizes. A path check followed by a
   host read is not an immutable snapshot.
3. Copy the verified snapshot into private container storage. Bind snapshot and
   image digests, the approved plan, test policy and run ID in durable evidence.
4. Execute the edit and tests only inside the bounded container. Treat all
   output, exit codes and test claims as untrusted evidence.
5. After termination export only bounded regular-file content and a manifest.
   The trusted Control Plane computes digests and verifies exact equality with
   the approved plan and tested artifact. #54 must consume this immutable
   artifact rather than re-reading a host worktree; preserve the separate
   GitHub approval, target identity checks and reconciliation.
6. Cancellation must stop the run and record a clear outcome. On crash, use
   run ownership labels and durable state to reconcile only owned disposable
   resources; never blindly repeat an ambiguous external write. Do not touch
   existing services or volumes.

### Evidence and remaining gates

The [Podman isolation proof](../../../tests/security-contracts/podman-isolation-proof.cjs) uses a locally
pinned test image and checks a non-root UID, zero effective capabilities,
no-new-privileges, absence of a synthetic inherited host variable, two known
socket paths and two known Windows paths, loopback-only networking, a failed
HTTP connection, read-only root, confined in-container symlink/swap and an
offline synthetic build. This is not a full penetration test, proof for every
host path/socket/credential, an actual hostile-host race, or an integrated HIC
backend. The local image ID is not a production image policy.

#43 verifies rejection of a real Windows junction and descendant; Windows
symlink creation was unavailable to the test account. A separate disposable
PostgreSQL SIGKILL test verified persisted claim/audit and rollback of an open
transaction. Actual publication crash, container cancellation/recovery and
artifact-export authorization against the new backend remain untested. #54
mock tests cover duplicate/reconciliation behavior but not this backend.

Before merge: usable isolated VM with verified resource controllers; safe
snapshot/import/export; #43/#51/#54 compatibility and cancellation; real
hostile-host swap, credential/socket, network, traversal/junction, artifact
tampering and crash tests; exact integrated CI and platform evidence; and an
independent human security review. Existing green CI and the synthetic proof
do not replace these gates.
