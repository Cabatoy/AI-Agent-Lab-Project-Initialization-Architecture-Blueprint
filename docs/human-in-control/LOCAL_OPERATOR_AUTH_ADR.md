# HIC local operator authentication and approval keys — proposed ADR

**Status:** Proposal only. No identity provider, key provisioning, schema migration, approval HTTP route or runner execution is introduced by this document.

## Decision context

The HIC stack has a loopback Web UI, Native Host and PostgreSQL-backed Control Plane. Existing origin restrictions and the Native Host ephemeral token guard the current read-only workspace inspection operation. They do **not** authenticate the human who authorizes a particular task. The existing Control Plane task approval endpoint can enqueue a Worker task without strong operator authentication; that endpoint must never become the new native-run approval issuer.

Adversaries include an attacker-controlled browser tab/site (CSRF or DNS rebinding), a local unprivileged process, a forged model/GitHub message, a stale session, replay of a correctly signed receipt, a swapped public key pin, and system rollback/restart after a sleep-related WSL outage. A fully compromised OS administrator or the logged-in Windows account is not within the scope of meaningful cryptographic non-repudiation.

## Proposed controls, pending local Windows validation

1. **Human presence:** For each sensitive approval, require a verifiable human-presence ceremony using a registered device-backed authenticator, preferably Windows Hello or WebAuthn/passkey. Validate exact browser/origin/RP-ID support on the actual Windows machine before choosing this mechanism. Windows username, loopback address, UI clicks, browser Origin, and the Native Host proxy token alone are not acceptable actor proof. If the ceremony cannot be implemented safely, keep approvals disabled rather than silently bypassing.
2. **Session:** Use a short-lived server-side operator session with HTTP-only, SameSite cookies, cryptographically random CSRF protection, exact Host/Origin restrictions, and loopback-only binding. Prevent browser DNS-rebinding and direct Control Plane bypass. Sensitive approvals revalidate user presence and actor binding; a recent generic login is not sufficient.
3. **Consent:** Display the exact task ID, expected revision, action, workspace scope, data read exposure and expiry. An approval binds this immutable intent and cannot be reused after any task edit. Keep the read-only action fixed to workspace.inspect until further review.
4. **Asymmetric issuer key:** Only an authenticated Control Plane authority holds a P-256 private signing key protected by a Windows-compatible protected key store, preferably nonexportable if supported. The Native Host has only the pinned public SPKI key and its trusted key ID. Do not send signing secrets, receipts or untrusted client-provided keys through the Web UI, model prompts or GitHub issues. The source-only P-256 issuer/verifier from PRs #30/#31 is not wired to runtime.
5. **Enrollment and rotation:** Explicitly record which local operator enrolled which issuer key. Publish a pinned public-key registry through a trusted local enrollment path, not a caller-specified remote JWKS URL. Unknown key ID, unauthenticated key replacement, lost private key, conflicting installation identity or revocation epoch mismatch must fail closed. Allow a narrowly bounded retiring-key window only when defined and audited. A revoked key immediately invalidates outstanding grants.
6. **PostgreSQL authority:** The actual authenticated issuer must read the current task version and durable v3 approval, validate actor, revocation and canonical intent digest in a properly isolated transaction, then mint an audience-bound receipt. The native side must require an atomic single-use claim; valid signature alone is replayable. Lock down EXECUTE permissions to the authorized component. Manual schema v3 migration is NOT allowed during existing R2 recovery without separate approved backup and rollout.
7. **Recovery and audit:** Write immutable audit metadata containing only actor reference, task ID, run ID, scope, digest, decision and safe timestamps. Never log raw receipts, task text, environment variables or secrets. R3 heartbeat and R4 crash/reconciliation semantics must prevent repeated execution when a result is unknown. Do not auto-start the legacy Worker or incur paid model usage.

## Rejected shortcuts

- A client-supplied approved=true, CryptographicProofVerified=true, actor ID, or ExclusiveRunClaimRecorded=true field.
- Reusing the unauthenticated legacy task approval endpoint as human authority.
- Trusting localhost, process ownership or an Origin header as the sole identity check.
- Sharing the earlier HMAC signing secret with the Native Host instead of pinning a verifier-only public key.
- Automatic v3 migration on existing R2 PostgreSQL or a WSL reset on approval failure.

## Acceptance gates before enabling runtime actions

- Complete a sanitized Windows-local identity ceremony test, with genuine user presence and documented supported browser origin behavior.
- Verify issuer-key enrollment/rotation/revocation, identity/session expiry, CSRF, Host/Origin checks and stale task revision protection.
- Prove signed receipt fields and pinned audience/key; verify atomically consumed run claims, concurrent replay rejection and an append-only audit record.
- Demonstrate that an untrusted browser/DB role cannot call the native run claim or use client-side booleans to mint authority.
- Exercise loss of PostgreSQL, Redis, UI tab, Podman VM and cancellation/restart mid-claim. Fail closed rather than guessing that a task completed.
- Preserve the current native runtime execution-disabled flag until all gates, including separate schema-v3 migration consent, pass.

## New independent CI proof

The additive workflow hic-ledger-concurrency-ci.yml runs on a disposable GitHub-hosted Linux VM, not the user's PC. It boots an unexposed PostgreSQL 16 container, applies the fresh v2 schema followed by the manually gated v3 SQL, and seeds one synthetic approval. Twenty-four independent database clients compete for the same run. The acceptance condition is **exactly one success, 23 denials, one persisted run claim, one claimed audit event and unchanged Draft status**, plus confirmation that a fresh untrusted SQL role lacks EXECUTE privilege. This supports the correctness of the SQL claim primitive under the tested contention; it does not establish a real user identity, true approval issuer, end-to-end execution authority or protection against an OS administrator.
