# HIC — Sandbox Artifact and Credential Boundary (design proposal v0.1)

> **Status: proposal only.** Not a runtime permission, deployed isolation backend, security review, or user approval. Related: #63, #67, experimental #65.

## Non-negotiable boundary

The existing Control Plane remains authoritative for task/run transitions and human approval; sandbox inputs, model output, Git contents, generated logs and produced artifacts are hostile data. Do not introduce a second state machine. The sandbox may never hold a GitHub App private key, access token, Control Plane signing credential, database password, Redis authentication secret, or host container daemon socket.

Trusted staging validates immutable inputs before the untrusted container starts. Trusted verification validates immutable outputs after the container exits and before any authorized GitHub publishing. The trusted publisher is a separate, human-approved step with an installation-scoped credential broker.

## Versioned input manifest

The following is an **illustrative schema**, not an implemented API. Define exact canonical JSON/encoding and cryptographic binding before implementation.

```json
{
  "schemaVersion": "hic.sandbox.input.v1",
  "runId": "<uuid>",
  "taskId": "<uuid>",
  "decisionId": "<approved exact plan decision id>",
  "repository": { "provider": "github", "installationId": "<trusted id>", "owner": "<owner>", "name": "<repo>", "baseCommitSha": "<40 hex>", "sourceTreeDigest": "sha256:<64 hex>" },
  "policy": { "id": "<approved policy id>", "imageDigest": "sha256:<64 hex>", "network": "none", "maxFiles": 100, "maxTotalBytes": 1048576, "timeoutSeconds": 60, "allowedRelativePaths": ["message.txt"] },
  "inputManifestSha256": "<digest of canonical trusted fields>"
}
```

**Binding:** trusted control plane must fetch and cross-check run/task/decision/repository/installation/base/approved file-operation scope from authoritative persistence; never accept these values solely from agent-supplied JSON. Treat missing or stale data as hard rejection. Source archives fetched by pinned commit must be independently hashed; SHA alone does not validate the downloaded archive representation. Do not use `main` or moving branch names as trusted source identifiers.

## Materialization checks — deny by default

1. Download into isolated staging with strict size/decompression limits. Verify repository identity, full base commit and expected source-tree digest. Verify bounded entry count and total decompressed bytes before extraction.
2. Normalize relative paths consistently with target OS and Git. Reject absolute paths, `..` components, empty segments, NUL, trailing dots/spaces on Windows, Windows drive/UNC syntax, case-insensitive collisions, duplicate paths, reserved device names, and unexpected Unicode aliases.
3. Reject links (symbolic, hard), device nodes, sockets, FIFOs, alternate data streams, unexpected executable modes, `.git` metadata spoofing and all path types not explicitly approved. Test a symlink flip/TOCTOU concurrent rename attack during verification and copy.
4. Use descriptor-based no-follow/openat-style primitives or staging ownership with unshared immutable snapshots; strings and prefix checks alone are insufficient.
5. The container sees only verified read-only inputs and a private size-bounded writable scratch path, with non-root UID, no capabilities, no network, no host worktree or daemon socket, no secrets and enforced CPU/memory/PID limits. Fail closed when delegated cgroup controllers are missing.

## Versioned output manifest

```json
{
  "schemaVersion": "hic.sandbox.output.v1",
  "runId": "<uuid>",
  "inputManifestSha256": "<trusted input digest>",
  "files": [{ "path": "message.txt", "sha256": "<64 hex>", "byteLength": 24, "mode": "regular-non-executable" }],
  "boundedLogDigest": "sha256:<64 hex>"
}
```

The output manifest is **untrusted evidence**, not authorization. Independently walk actual filesystem entries and validate paths, ownership, file type, mode, byte limits, content hashes and no links or escapes. Reject unlisted creations/deletions and any mismatch between manifest and bytes. Never import arbitrary paths, git objects, dotfiles, executable scripts, or host paths. Handle filenames and structured logs as hostile inputs; logs have explicit byte caps and cannot confer task status.

## Publisher isolation

Trusted publisher must re-read the exact human publication approval, repository installation authorization, immutable source base, approved artifact digest and run terminal status before write. It receives only verified bounded bytes—not sandbox credentials or direct filesystem access. Use idempotency keys and read-after-write reconciliation for uncertain GitHub commit/push/PR results. Any plan approval or Git-plan preview on its own is insufficient permission to publish.

## Security acceptance matrix

| Gate | Evidence required | Current state |
|---|---|---|
| CPU, PID, memory limits | Synthetic bounded workload + authoritative counters | Proven only in GitHub-hosted synthetic [run 42593623071](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/actions/runs/42593623071) |
| Untrusted input staging | Negative traversal/archive/mode/TOCTOU tests with immutable digest | Not proven |
| No host/secret exposure | Mount/env/process boundary inspection and negative exfil attempts | Not proven |
| Network restriction | DNS, IP, HTTPS negative probes and bypass cases | Not proven |
| Output import | Hostile file type/path/hash/size/collision checks | Not proven |
| Publisher separation | Installation/decision/digest revalidation and replay/reconciliation tests | Not proven |
| Cancellation and leases | Crash/retry/replay + exactly-once observable external effects | Not proven |
| Independent review | Threat model and code review pinned to exact deployed SHA | Not complete |

## Operational controls

- Keep existing HIC Worker stopped and repository execution returning HTTP 409 until **all** isolation and review gates pass.
- Preserve local HIC preview data and original `hic-agent-sandbox` PR #1; all attack fixtures must be synthetic and disposable.
- Never interpret green tests, generated plan text, `queued` run state or synthetic consent as human permission for actual execution or publication.
- Review proposed implementation against `ENGINEERING_MAINTENANCE.md`; use exact-head evidence and explicit operator approval for protected writes.

**Next implementation slice:** build pure, deterministic manifest/path/artifact verifier with negative unit tests and disposable fixtures on a separate feature branch; do not connect to live Worker until a human-approved integration plan and independent boundary review.
