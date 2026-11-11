# HIC — Provider-neutral Sandbox Delivery Plan and Ordered Queue

**Prepared:** 2026-11-11. **Status:** approved to plan/document/review and create tracked tasks; no live cloud, machine, agent or model execution approved. Parent #105. Maintain one canonical HIC task state machine.

## Current baseline / provenance

- #103 (signed authority P1A.2, stacked on #101) has exact-head offline GitHub Actions on Windows and Ubuntu at 0f8c89fd467abd04fef805dc1bb6675fe796d179; real authenticated ledger revision/claim/approval key bootstrap remain blocked.
- #95 has offline synthetic sandbox contracts; **actual OS isolation is UNVERIFIED**.
- #104 proved missing CPU/memory/PID controller delegation in the existing local rootless Podman chain. Later VM preparation was BLOCKED by non-elevated session and unverified Linux image; a subsequent administrator PowerShell session could read Get-VMHost, but no dedicated local VM was built. The local workplace PC is organization-managed; local execution is **optional, not a prerequisite**.
- Operator will sign up for OCI Always Free independently; no validated tenancy, VM, account API key, Docker/Podman or SDK service in OCI exists today.
- No cloud provider, architecture target or account choice should change the existing Control Plane's task ownership and audit responsibilities.

## Ordered backlog with explicit parallel lanes

| Order | Issue | Lane | Deliverable | Prerequisites / accepted evidence | Gate |
| --- | --- | --- | --- | --- | --- |
| 0 | #105 | Docs / architecture | ADR, proposed contract, threat model, optional OCI runbook, delivery plan in Draft PR | Read-only review of existing HIC docs and official OCI facts | **This work** |
| 1 | #106 | Offline engineering | Minimal provider-neutral interface, two fake backends, admission/selection rules, deterministic adversarial tests | #105 architecture reviewed, keep runtime off | No real execution |
| 1 parallel | #107 | Cloud readiness | OCI free eligibility and ARM64 checklist with no credentials | Owner may create account independently | No provisioning |
| 2 | #108 | Runtime security | One disposable *synthetic* remote sandbox probe with OS-level evidence | #107 account readiness; #92/#104 policy; #67 artifact safeguards, new explicit approval | **BLOCKED now** |
| 2 parallel | #102 / #103 | Authority | Trusted server ledger snapshot, key enrollment/revocation, exact claim/lease reconciliation | Separate reviewed authority and safe staging; existing draft alone insufficient | **BLOCKED for live execution** |
| 3 | #109 | Product UX | Manual/Auto provider selection, policy and budget/verification UX | #106 interface; existing #79 and #44 product design | Design/offline tests first |
| 4 | #63 / #67 / #42 / #49 | First live vertical slice | One exact-SHA, owner-approved, single developer task -> quarantined diff/tests -> separate Draft PR publication | Verified isolation + ledger + model entitlement/quota + separate tool/cost approval | **Not authorized** |

### What can be done now without the OCI account

- Finish and review #105 documentation; anchor differences from existing model-provider docs, artifact boundary and runtime admission.
- Specify #106 pure interface/fake adapter tests; no cloud calls, no new Product Worker, no new task states, no database migrations. Record a Draft PR only when code+tests exist.
- Write clear UI copy and policy matrices for #109, not enable cloud login or paid routing.
- Review existing #102/#103 authority gap and #67 artifact quarantine without merging or changing protected runtime.
- Check official OCI Always Free eligibility and OS image docs periodically **on demand** before provisioning; do not automate claim of indefinite availability.

### What requires account owner input

- Report only that the personal free account is active, MFA enabled, free eligible home region and shape identified, and whether its A1 free shape is available. **Never send a private key, SSH key, token, payment card, exact account ID, public IP or console screenshot showing secrets.**
- After readiness, operator separately authorizes any cloud VM/network/volume or provider credentials, and approves cost/region/network scope.
- A third explicit approval is needed for runtime synthetic probe #108. A fourth independent consent may later be needed for real model/tool calls and their charges.

## Acceptance checklist for the next release gates

- [ ] Provider contract version and canonical identity field ownership reviewed (does not shadow HIC task ledger).
- [ ] Manual selection + Auto policy disallow cross-cloud/paid/region fallback without new approval.
- [ ] Fake provider tests pass Linux/Windows exact-head, with race, replay, budget, cancellation and cleanup denial coverage.
- [ ] OCI account free allowance/quotas checked in owner's account (not generic website only).
- [ ] All relevant image/tool artifacts architecture-compatible and image digest/provenance checked.
- [ ] Verified CPU/memory/PID enforcement, filesystem/secret isolation, no sandbox DNS/egress and exact-owned cleanup in a **real** disposable probe.
- [ ] Trusted ledger revision/signature/claim and revocation/recovery independently proven; current /tasks/{id}/approve must not be repurposed.
- [ ] Host and sandbox do not expose publication keys or Control Plane DB; broker publishes only after separate approval.
- [ ] No out-of-budget/unverified provider can silently run or trigger paid fallback.

## Evidence/reporting format

For any future work item, record:
- issue/branch/commit SHA/PR URL, exact baseline and CI run(s);
- scope attempted and operator approval **specific to that step**;
- platform/provider/architecture and actual resource identity (redacted);
- tests by platform with actual counts and failure reason;
- immutable manifest and image digest / policy revision;
- separate **DESIGNED**, **OFFLINE_TESTED**, **VERIFIED**, **UNVERIFIED** and **BLOCKED** fields;
- what was NOT executed (DB/Worker/VM/model/GitHub publish/merge/deploy), protected resources touched and rollback/recovery;
- next clearly scoped dependency and responsible owner.

## Finish line for the first provider-neutral milestone

A reviewed Draft PR containing coherent ADR, bounded interface, threat model and runbook with no secrets, plus prioritized issues. This is **planning complete**, not sandbox complete. OCI may later be substituted with any approved provider whose adapter passes the same security policy and acceptance tests.

**Humans are not message queues.**
