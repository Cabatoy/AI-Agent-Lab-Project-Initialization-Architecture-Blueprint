#!/usr/bin/env bash
# HIC OCI Always Free feasibility — READ ONLY / fail closed.
# No create/update/delete/launch/terraform apply; no OCI IDs, credentials or raw API responses printed.
# Run in OCI Console -> Cloud Shell as a signed-in personal account operator.
set -Eeuo pipefail
umask 077

say() { printf '%s\n' "$*"; }
die() { say "BLOCKED: $*" >&2; exit 2; }
for exe in oci jq timeout; do command -v "$exe" >/dev/null 2>&1 || die "Missing $exe in OCI Cloud Shell"; done

# Strictly allowlist the only OCI subcommands used in this script. Never echo raw OCI responses.
oci_read() {
  local label="$1"; shift
  local body
  say "Checking OCI: $label ..." >&2
  if ! body="$(timeout 60s oci "$@" --no-retry --connection-timeout 10 --read-timeout 30 --output json 2>/dev/null)"; then
    die "Cannot read $label (OCI permission/API/network issue or 60s timeout). No resources modified."
  fi
  if ! jq -e . >/dev/null 2>&1 <<<"$body"; then die "Malformed OCI JSON for $label"; fi
  printf '%s' "$body"
}

say "HIC Oracle Cloud — preflight (read-only)"
say "No tenancy IDs, private keys, IP addresses, billing details, or credentials will be printed."
say "No Oracle resources are created, changed, or removed."
say ""

roots="$(oci_read 'root tenancy listing' iam compartment list --include-root --all)"
root="$(jq -r '[.data[] | select((.id // "") | startswith("ocid1.tenancy.")) | .id] | unique | if length == 1 then .[0] else empty end' <<<"$roots")"
[[ "$root" == ocid1.tenancy.* ]] || die "Could not uniquely identify the tenancy root from a read-only compartment listing"

tenancy="$(oci_read 'home region key' iam tenancy get --tenancy-id "$root")"
home_key="$(jq -r '.data."home-region-key" // empty' <<<"$tenancy")"
[[ -n "$home_key" ]] || die "Home region key unavailable"

regions="$(oci_read 'region map' iam region list --all)"
home="$(jq -r --arg k "$home_key" '[.data[] | select(.key == $k) | .name] | if length == 1 then .[0] else empty end' <<<"$regions")"
[[ -n "$home" ]] || die "Home region name unavailable; cannot proceed"
say "Home region: $home"

ads="$(oci_read 'availability domains' iam availability-domain list --compartment-id "$root" --region "$home" --all)"
ad_count="$(jq -r '.data | length' <<<"$ads")"
[[ "$ad_count" =~ ^[0-9]+$ ]] || die "Invalid availability-domain response"
say "Availability domains visible: $ad_count"
(( ad_count > 0 )) || die "No availability domains visible in home region"

shapes="$(oci_read 'Ampere A1 shape listing' compute shape list --compartment-id "$root" --shape 'VM.Standard.A1.Flex' --region "$home" --all)"
a1_count="$(jq -r '[.data[] | select(.shape == "VM.Standard.A1.Flex")] | length' <<<"$shapes")"
if (( a1_count > 0 )); then
  say "Ampere A1.Flex: SHAPE LISTED (NOT capacity or free-tier eligibility proof)"
else
  say "Ampere A1.Flex: NOT LISTED — BLOCKED"
fi

images="$(oci_read 'Ubuntu 24.04 ARM-compatible image listing' compute image list --compartment-id "$root" --operating-system 'Canonical Ubuntu' --operating-system-version '24.04' --shape 'VM.Standard.A1.Flex' --region "$home" --all)"
image_count="$(jq -r '[.data[] | select(."lifecycle-state" == "AVAILABLE" and (."operating-system-version" == "24.04"))] | length' <<<"$images")"
if (( image_count > 0 )); then
  say "Ubuntu 24.04 image for A1.Flex: IMAGE(S) LISTED (publisher/digest NOT attested)"
else
  say "Ubuntu 24.04 image for A1.Flex: NOT LISTED — BLOCKED"
fi

compartments="$(oci_read 'hic-sandbox compartment lookup' iam compartment list --compartment-id "$root" --name 'hic-sandbox' --region "$home" --all)"
comp_count="$(jq -r '[.data[] | select(.name == "hic-sandbox" and ."lifecycle-state" == "ACTIVE")] | length' <<<"$compartments")"
if (( comp_count == 1 )); then
  say "hic-sandbox root child compartment: EXISTS"
elif (( comp_count == 0 )); then
  say "hic-sandbox root child compartment: NOT CREATED (this preflight will NOT create it)"
else
  say "hic-sandbox root child compartment: AMBIGUOUS — investigate before provisioning"
fi

say ""
say "Still UNVERIFIED: tenancy Always Free tier/remaining monthly quota; actual A1 host capacity;"
say "MFA, image provenance, exact per-resource cost estimate, boot storage, private networking,"
say "OCI Bastion/IAM, OS cgroup isolation, per-task sandbox, and any live execution."
say "Next: review OCI Console's Always Free eligibility and cost estimate before provisioning."

if (( a1_count == 0 || image_count == 0 || comp_count > 1 )); then
  die "One or more prerequisites unavailable — no changes made"
fi
say "Preflight completed: READ-ONLY checks succeeded; NOT authorization to create a VM."
