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
    'oci_core_vcn.hic', 'oci_core_security_list.deny_all',
    'oci_core_subnet.private', 'oci_core_instance.sandbox_host'
)
$data = [regex]::Matches($main, '(?m)^data\s+"([^"]+)"\s+"([^"]+)"\s*\{')
if ($data.Count -ne 1 -or $data[0].Groups[1].Value -ne 'oci_identity_compartments' -or
    $data[0].Groups[2].Value -ne 'hic') { throw 'Unexpected compartment data source.' }
$lookup = $main.Substring($data[0].Index, $resources[0].Index - $data[0].Index)
Assert-Match $lookup '(?m)^\s*count\s*=\s*var\.proposal_network\s*\?\s*1\s*:\s*0\s*$' 'Lookup is not gated by proposal_network.'
Assert-Match $lookup '(?m)^\s*compartment_id\s*=\s*var\.tenancy_ocid\s*$' 'Lookup is not scoped to the tenancy.'
Assert-Match $lookup '(?m)^\s*compartment_id_in_subtree\s*=\s*false\s*$' 'Lookup can include descendants.'
Assert-Match $lookup '(?m)^\s*name\s*=\s*"hic-sandbox"\s*$' 'Lookup name filter changed.'
Assert-Match $lookup '(?m)^\s*state\s*=\s*"ACTIVE"\s*$' 'Lookup state filter changed.'
Assert-Match $lookup '(?s)precondition\s*\{[^}]*owner_free_eligibility_checked\s*&&\s*var\.owner_billing_reviewed\s*&&\s*startswith\(var\.tenancy_ocid,\s*"ocid1\.tenancy\."\)' 'Lookup precondition changed.'
$postcondition = [regex]::Match($lookup, '(?ms)postcondition\s*\{\s*condition\s*=\s*([^\r\n]+)')
if (-not $postcondition.Success) { throw 'Compartment postcondition missing.' }
$predicate = $postcondition.Groups[1].Value.Trim()
Assert-Match $predicate 'length\(self\.compartments\)\s*==\s*1' 'Lookup must require exactly one result.'
Assert-Match $predicate 'alltrue\(\[for c in self\.compartments' 'Lookup must validate every result.'
Assert-Match $predicate 'c\.compartment_id\s*==\s*var\.tenancy_ocid' 'Lookup must check the parent.'
Assert-Match $predicate 'c\.name\s*==\s*"hic-sandbox"' 'Lookup must check the name.'
Assert-Match $predicate 'c\.state\s*==\s*"ACTIVE"' 'Lookup must check lifecycle.'
Assert-Match $predicate 'startswith\(c\.id,\s*"ocid1\.compartment\."\)' 'Lookup must check result identity.'
if ($resources.Count -ne $expected.Count) { throw 'Unexpected resource count in configuration.' }
for ($i = 0; $i -lt $resources.Count; $i++) {
    $match = $resources[$i]
    $name = "$($match.Groups[1].Value).$($match.Groups[2].Value)"
    if ($name -ne $expected[$i]) { throw "Unexpected resource: $name" }
    $end = if ($i + 1 -lt $resources.Count) { $resources[$i + 1].Index } else { $main.Length }
    $block = $main.Substring($match.Index, $end - $match.Index)
    $flag = if ($name -eq 'oci_core_instance.sandbox_host') { 'proposal_vm' } else { 'proposal_network' }
    Assert-Match $block "(?m)^\s*count\s*=\s*var\.$flag\s*\?\s*1\s*:\s*0\s*$" "Unsafe count: $name"
    Assert-Match $block '(?m)^\s*compartment_id\s*=.*one\(data\.oci_identity_compartments\.hic\[0\]\.compartments\)\.id' "Resource does not reference the read-only compartment: $name"
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
$root = '"ocid1.tenancy.synthetic"'
$valid = '{name="hic-sandbox",compartment_id="ocid1.tenancy.synthetic",state="ACTIVE",id="ocid1.compartment.synthetic"}'
$cases = @(
    @{ Name = 'valid'; Input = "[$valid]"; Expected = 'true' },
    @{ Name = 'missing'; Input = '[]'; Expected = 'false' },
    @{ Name = 'duplicate'; Input = "[$valid,$valid]"; Expected = 'false' },
    @{ Name = 'wrong parent'; Input = '[{name="hic-sandbox",compartment_id="ocid1.tenancy.other",state="ACTIVE",id="ocid1.compartment.synthetic"}]'; Expected = 'false' },
    @{ Name = 'inactive'; Input = '[{name="hic-sandbox",compartment_id="ocid1.tenancy.synthetic",state="INACTIVE",id="ocid1.compartment.synthetic"}]'; Expected = 'false' },
    @{ Name = 'wrong name'; Input = '[{name="other",compartment_id="ocid1.tenancy.synthetic",state="ACTIVE",id="ocid1.compartment.synthetic"}]'; Expected = 'false' },
    @{ Name = 'invalid id'; Input = '[{name="hic-sandbox",compartment_id="ocid1.tenancy.synthetic",state="ACTIVE",id=""}]'; Expected = 'false' }
)
foreach ($case in $cases) {
    $expression = $predicate.Replace('self.compartments', $case.Input).Replace('var.tenancy_ocid', $root)
    $result = $expression | & terraform console -no-color 2>&1
    if ($LASTEXITCODE -ne 0 -or ($result -join '').Trim() -ne $case.Expected) {
        throw "Compartment postcondition failed case $($case.Name): $result"
    }
}
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
    'oci_core_vcn\.hic.*->.*data\.oci_identity_compartments\.hic'
)) {
    Assert-Match $graph $edge "Missing dependency edge: $edge"
}
Write-Output 'PASS: synthetic compartment selection cases, static resources/counts, console defaults, network/IMDS settings, fmt, validate and dependency graph.'
Write-Output 'UNVERIFIED: authenticated zero-change plan, OCI resources, quotas, image, access and network enforcement.'
