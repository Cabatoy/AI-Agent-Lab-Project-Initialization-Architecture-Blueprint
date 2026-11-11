# HIC Oracle Cloud Shell preflight (read-only)

Parent: #105. Work item: #107. This is an optional **OCI adapter readiness** script and is not a deployed execution provider or sandbox. It does **not** replace #108's future synthetic OS isolation probe.

## Why Cloud Shell

OCI Console's Cloud Shell provides the OCI CLI, `jq` and, per Oracle docs, Terraform. You can use your signed-in personal OCI account without installing Oracle CLI, Hyper-V or SSH credentials on a corporate-managed Windows PC. OCI Cloud Shell is free within its monthly usage limits. Still follow your company's acceptable-use and browser policies.

## Run (only after reviewing the file)

1. Sign in to your **personal** Oracle Cloud Console.
2. Open **Cloud Shell** from the Console Developer Tools / terminal icon; use the tenancy **home region** when possible.
3. Open `tools/oci-sandbox/preflight.sh` in this repository and copy it into Cloud Shell as `hic-oci-preflight.sh`. Review its source before executing. **Do not run an unreviewed downloaded script from the internet.**
4. Execute `bash hic-oci-preflight.sh`. Every read-only OCI query prints a `Checking OCI: ...` progress line to show where it is waiting; each query has a 60-second timeout and automatic OCI retries are disabled. If an earlier version prints only the first three introductory lines, use `Ctrl+C` and **redownload the current branch version** before rerunning. No cloud resources are changed. Do not provide secrets, IDs, payment information or CLI debug logs to a public GitHub issue or to ChatGPT.
5. Share only these redacted output lines: `Home region`, `Availability domains visible`, `Ampere A1.Flex`, `Ubuntu 24.04 image`, `hic-sandbox compartment`. The script intentionally prints no OCIDs.
6. **Do not press Create instance yet.** The **Console estimate** and shape's Always Free label must be independently checked; A1 listed does not prove current host capacity.

This script is designed to be idempotent: all OCI calls use `list` or `get`; none create or alter Oracle resources. On API/auth errors it stops with `BLOCKED`. It cannot verify billing tier, remaining quota, regional capacity, resource prices, MFA, Bastion connectivity or sandbox isolation. The `--shape` image listing only shows image compatibility, not an immutable image digest or actual successful instance launch.

After preflight and account-specific billing/free-tier verification, a **separate reviewed Terraform plan** can propose the compartment, private subnet, VCN and A1 VM. `terraform plan` previews proposed changes; `terraform apply` mutates cloud resources and requires a **distinct explicit approval** after reviewing costs, dependencies, IAM and network reachability. Terraform state can expose sensitive IDs and must not be committed or posted publicly. For now **no Terraform apply scripts are included**.

## What will eventually be automated

- Compartment `hic-sandbox` and scoped IAM/limits.
- Isolated VCN, private subnet and no public executor address. OCI Bastion may provide restricted administrative access after IAM/security review.
- ARM64 Ubuntu 24.04 platform image and `VM.Standard.A1.Flex` sizing (candidate 2 OCPU / 8 GiB RAM / 50 GB boot within account-specific verified Always Free allowances).
- Resource tags, exact-owned cleanup, per-resource free eligibility, budget and unknown-cost refusal.
- Independent cgroup v2, filesystem, secrets, metadata/DNS/egress, cancellation and output security probes before a real agent task.

## Official sources

- [OCI Cloud Shell](https://docs.oracle.com/en-us/iaas/Content/API/Concepts/cloudshellintro.htm)
- [OCI Always Free resources](https://docs.oracle.com/en-us/iaas/Content/FreeTier/freetier_topic-Always_Free_Resources.htm)
- [OCI CLI compute shape list](https://docs.oracle.com/en-us/iaas/tools/oci-cli/latest/oci_cli_docs/cmdref/compute/shape/list.html)
- [OCI CLI compute image list](https://docs.oracle.com/en-us/iaas/tools/oci-cli/latest/oci_cli_docs/cmdref/compute/image/list.html)

No model, SDK, Cloud/Windows VM, task ledger, GitHub publisher, Control Plane/Worker, live data or production namespace is touched by this preflight.
