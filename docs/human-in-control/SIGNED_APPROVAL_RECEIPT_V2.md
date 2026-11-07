# HIC approval receipts v2 — public-key-only Native Host verification

**Status: independent, UNWIRED security primitive; no execution, no signing issuer, no runtime key provisioning.** v1 HMAC code remains as historical prototype and is never used by the live host. This v2 protocol is the candidate for future HIC runner approval because only Control Plane requires a private signing key; Native Host can verify with a pinned public key.

## Exact protocol

- Algorithm: ECDSA P-256 with SHA-256, IEEE P1363 fixed-width signature `r || s`, exactly 64 bytes; URL-safe unpadded base64url signature (86 ASCII characters).
- Key material: trusted P-256 SPKI public key bytes, length 80–256, supplied by the local authority's pinned key registry (NOT by browser/body). Key ID must match that registry independently. Never accept arbitrary JWKS URL, caller-selected key, self-signed receipt or the UI's `approved=true` flag.
- Receipt: existing `SignedNativeApprovalReceipt` contract with **Version 2** and domain separator `hic-native-approval-receipt-v2-ecdsa-p256-sha256`, followed by exactly the same 14 canonical bound fields as v1 (fixed issuer `hic-control-plane`, audience `hic-native-host`, key ID, approval/task/run/actor UUIDs, positive task version, `workspace.inspect`, configured-read scope, lowercase SHA-256 digest, whole-second Unix issued/expiry).
- Maximum validity five minutes, no future issuance, expired signatures fail closed, no control/newline in constrained values, no arbitrary arguments or shell actions.
- The Native Host **does not** import or store the private key. Its verifier only imports SPKI public keys and checks curve, signature format, message bytes and pinned ID.

## Trust boundary and staged rollout

The earlier symmetric HMAC v1 prototype forces the verifying Native Host to possess a shared signing secret, which weakens separation if that host is compromised. This v2 verifier exists separately; v1 is still unwired. Before any real execution enablement, the team must (1) implement authenticated human identity and durable scoped CP approval, (2) securely provision/control private signing key and public-key pinning + rotation/revocation, (3) bind the signature to schema v3 approval and atomically consume a single-use claim, (4) test R3/R4 crash recovery, audit, cancellation and replay, and (5) explicitly enable the read-only native action behind a reviewed deployment gate.

**This source change does not** introduce a CP signer, API route, DB migration, Worker start, local WSL or agent task execution. Verifying a signature only proves possession of the issuer private key, not authorization, because no approved identity/receipt issuer exists yet.

## CI

Unit tests create ephemeral P-256 issuer keys **only within the test process**. Tests prove public-only verification, wrong key/curve rejection, tampering of every bound field, expired/future/oversized grant rejection, strict signature encoding, and v1 downgrade rejection. No secret key is committed to Git.
