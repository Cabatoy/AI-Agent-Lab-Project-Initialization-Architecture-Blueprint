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
    'oci_core_subnet.private', 'oci_bastion_bastion.management',
    'oci_core_network_security_group.ssh_target',
    'oci_core_network_security_group_security_rule.bastion_ssh',
    'oci_core_instance.sandbox_host', 'oci_identity_policy.bastion_operator'
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
    $flag = if ($name -eq 'oci_core_instance.sandbox_host') { 'proposal_vm' } elseif ($name -eq 'oci_identity_policy.bastion_operator') { 'proposal_bastion_iam' } elseif ($name -match 'bastion|ssh_target') { 'proposal_bastion' } else { 'proposal_network' }
    Assert-Match $block "(?m)^\s*count\s*=\s*var\.$flag\s*\?\s*1\s*:\s*0\s*$" "Unsafe count: $name"
    if ($name -ne 'oci_core_network_security_group_security_rule.bastion_ssh') {
        Assert-Match $block '(?m)^\s*compartment_id\s*=.*one\(data\.oci_identity_compartments\.hic\[0\]\.compartments\)\.id' "Resource does not reference the read-only compartment: $name"
    }
    if ($name -eq 'oci_core_security_list.deny_all') {
        Assert-NoMatch $block '(?m)^\s*(ingress_security_rules|egress_security_rules)\s*[{=]' 'Security list contains rules.'
    }
}
foreach ($flag in @('proposal_network', 'proposal_vm', 'proposal_bastion', 'proposal_bastion_iam')) {
    Assert-Match $variables "(?s)variable\s+`"$flag`"\s*\{[^}]*\bdefault\s*=\s*false\b" "Unsafe default: $flag"
}
Assert-Match $main '(?m)^\s*prohibit_public_ip_on_vnic\s*=\s*true\s*$' 'Subnet public IP is not prohibited.'
Assert-Match $main '(?m)^\s*assign_public_ip\s*=\s*false\s*$' 'Instance public IP is not disabled.'
Assert-Match $main '(?m)^\s*assign_ipv6ip\s*=\s*false\s*$' 'Instance IPv6 is not disabled.'
Assert-Match $main '(?m)^\s*is_ipv6enabled\s*=\s*false\s*$' 'VCN IPv6 is not disabled.'
Assert-Match $main '(?m)^\s*security_list_ids\s*=\s*\[oci_core_security_list\.deny_all\[0\]\.id\]\s*$' 'Custom security list is not attached.'
Assert-Match $main '(?m)^\s*are_legacy_imds_endpoints_disabled\s*=\s*true\s*$' 'IMDSv1 is not disabled.'
Assert-NoMatch $main '(?m)^\s*route_table_id\s*=' 'Unexpected route table.'
Assert-Match $main '(?m)^\s*nsg_ids\s*=\s*var\.proposal_bastion\s*\?\s*\[oci_core_network_security_group\.ssh_target\[0\]\.id\]\s*:\s*\[\]\s*$' 'Unexpected VM NSG.'
Assert-Match $main '(?m)^\s*source\s*=\s*"\$\{oci_bastion_bastion\.management\[0\]\.private_endpoint_ip_address\}/32"\s*$' 'SSH source must be Bastion endpoint /32.'
Assert-Match $main '(?m)^\s*direction\s*=\s*"INGRESS"\s*$' 'SSH rule must be ingress.'
Assert-Match $main '(?m)^\s*protocol\s*=\s*"6"\s*$' 'SSH rule must be TCP.'
Assert-Match $main '(?s)tcp_options\s*\{\s*destination_port_range\s*\{\s*min\s*=\s*22\s*max\s*=\s*22\s*\}' 'SSH rule must be TCP/22 only.'
Assert-Match $main '(?m)^\s*stateless\s*=\s*false\s*$' 'SSH response traffic must be stateful.'
Assert-Match $main '(?m)^\s*max_session_ttl_in_seconds\s*=\s*1800\s*$' 'Session TTL changed.'
Assert-Match $main '(?m)^\s*dns_proxy_status\s*=\s*"DISABLED"\s*$' 'Bastion proxy must be disabled.'
Assert-Match $main '(?m)^\s*client_cidr_block_allow_list\s*=\s*\[var\.bastion_client_cidr\]\s*$' 'Client allowlist must contain the reviewed single source.'
Assert-Match $main '(?s)precondition\s*\{[^}]*var\.proposal_network\s*&&\s*var\.proposal_bastion\s*&&\s*var\.owner_free_eligibility_checked' 'VM must require network and Bastion.'
Assert-Match $main '(?s)precondition\s*\{[^}]*var\.proposal_network\s*&&\s*var\.owner_private_management_reviewed\s*&&\s*var\.owner_free_eligibility_checked\s*&&\s*var\.owner_billing_reviewed\s*&&\s*local\.bastion_client_cidr_valid' 'Bastion review gates missing.'
Assert-Match $main '(?s)precondition\s*\{[^}]*var\.proposal_network\s*&&\s*var\.proposal_bastion\s*&&\s*var\.proposal_vm\s*&&\s*var\.owner_private_management_reviewed\s*&&\s*local\.bastion_group_valid' 'IAM review gates missing.'
Assert-Match $main 'target\.bastion\.ocid' 'IAM bastion scope missing.'
Assert-Match $main 'target\.resource\.ocid' 'IAM target scope missing.'
Assert-NoMatch $main '(?i)manage\s+(bastion-family|virtual-network-family)|in tenancy' 'Broad IAM permissions are not permitted.'
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
$cidrPredicate = [regex]::Match($main, '(?m)^\s*bastion_client_cidr_valid\s*=\s*([^\r\n]+)').Groups[1].Value.Trim()
if (-not $cidrPredicate) { throw 'Bastion client CIDR validation missing.' }
$groupPredicate = [regex]::Match($main, '(?m)^\s*bastion_group_valid\s*=\s*([^\r\n]+)').Groups[1].Value.Trim()
if (-not $groupPredicate) { throw 'Bastion group validation missing.' }
$cidrCases = @(
    @{ Input = '203.0.113.5/32'; Expected = 'true' },
    @{ Input = ''; Expected = 'false' },
    @{ Input = '0.0.0.0/0'; Expected = 'false' },
    @{ Input = '0.0.0.0/32'; Expected = 'false' },
    @{ Input = '203.0.113.0/24'; Expected = 'false' },
    @{ Input = '999.0.0.1/32'; Expected = 'false' },
    @{ Input = '2001:db8::1/128'; Expected = 'false' }
)
$groupCases = @(
    @{ Input = 'ocid1.group.synthetic'; Expected = 'true' },
    @{ Input = ''; Expected = 'false' },
    @{ Input = "ocid1.group.fake' or true"; Expected = 'false' },
    @{ Input = 'ocid1.user.synthetic'; Expected = 'false' }
)
$expressions = @()
foreach ($case in $cases) {
    $expressions += '(' + $predicate.Replace('self.compartments', $case.Input).Replace('var.tenancy_ocid', $root) + ')'
}
foreach ($case in $cidrCases) {
    $expressions += '(' + $cidrPredicate.Replace('var.bastion_client_cidr', "`"$($case.Input)`"") + ')'
}
foreach ($case in $groupCases) {
    $expressions += '(' + $groupPredicate.Replace('var.bastion_operator_group_ocid', "`"$($case.Input)`"") + ')'
}
$result = ('jsonencode([' + ($expressions -join ',') + '])') | & terraform console -no-color 2>&1
if ($LASTEXITCODE -ne 0) { throw "Synthetic validation failed: $result" }
$json = $result | ConvertFrom-Json
$results = $json | ConvertFrom-Json
$allCases = @($cases) + @($cidrCases) + @($groupCases)
if ($results.Count -ne $allCases.Count) { throw "Synthetic result count mismatch: $($results.Count) instead of $($allCases.Count); result $result" }
for ($i = 0; $i -lt $results.Count; $i++) {
    if ($results[$i].ToString().ToLowerInvariant() -ne $allCases[$i].Expected) {
        throw "Synthetic validation failed case $i."
    }
}
$console = 'jsonencode({network=var.proposal_network,vm=var.proposal_vm,bastion=var.proposal_bastion,iam=var.proposal_bastion_iam,network_count=var.proposal_network ? 1 : 0,vm_count=var.proposal_vm ? 1 : 0,bastion_count=var.proposal_bastion ? 1 : 0,iam_count=var.proposal_bastion_iam ? 1 : 0})' |
    & terraform console -no-color 2>&1
if ($LASTEXITCODE -ne 0) { throw "terraform console failed: $console" }
$values = ($console | ConvertFrom-Json) | ConvertFrom-Json
if ($values.network -ne $false -or $values.vm -ne $false -or $values.bastion -ne $false -or $values.iam -ne $false -or
    $values.network_count -ne 0 -or $values.vm_count -ne 0 -or $values.bastion_count -ne 0 -or $values.iam_count -ne 0) {
    throw 'Effective default flags or count expressions are not zero.'
}
$graph = Run-Terraform @('graph', '-type=plan')
foreach ($edge in @(
    'oci_core_instance\.sandbox_host.*->.*oci_core_network_security_group_security_rule\.bastion_ssh',
    'oci_core_subnet\.private.*->.*oci_core_security_list\.deny_all',
    'oci_core_security_list\.deny_all.*->.*oci_core_vcn\.hic',
    'oci_core_vcn\.hic.*->.*data\.oci_identity_compartments\.hic'
    'oci_core_network_security_group_security_rule\.bastion_ssh.*->.*oci_bastion_bastion\.management'
    'oci_bastion_bastion\.management.*->.*oci_core_subnet\.private'
    'oci_identity_policy\.bastion_operator.*->.*oci_core_instance\.sandbox_host'
)) {
    Assert-Match $graph $edge "Missing dependency edge: $edge"
}
Write-Output 'PASS: synthetic compartment, CIDR and group cases; static resource gates, network/IMDS, fmt, validate and dependency graph.'
Write-Output 'UNVERIFIED: authenticated zero-change plan, OCI resources, quotas, image, access and network enforcement.'
