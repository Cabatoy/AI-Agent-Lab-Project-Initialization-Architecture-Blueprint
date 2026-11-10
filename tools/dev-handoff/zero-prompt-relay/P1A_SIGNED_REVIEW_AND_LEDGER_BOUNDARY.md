# HIC DEV P1A — signed reviewed work-order boundary (OFFLINE ONLY)

**Status:** Developer-only review library, not installed or executable. This branch does not change the Windows scheduled `AckWatch` or any live HIC Control Plane, Redis, PostgreSQL, Podman, or Git credential.

## Authority

Public GitHub Issues, PR comments, files and the `hic-dev-orders` branch are **transport, not approval**. `src/reviewed-order.mjs` requires an independently provided, operator-controlled and preauthorized Ed25519 reviewer public key (`Map` in memory). The repo and tests contain no reusable private key. Real key provisioning, revocation persistence, key owner enrollment, and authenticated approval UX are **not implemented**.

The versioned envelope uses exact keys (`version`, `kind`, `algorithm`, `keyId`, `payload`, `signature`). Payload bytes must be canonical sorted JSON and are verified with domain-separated Ed25519 over `HIC-DEV-REVIEWED-WORK-ORDER-V1\n<keyId>\n<payload bytes>`. Rejects duplicate/noncanonical JSON, extra fields, untrusted or revoked reviewer, invalid signature, different signer identity and unsupported algorithms. Public keys must never be loaded from the received GitHub document.

Signed fields cover: repository/audience, transport ref, Issue/source, exact reviewed Git SHA (supplied independently to verifier), order ID, HIC task ID, target branch, exact base SHA, allowed developer-tool file paths, review ID, reviewer, digest, timestamps and zero-cost/zero-tool/zero-network limits. This v1 explicitly **requires `executionAllowed=false`** and cannot be upgraded by changing a boolean; live execution needs a separately reviewed protocol.

Successful verification returns `accepted-for-review` with `ledgerVerified:false` and `executionAllowed:false`. **This means cryptographic review only; not authorization to call a model or start a task.** A Git commit SHA supplied by an untrusted order does not count as independent GitHub verification. The caller must separately fetch/verify exact refs and status before trusting them.

## Existing HIC ledger interface

`src/ledger-review.mjs` accepts an injected read-only `readTask` callback in fake/offline tests. It never makes a request itself, and never writes state. A missing task, mismatched task ID, non-Draft status, missing authoritative revision, or read failure is blocked. Even a matched `Draft` row with a revision yields only `observed-draft-unverified`, because no authenticated issuer, claim, lease or signature-to-ledger binding exists.

On current `develop`, `GET /tasks/{id}` does not include `nativeRunRevision` or trustworthy authorization. `POST /tasks/{id}/approve` enqueues a legacy Worker and must **never** be called by developer relay. Proposed native ledger v3 is not deployed with authenticated issuer. Do not run migrations or enable Product Worker.

## Verification

Node >=22; from `tools/dev-handoff/zero-prompt-relay` run `npm run check`. Offline tests generate fresh Ed25519 keys in memory, exercise success and malicious inputs, and verify fail-closed fake ledger decisions. Exact-head GitHub Actions run on Windows and Ubuntu, plus Windows launcher syntax-only parse. This PR adds no SDK, watcher dispatch, network calls, credentials or local autostart.

**Next gates:** operator-controlled key enrollment; verified GitHub source/head/CI; durable HIC ledger task revision and authenticated single-use claim/lease; replay and cancellation across restarts; real #92 OS sandbox attestation; official Copilot SDK auth/entitlement/quota and explicit model cost consent. No real autonomous coding until these gates are independently proven.

*Humans are not message queues.*
