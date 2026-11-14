# Relay → AWS staging: offline observation contract (Draft)

This change defines a **local-only, non-executable** probe and response validator. It does **not** connect to AWS, authenticate, transmit receipts, create infrastructure, start a container, or authorize agent execution.

## Input

`buildStagingProbe({probeId, receiptBytes})` accepts a local `hic.dev-relay-ack/1` receipt with `type=synthetic-ping`, `status=seen-untrusted`, and `executionAllowed=false`. It rejects review-only proposals, arbitrary commands, additional fields, malformed UUIDs, and oversized receipts. The output contains `probeId`, `orderId`, and SHA-256 of the exact receipt bytes, with `requestedAction=observe-only`.

## Response

`verifyStagingResponse(response,request)` accepts only a correlated `hic.staging-observation/1` object with `status=observed`, `agentExecutionAllowed=false`, and `runtimeIsolationVerified=false`. A matching response is **not** a proof of AWS provenance or isolation; it remains `observed-untrusted` until independently authenticated transport and attestation are implemented.

## Next gated work

1. Decide authenticated AWS transport (no corporate AWS profile fallback; dedicated personal identity, minimal permissions).
2. Implement remote allowlisted observation endpoint/runner and a transport adapter separately; no shell interpolation or user-controlled commands.
3. Bind a signed or authenticated response to the exact probe, with replay/expiry checks and host attestation. No credentials in logs.
4. Exercise real Windows → AWS → Windows synthetic probe and negative cases before any HIC task or agent enablement.

Offline tests: `node --test test/aws-staging-contract.test.mjs` from the relay package. The existing receive-only CLI remains unchanged.
