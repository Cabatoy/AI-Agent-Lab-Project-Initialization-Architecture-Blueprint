# HIC signed native approval receipt v1 — verifier-only

**Not executable. Not deployed.** `NativeApprovalReceiptVerifier` is an internal, pure cryptographic verifier in the Native Host project. There is NO signing/issuing endpoint, no `HIC_NATIVE_APPROVAL_KEY` environment variable, no HTTP integration, no token accepted from the browser as permission, and the current host still exposes only `workspace.inspect` and reports `taskExecutionEnabled=false`.

## Format (canonical signed payload)

A future **authenticated** Control Plane would issue a receipt after an explicit UI approval and durable PostgreSQL v3 approval record. Exactly these 15 newline-separated ASCII fields are HMAC-SHA256 signed, in order:

1. `hic-native-approval-receipt-v1` domain separator
2. protocol version (exactly `1`)
3. issuer (`hic-control-plane`)
4. audience (`hic-native-host`)
5. key ID (1–64 ASCII letters/digits/`.`/`-`/`_`)
6. approval UUID without hyphens
7. task UUID without hyphens
8. run UUID without hyphens
9. actor UUID without hyphens
10. positive task revision
11. action (`workspace.inspect`)
12. scope (`workspace:configured:read`)
13. 64-character lowercase SHA-256 digest of the canonical intent
14. issued Unix epoch seconds, UTC
15. expiry Unix epoch seconds, UTC

The signature is exactly 32 bytes of HMAC-SHA256, encoded as **unpadded 43-character base64url**. The verifier requires a separately supplied trusted issuer key of at least 32 bytes, an exactly matching trusted key ID, no subsecond timestamps, a five-minute maximum duration, issued time not in the future, and unexpired lifetime. It uses constant-time MAC comparison and does not log raw receipts or key material. It never signs tokens or derives the key from an API request.

## Explicitly missing trust infrastructure

- How the CP stores/rotates its signing key, protects signing authority and delivers the trusted verifier key to a loopback-only native runner is **not yet implemented**. The ephemeral `HIC_NATIVE_TOKEN` is a proxy token and MUST NOT be reused as the approval-signing key.
- The CP's current unauthenticated `/tasks/{id}/approve` endpoint is NOT a valid issuer; a new authenticated approval issuer and explicit actor identity are prerequisites.
- Signature verification alone is replayable. The manually opt-in PostgreSQL **v3 approval and single-run claim** from PR #28 is still required, and must be linked to the verified receipt's task, run, action, scope, revision and intent digest under a transactional authority.
- The verified receipt must never be constructed from user-controlled `proofVerified=true` or similar fields. Future production code must compute and bind cryptographic proof, task revision and atomically claimed run from trusted sources.
- Cancellation, revoked key epochs, runner isolation, audit, R3/R4 recovery, no Worker startup and no paid cloud fallback remain gated.

## Tests and next delivery

The new unit tests locally construct a **synthetic-only** HMAC for verification. They cover a valid receipt, altered fields, wrong keys/key IDs, hostile scope/action, excessive or expired grants, malformed signatures, newline injection and the fact that signatures alone cannot stop replay. The tests do not create approvals or execute a task.

Next: authenticated issuer/key lifecycle design and a trusted bridge between v3 approval claim and the Native Host; only after a future independent security review should any task-backed execution endpoint be added. Existing user R2 PostgreSQL, Worker and Podman remain untouched.
