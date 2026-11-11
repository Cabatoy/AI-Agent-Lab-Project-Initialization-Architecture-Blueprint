# HIC OCI Terraform PoC — plan-only, all resources disabled

Issue #112; related #105/#107. **Not executed or validated in the user's Oracle tenancy. No apply approval.** OCI remains an optional provider; HIC's canonical task ledger/approval engine is not changed.

## Intended resources

- The existing `hic-sandbox` root-child compartment is referenced by a read-only data source, not created, imported, updated or deleted by this configuration. Optional VCN `10.92.0.0/16`; private subnet `10.92.1.0/24`, no public address; a custom security list with no configured ingress or egress rules, no internet, NAT, service gateway, or public HIC endpoint. This is a configuration intent, not measured network isolation.
- Optional Ubuntu 24.04 ARM64 `VM.Standard.A1.Flex`: **2 OCPU / 8 GiB**, **50 GiB** boot disk. Exact image and AD must be verified in the tenancy. Instance resource is `prevent_destroy` guarded but that is not an authorization or a cost guarantee.
- `proposal_network=false` and `proposal_vm=false` by default: static count checks indicate **zero resources for a fresh empty state**. An authenticated Terraform plan has not been run, and existing state could cause changes even with both flags off. Operator must never run `terraform apply` for this issue, even if an eventual `plan` is nonempty.
- The proposed instance disables legacy IMDS endpoints with `instance_options.are_legacy_imds_endpoints_disabled = true`. This requires IMDSv2, but **does not block IMDSv2**, prove instance-principal isolation, or prevent sandbox access to `169.254.169.254`. Independent sandbox-level denial and negative tests remain BLOCKED.

## Current verified account facts

The owner reported a read-only OCI preflight on 2026-11-11: home region `me-abudhabi-1`, 1 AD, A1.Flex shape **listed** and an Ubuntu 24.04 compatible image **listed**. A later owner-supplied Resource Explorer view showed an **ACTIVE root-child `hic-sandbox` compartment**, superseding the earlier absence observation. Its identity and current state have not been independently queried in this validation. The view does not establish that a VM, VCN or subnet exists. These observations do **not** verify remaining free quota, A1 host capacity, exact price/shape eligibility, image integrity, current account terms, cloud IAM, MFA, Bastion or real container sandbox enforcement.

## Before any nonempty terraform plan

1. Verify Oracle account-specific **Always Free Eligible** shape and billing; Free Trial credits may hide costs. Check existing A1 OCPU-hours/GB-hours, A1 tenancy limits and aggregate boot/block use against the current official terms and account status. The proposed 50 GiB is not proof of remaining allowance; the account owner must inspect every cost item and quota in the Console. If uncertain, STOP; do not fall back to another region or paid shape.
2. Verify the existing `hic-sandbox` is the intended ACTIVE direct child of the specified tenancy root and that the operator has appropriate read-only inspection rights. When `proposal_network=true`, the data source lists only direct children with exact name and ACTIVE filters; a postcondition rejects zero, multiple, wrong-parent, wrong-name, inactive or invalid-ID results. Insufficient permissions, missing or ambiguous results are BLOCKED; visibility may depend on IAM. This is not permission to import, adopt, update or delete the compartment. If any earlier Terraform state already manages this compartment, **stop**: removing its declaration can propose state-driven changes; do not apply, import or modify state without separate review.
3. Verify the exact regional official Ubuntu 24.04 ARM64 image OCID, publisher, A1 compatibility and provenance (not a mutable name); the prefix check and `image_provenance_reviewed` flag do not perform this verification. Check an allowed **personal** SSH public key and a safe private administration path. Current deny-all network means the VM is unreachable through Bastion; the management-review flag adds no access. OCI Bastion/IAM/rules require a **separate review** before launch. A virtual machine alone is not an isolated task sandbox.
4. Use only a personally signed-in Oracle Cloud Shell profile; credentials, CLI token, region/tenancy OCIDs and Terraform state are **private**. A security token is generally short-lived; verify how Cloud Shell authenticates. Do not copy any corporate SSH or cloud identity.
5. This proposal pins OCI provider version `9.11.0`; independently verify `terraform init` downloads a genuine provider and that it is compatible with Cloud Shell. The local `.terraform.lock.hcl` is ignored, so provider version pinning alone does not provide a reviewed, cross-platform checksum lock. Its provenance and reproducibility remain a separate review gate. Read init/validate diagnostics locally; never use `curl | bash` or untrusted providers.
6. Never share `terraform.tfstate`, `*.tfplan`, `*.tfvars`, raw `terraform plan` output, OCIDs, SSH keys, billing or Console account screenshots to public issues/chat. Terraform plan/state may contain sensitive values even when inputs are marked `sensitive`; `.gitignore` is not encryption or access control. Use a private, access-controlled and encrypted state/plan location with backups, locking and a separately reviewed recovery procedure before any future nonempty plan. No remote backend or state security is configured here.
7. No automated hard billing cap is implemented: budget alerts do not block spending. **The owner must deliberately review all cost items; a nonzero/unknown quote is BLOCKED.**

## Safe syntax/zero-provisioning examination

The following commands are **optional and only for a trusted Cloud Shell with a reviewed repo checkout**, not executed by ChatGPT:

~~~bash
cd tools/oci-sandbox/terraform
terraform fmt -check
terraform init -backend=false
terraform validate
# A plan may still require provider authentication, even with both flags false.
terraform plan -input=false -no-color
~~~

Do not set `proposal_network=true` or `proposal_vm=true` or run `terraform apply` as part of this phase. With `proposal_network=false` the compartment data source has `count=0`, so it performs no compartment lookup. Even with both flags false, Terraform plan may initialize/authenticate the provider, refresh existing state and expose local state; keep your Cloud Shell workspace private. Do not disable locking. A zero-change result is only expected in a fresh empty state and is not an authorization or a cost guarantee. For any nonempty plan, separate approval is required with reviewed account-specific quote and exact changes.

**Do not use `terraform destroy` for cleanup**: the account may contain unrelated resources or the VM's lifecycle has destruction protection. Exact ownership and rollback procedures must be reviewed separately.

## Security/functional gaps

This plan **does not** configure Bastion, SSH access, operating system updates, OCI IAM policies or quotas, network egress, persistent logs, image integrity, spend enforcement, OCI task executor, rootless cgroup controllers, artifact staging, lease/revocation, provider attestation, or an agent. All of these are separate review gates (#108, #67, #102/#103). Local managed Windows/Podman/WSL runtime is untouched.

**Private management: BLOCKED.** The current security list has no ingress and no
Bastion exists. A future separate design must review Bastion identity/IAM,
client CIDR restrictions, session type and cost; allow only the Bastion private
endpoint `/32` to the target TCP/22 (plus the return path required by the chosen
stateful/stateless rules) and verify host SSH settings. No such rule or service
is provisioned here. OCI security lists and NSGs are additive, and VCN defaults
must also be reviewed; absence of a public IP alone is not a security proof.

**Network and metadata: UNVERIFIED.** Oracle states that security rules are not
enforced for `169.254.0.0/16`, including instance metadata. Security lists do
not establish guest namespace isolation; independently verify DNS, link-local
metadata and sandbox egress. IMDSv2 requires an
authorization header, not a secret, and is not a substitute for blocking the
untrusted sandbox from metadata. Account quota, image identity, Bastion reachability,
actual network enforcement and Terraform state protections remain unverified.

## Offline regression checks

After `terraform init -backend=false`, run
`powershell -NoProfile -File ./test-offline.ps1` on Windows, or
`pwsh -NoProfile -File ./test-offline.ps1` where PowerShell is available.
This checks Terraform formatting/validation, static default/count declarations
and dependency edges, plus evaluates effective default flags and the actual
compartment postcondition against synthetic valid, missing, duplicate,
wrong-parent, inactive, wrong-name and invalid-ID inputs in Terraform console,
without OCI credentials, an authenticated plan or cloud API calls. It
requires a fresh directory with no local state or automatically loaded tfvars.
It does not prove a zero-change plan, live isolation, quota or cost.

## Sources

- https://docs.oracle.com/en-us/iaas/Content/FreeTier/freetier_topic-Always_Free_Resources.htm
- https://docs.oracle.com/en-us/iaas/Content/dev/terraform/configuring.htm
- https://docs.oracle.com/en-us/iaas/tools/terraform-provider-oci/latest/docs/r/core_instance.html
- https://docs.oracle.com/en-us/iaas/tools/terraform-provider-oci/latest/docs/r/core_security_list.html
- https://docs.oracle.com/en-us/iaas/tools/terraform-provider-oci/latest/docs/d/identity_compartments.html
- https://docs.oracle.com/en-us/iaas/Content/Compute/Tasks/gettingmetadata.htm
- https://docs.oracle.com/en-us/iaas/Content/Bastion/Tasks/connectingtosessions.htm
- https://docs.oracle.com/en-us/iaas/Content/Network/Concepts/securityrules.htm
- https://registry.terraform.io/providers/oracle/oci
