# HIC — Optional Oracle Cloud Always Free Sandbox PoC Readiness

**Status:** operator self-signup planned; NO OCI account access, API keys, tenancy/compartment ID, VM or sandbox attestation currently verified. Research checkpoint 2026-11-11. This is an optional provider feasibility path under #105 and #107; use another provider without changing canonical task semantics.

## Current official product facts to recheck at account setup

Based on the English **OCI Always Free Resources** and **Free Tier** official documentation checked 2026-11-11:

- Ampere A1.Flex ARM64 Always Free allowance: first **1,500 OCPU hours** and **9,000 GB-hours** per month, equivalent for Always Free tenancies to **2 OCPU and 12 GB RAM** over a month. AMD E2.1.Micro free shapes are also available but generally less useful for tool-heavy sandbox tasks.
- Shared Always Free Block Volume allocation: **200 GB** including boot volume usage; Oracle documents around **47–50 GB** minimum/default boot volume depending on creation flow. Count all existing instances/volumes, backups and resource usage.
- Always Free Compute and volume eligibility depends on the tenancy **home region**. An eligible shape can still be temporarily unavailable with an "out of host capacity" error. Do not select a paid shape as fallback.
- Instances may be reclaimed due to Oracle's idle-use policy. Do not assume always-on availability or use synthetic busywork to evade reclamation. Provider design must handle loss of a host and recover against the canonical HIC ledger.
- A 30-day Free Trial and trial credit are **not** the same as Always Free eligibility. Do not make a paid resource accidentally look free because it is temporarily covered by trial credits. An account upgrade or additional paid resource requires a fresh explicit owner decision.
- OCI Budgets/alerts are **soft controls**, normally evaluated periodically, not a hard realtime cost cap. HIC itself must refuse unapproved chargeable provisioning/egress and record unknown billing as BLOCKED.
- OCI Ubuntu platform images include Arm/aarch64 variants; verify exact current image OCID, OS version and provenance at deployment. For Arm-based shapes use a compatible full Ubuntu image rather than assuming a particular Minimal image works. Verify .NET/Node/runtime multiarch container digests separately.

Official sources (consult current pages before provisioning):
- https://docs.oracle.com/en-us/iaas/Content/FreeTier/freetier_topic-Always_Free_Resources.htm
- https://docs.oracle.com/en-us/iaas/Content/FreeTier/freetier.htm
- https://docs.oracle.com/en-us/iaas/Content/Billing/Concepts/budgetsoverview.htm
- https://docs.oracle.com/en-us/iaas/Content/Compute/References/images.htm
- https://docs.oracle.com/en-us/iaas/Content/Compute/Tasks/connect-to-instance-private-subnet.htm
- https://docs.oracle.com/en-us/iaas/Content/Bastion/Concepts/bastionoverview.htm

**Source precedence:** The OCI English Always Free documentation is the research baseline; some localized/older pages describe previous A1 limits (for example 4 OCPU/24 GB). The live tenancy's eligibility, limits and console quote decide what the operator may provision. If contradictory, block and recheck rather than assuming the larger amount.

## Account-owner checklist (NO data to upload)

1. Create one personal OCI account with personal contact information, strong MFA, unique credentials, and careful home-region choice. No corporate SSO, employer email, work workspace or work GitHub token.
2. In Console, check subscription type, actual **Always Free eligible** shapes in home region, account resource quotas, billing profile and actual quoted monthly price before any Create action. Distinguish Free Trial credits from Always Free.
3. Set billing alerts/notifications and review compartment-level limits; remember alerts do not impose zero-charge hard enforcement. Do not upgrade to Pay As You Go or create billable resources without a new explicit approval.
4. Prefer a dedicated personal sandbox compartment and a least-privileged operator/admin workflow. Keep API tokens, private keys, SSH keys, tenancy OCIDs, IPs and billing details OFF public repositories and out of this chat.
5. Note only **sanitized** status for HIC: account active? home region code (if comfortable), shape eligible?, available A1 capacity?, proposed CPU/RAM/boot volume within free allowance?, MFA enabled? Never send payment card, token, signing key, console access link or sensitive screenshots.
6. Wait for a separate HIC provisioning plan and scoped authorization before creating the first VM and network/security resources.

## Example future single-host layout (not provisioned)

- **Candidate only:** 1 OCI Ampere A1.Flex VM with 2 OCPU, ~8 GiB RAM and an eligible ~50 GB boot disk. The unused free RAM is reserve, not a mandatory target; choose exact allowance only after confirming tenant eligibility. This is NOT a promise that A1 capacity will be available.
- ARM64 Ubuntu 24.04 LTS with pinned official image digest/OCID (the image identifier and provenance are unknown now). Rootless container engine setup must be reviewed for actual cgroup v2 CPU/memory/PID delegation and safe user/service configuration.
- Separate provider management plane and workload execution plane. No public HTTP execution endpoint. Prefer private subnet and narrowly authorized, time-limited Bastion administration where practical; Bastion itself requires network/IAM prerequisites. SSH/key management must remain outside sandbox.
- Sandbox *containers* should start with network disabled, no host sockets or credentials, read-only verified inputs and tightly bounded outputs. The host network management path must not be conflated with no-network sandbox egress.
- Strict ownership tags and per-attempt IDs for cleanup, independent image registry provenance and OS-level negative tests. Every instance/volume/route/backup needs an explicit cost/retention plan.

## OCI readiness gate matrix

| Gate | Current status | Required nonsecret evidence |
| --- | --- | --- |
| User-owned account/MFA | UNVERIFIED | Owner states account and MFA configured |
| Home region and free quota | UNVERIFIED | Console shows exact eligible A1 allowance and quota |
| A1 capacity | UNVERIFIED | Eligible shape shown and capacity confirmed at creation time |
| Image provenance/ARM compatibility | UNVERIFIED | OCI image OCID+publisher verified privately; digest and arch proof |
| Cost safety | UNVERIFIED | Cost estimate / eligible resources and denial of paid fallback confirmed |
| IAM / compartment / management transport | UNVERIFIED | Reviewed policy, no execution API public ingress |
| Verified cgroup/network/filesystem isolation | UNVERIFIED | Separate synthetic host/container probe with independent evidence |
| Ledger authority and task claim | BLOCKED | #102/#103 real issuance/claim/recovery gate |
| Real agent execution | BLOCKED | All previous gates + explicit model/tool/cost consent |

## No-capacity, cost or verification failure

If free A1 is unavailable or the region has no capacity, report BLOCKED and retry manually later or select another authorized provider. Do not sign up repeatedly, choose a paid shape silently, switch regions without reviewing free eligibility, or leak credentials. HIC provider selection UI should show **Unavailable — capacity**, **Not eligible for free tier**, **Security unverified**, or **Awaiting owner approval** as distinct reasons.

The current locally managed HIC development runtime remains independent; no reason to start/repair existing WSL/Podman, copy data from work device, or change existing Draft PRs for the account readiness phase.

## Console walkthrough after owner signup (2026-11-11)

The account owner reports creating an OCI account, but **this report is not independent proof of account tier, billing state, home region, A1 capacity, MFA, compartment, network or VM**. This walkthrough distinguishes **safe account preparation** from **provisioning**, which requires a separately scoped operator decision.

### Stage 0 — Read-only console checks, no Create

1. In OCI Console, inspect account/subscription **Free Trial vs Always Free** and verify MFA. The 30-day trial credits can temporarily cover *paid* services; only resources marked **Always Free Eligible** count for this plan.
2. In the Console region selector, confirm **home region**. Official OCI Free Tier says Always Free Compute must be provisioned in tenancy home region.
3. Open **Governance & Administration → Tenancy Management → Limits, Quotas and Usage** (menu labels can vary); inspect A1/Flex quota and current usage in that home region. Quota is not the same as real available host capacity. Official: https://docs.oracle.com/en-us/iaas/Content/FreeTier/freetier_topic-Always_Free_Resources.htm
4. Open **Compute → Instances → Create instance**, but **do not click final Create** yet. Check Ampere shape `VM.Standard.A1.Flex` labeled **Always Free Eligible**, Linux image compatible with `aarch64` (proposed Ubuntu 24.04), 2 OCPU, 8 GiB RAM, default 50 GB boot. At most 2 OCPU and 12 GB RAM across all A1 VMs on the free tenancy, and 200 GB shared home-region Block Volume (boot included); actual Console estimate and account limit win.
5. If any resource shows a nonzero monthly estimate, Paid, Pay As You Go, trial-credit-only eligibility, questionable storage/backup, unknown charges or ambiguous region: **STOP** and report only the class of problem, not account identifiers.

### Stage 1 — Account hygiene / organizing (owner operated)

6. In **Identity & Security → Compartments**, create a dedicated non-root compartment, suggested `hic-sandbox`, description `Isolated execution infrastructure for Human In Control`. A compartment is **resource/IAM organization**, not filesystem, account or network isolation by itself. Check that the owner's IAM principal can actually manage its resources. Official: https://docs.oracle.com/en-us/iaas/Content/Identity/compartments/To_create_a_compartment.htm
7. Set a small **notification budget** under **Billing & Cost Management → Budgets** for the HIC compartment (or root if needed), with actual-cost alerts at an intentionally small threshold and a receiving owner email. **Budget alerts are evaluated periodically and DO NOT stop charges.** Eligibility and quotas prevent unapproved paid resource use; do not assume a `$0` budget is a hard gate. Official: https://docs.oracle.com/en-us/iaas/Content/Billing/Concepts/budgetsoverview.htm
8. Review compartment quotas and existing resource counts before creating anything. Do not install OCI CLI, use privileged API keys or copy SSH keys from an employer-managed computer for this planning step.

### Stage 2 — Networking and access design (review, not provisioned)

9. Proposed network: a dedicated VCN and a **private subnet without public IP** for the execution host. Never expose an agent executor or HIC Control Plane inbound HTTP/SSH to the internet. A private subnet alone does **not** prove sandbox network egress is blocked.
10. **Preferred management:** OCI Bastion with source-IP restricted, time-limited SSH sessions into the private VM. Oracle lists OCI Bastion as free for Free Tier and paid accounts; IAM, subnet security rules and guest plugin availability must be configured and verified. Official: https://docs.oracle.com/en-us/iaas/Content/Compute/Tasks/connect-to-instance-private-subnet.htm and https://docs.oracle.com/en-us/iaas/Content/FreeTier/freetier_topic-Always_Free_Resources.htm
11. A VM with no public IP may need a separately controlled route (e.g. NAT gateway) for OS package updates. **Do not create or assume the price of network gateways or public IPs until account-specific estimate and egress policy are checked.** Administration traffic and sandbox task network policy are distinct.
12. If Bastion/private-subnet prerequisites or company-device network policy prevent access, pause. Do not silently open 0.0.0.0/0 inbound, enable unrestricted SSH, grant global IAM roles or weaken endpoint protections.

### Stage 3 — Proposed instance review, then separately approved Create

13. Instance name suggestion `hic-sandbox-host-01`, compartment `hic-sandbox`, Ubuntu 24.04 ARM64 official platform image (image OCID/provenance checked in actual region), `VM.Standard.A1.Flex`, **2 OCPU / 8 GiB**, **50 GB** boot, in the verified home region.
14. Provider management SSH **private** key is kept only on an approved personal/admin machine; upload only a public key or download a generated private key into approved private storage. Do not add any SSH key, OCI OCID, API credential, payment method, public IP, instance console URL or secret-bearing screenshot to repo/issues/chat.
15. Before pressing **Create instance**, review each listed shape, boot volume, image license, backups, network/public IP, quotas, cost estimate and Always Free labeling. A successful launch is still only a host, **not** a verified sandbox. Leave cloud instance provisioning and real synthetic probes outside this current documentation task.
16. Actual OCI instance created? Record only redacted evidence in a private operator checklist: account active, MFA yes/no, home region optionally, A1 eligible yes/no, image architecture, free quota, compartment present, no nonzero estimate, no public executor port. All live resource identities remain private.

### Stage 4 — Real sandbox later; NOT authorized by this runbook

When host access and ownership are verified under separate consent, provision an isolated rootless runtime with actual CPU/memory/PID controller delegation and no credentials/egress inside task sandboxes. Run only a disposable synthetic no-secret isolation probe under #108 **after** separate explicit operator authorization, and report every OS-level proof. No real model or repository write until signed ledger claim, artifact boundary, SDK entitlement and independent publish gates pass.

### Operator-readable handoff

Only ask the account owner for:
- Is OCI Console active and MFA enabled?
- What is the account's **home region** (region name only, optional)?
- Is `VM.Standard.A1.Flex` shown as **Always Free Eligible**?
- Does a proposed 2 OCPU / 8 GB / 50 GB configuration show **$0 estimated** in that account?
- Was `hic-sandbox` compartment created?

If any answer is not known, keep the corresponding gate **UNVERIFIED**; no cloud credentials, account IDs, payments or machine IPs are needed for planning.
