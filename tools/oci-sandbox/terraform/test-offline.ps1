$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if ((Test-Path 'terraform.tfstate') -or (Test-Path 'terraform.tfstate.d') -or
    (Get-ChildItem -File -ErrorAction Stop | Where-Object {
        $_.Name -match '^terraform\.tfvars(\.json)?$|\.auto\.tfvars(\.json)?$'
    })) {
    throw 'Offline checks require a fresh directory without local state or automatic tfvars.'
}

function Assert-Match([string]$text, [string]$pattern, [string]$message) {
    if ($text -notmatch $pattern) { throw $message }
}

function Assert-NoMatch([string]$text, [string]$pattern, [string]$message) {
    if ($text -match $pattern) { throw $message }
}

function Run-Terraform([string[]]$arguments) {
    $result = & terraform @arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "terraform $($arguments -join ' ') failed: $result" }
    return ($result -join "`n")
}

$main = (Get-Content 'main.tf' -Raw) -replace '(?m)#.*$', ''
$variables = Get-Content 'variables.tf' -Raw
$versions = Get-Content 'versions.tf' -Raw
$resources = [regex]::Matches($main, '(?m)^resource\s+"([^"]+)"\s+"([^"]+)"\s*\{')
$expected = @(
    'oci_identity_compartment.hic', 'oci_core_vcn.hic',
    'oci_core_security_list.deny_all', 'oci_core_subnet.private',
    'oci_core_instance.sandbox_host'
)
if ($resources.Count -ne $expected.Count) { throw 'Unexpected resource count in configuration.' }
for ($i = 0; $i -lt $resources.Count; $i++) {
    $match = $resources[$i]
    $name = "$($match.Groups[1].Value).$($match.Groups[2].Value)"
    if ($name -ne $expected[$i]) { throw "Unexpected resource: $name" }
    $end = if ($i + 1 -lt $resources.Count) { $resources[$i + 1].Index } else { $main.Length }
    $block = $main.Substring($match.Index, $end - $match.Index)
    $flag = if ($name -eq 'oci_core_instance.sandbox_host') { 'proposal_vm' } else { 'proposal_network' }
    Assert-Match $block "(?m)^\s*count\s*=\s*var\.$flag\s*\?\s*1\s*:\s*0\s*$" "Unsafe count: $name"
    if ($name -eq 'oci_core_security_list.deny_all') {
        Assert-NoMatch $block '(?m)^\s*(ingress_security_rules|egress_security_rules)\s*[{=]' 'Security list contains rules.'
    }
}
foreach ($flag in @('proposal_network', 'proposal_vm')) {
    Assert-Match $variables "(?s)variable\s+`"$flag`"\s*\{[^}]*\bdefault\s*=\s*false\b" "Unsafe default: $flag"
}
Assert-Match $main '(?m)^\s*prohibit_public_ip_on_vnic\s*=\s*true\s*$' 'Subnet public IP is not prohibited.'
Assert-Match $main '(?m)^\s*assign_public_ip\s*=\s*false\s*$' 'Instance public IP is not disabled.'
Assert-Match $main '(?m)^\s*security_list_ids\s*=\s*\[oci_core_security_list\.deny_all\[0\]\.id\]\s*$' 'Custom security list is not attached.'
Assert-Match $main '(?m)^\s*are_legacy_imds_endpoints_disabled\s*=\s*true\s*$' 'IMDSv1 is not disabled.'
Assert-NoMatch $main '(?m)^\s*(nsg_ids|route_table_id)\s*=' 'Unexpected additional network policy.'
Assert-Match $versions 'version\s*=\s*"=\s*9\.11\.0"' 'OCI provider pin changed.'

$tfFiles = @(Get-ChildItem -File -Filter '*.tf' | ForEach-Object { $_.Name } | Sort-Object)
if (($tfFiles -join ',') -ne 'main.tf,variables.tf,versions.tf') {
    throw 'Unexpected Terraform files could introduce additional resources.'
}

Run-Terraform @('fmt', '-check', '-no-color') | Out-Null
Run-Terraform @('validate', '-no-color') | Out-Null
$console = 'jsonencode({network=var.proposal_network,vm=var.proposal_vm,network_count=var.proposal_network ? 1 : 0,vm_count=var.proposal_vm ? 1 : 0})' |
    & terraform console -no-color 2>&1
if ($LASTEXITCODE -ne 0) { throw "terraform console failed: $console" }
$values = ($console | ConvertFrom-Json) | ConvertFrom-Json
if ($values.network -ne $false -or $values.vm -ne $false -or
    $values.network_count -ne 0 -or $values.vm_count -ne 0) {
    throw 'Effective default flags or count expressions are not zero.'
}
$graph = Run-Terraform @('graph', '-type=plan')
foreach ($edge in @(
    'oci_core_instance\.sandbox_host.*->.*oci_core_subnet\.private',
    'oci_core_subnet\.private.*->.*oci_core_security_list\.deny_all',
    'oci_core_security_list\.deny_all.*->.*oci_core_vcn\.hic',
    'oci_core_vcn\.hic.*->.*oci_identity_compartment\.hic'
)) {
    Assert-Match $graph $edge "Missing dependency edge: $edge"
}
Write-Output 'PASS: static resources/counts, console defaults, network/IMDS settings, fmt, validate and dependency graph.'
Write-Output 'UNVERIFIED: authenticated zero-change plan, OCI resources, quotas, image, access and network enforcement.'
