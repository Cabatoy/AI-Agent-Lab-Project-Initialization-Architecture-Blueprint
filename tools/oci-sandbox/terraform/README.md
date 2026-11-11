# HIC OCI Terraform PoC — plan-only, all resources disabled

Issue #112; related #105/#107. **Not executed or validated in the user's Oracle tenancy. No apply approval.** OCI remains an optional provider; HIC's canonical task ledger/approval engine is not changed.

## Intended resources

- Optional `hic-sandbox` compartment below root; VCN `10.92.0.0/16`; private subnet `10.92.1.0/24`, no public address; explicit deny-all security list (no ingress or egress), no internet, NAT, service gateway, or public HIC endpoint.
- Optional Ubuntu 24.04 ARM64 `VM.Standard.A1.Flex`: **2 OCPU / 8 GiB**, **50 GiB** boot disk. Exact image and AD must be verified in the tenancy. Instance resource is `prevent_destroy` guarded but that is not an authorization or a cost guarantee.
- `proposal_network=false` and `proposal_vm=false` by default: plan should propose **zero resources**. Operator must never run `terraform apply` for this issue, even if an eventual `plan` is nonempty.

## Current verified account facts

The owner ran the read-only OCI preflight on 2026-11-11: home region `me-abudhabi-1`, 1 AD, A1.Flex shape **listed**, an Ubuntu 24.04 compatible image **listed**, and no existing `hic-sandbox` compartment. No OCI resource has been created. These reads do **not** verify remaining free quota, A1 host capacity, exact price/shape eligibility, image integrity, current account terms, cloud IAM, MFA, Bastion or real container sandbox enforcement.

## Before any nonempty terraform plan

1. Verify Oracle account-specific **Always Free Eligible** shape and billing; Free Trial credits may hide costs. Check existing A1 hours/GB-hours and 200 GB aggregate boot/block use. Account owner must inspect the cost estimate in the Console. If uncertain, STOP.
2. Verify `hic-sandbox` compartment has **not** been created by another process. Do not accidentally adopt/update existing IAM/resources.
3. Verify official Ubuntu ARM64 image OCID and publisher (not a mutable name); check an allowed **personal** SSH public key and a safe private administration path. Current deny-all network means the VM could be unreachable; OCI Bastion/IAM/rules require a **separate review** before launch. A virtual machine alone is not an isolated task sandbox.
4. Use only a personally signed-in Oracle Cloud Shell profile; credentials, CLI token, region/tenancy OCIDs and Terraform state are **private**. A security token is generally short-lived; verify how Cloud Shell authenticates. Do not copy any corporate SSH or cloud identity.
5. This proposal pins OCI provider version `9.11.0`; independently verify `terraform init` downloads a genuine provider and that it is compatible with Cloud Shell. Read its init/validate diagnostics locally; never use `curl | bash` or untrusted providers.
6. Never share `terraform.tfstate`, `*.tfplan`, `*.tfvars`, raw `terraform plan` output, OCIDs, SSH keys, billing or Console account screenshots to public issues/chat. Terraform plan/state may contain sensitive values.
7. No automated hard billing cap is implemented: budget alerts do not block spending. **The owner must deliberately review all cost items; a nonzero/unknown quote is BLOCKED.**

## Safe syntax/zero-provisioning examination

The following commands are **optional and only for a trusted Cloud Shell with a reviewed repo checkout**, not executed by ChatGPT:

~~~bash
cd tools/oci-sandbox/terraform
terraform fmt -check
terraform init -backend=false
terraform validate
# No account IDs are needed while both proposal flags remain false.
terraform plan -input=false -lock=false -no-color
~~~

Do not set `proposal_network=true` or `proposal_vm=true` or run `terraform apply` as part of this phase. Even when flags remain false, Terraform can access the provider and expose a local state file; keep your Cloud Shell workspace private. `-lock=false` is only for a fresh, disposable **empty** planning workspace; do not reuse for shared state. For any nonempty plan, separate approval is required with reviewed account-specific quote and exact changes.

**Do not use `terraform destroy` for cleanup**: the account may contain unrelated resources or the VM's lifecycle has destruction protection. Exact ownership and rollback procedures must be reviewed separately.

## Security/functional gaps

This plan **does not** configure Bastion, SSH access, operating system updates, OCI IAM policies or quotas, network egress, persistent logs, image integrity, spend enforcement, OCI task executor, rootless cgroup controllers, artifact staging, lease/revocation, provider attestation, or an agent. All of these are separate review gates (#108, #67, #102/#103). Local managed Windows/Podman/WSL runtime is untouched.

## Sources

- https://docs.oracle.com/en-us/iaas/Content/FreeTier/freetier_topic-Always_Free_Resources.htm
- https://docs.oracle.com/en-us/iaas/Content/dev/terraform/configuring.htm
- https://docs.oracle.com/en-us/iaas/tools/terraform-provider-oci/latest/docs/r/core_instance.html
- https://docs.oracle.com/en-us/iaas/tools/terraform-provider-oci/latest/docs/r/core_security_list.html
- https://registry.terraform.io/providers/oracle/oci
