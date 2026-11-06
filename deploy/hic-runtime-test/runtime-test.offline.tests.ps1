$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
$harness = Join-Path $PSScriptRoot 'runtime-test.ps1'
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($harness, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -ne 0) { throw 'Harness PowerShell parse failed.' }
# Load only function declarations, never the runtime entry point.
foreach ($node in $ast.EndBlock.Statements) {
    if ($node -is [System.Management.Automation.Language.FunctionDefinitionAst]) {
        . ([scriptblock]::Create($node.Extent.Text))
    }
}
$root = $PSScriptRoot
$sourcePath = [IO.Path]::GetFullPath((Join-Path $root '..\..\agent-lab\control-plane\Program.cs'))
$migrationPath = Join-Path (Split-Path $sourcePath) 'Migrations\002-execution-ownership.sql'
$versionPath = Join-Path (Split-Path $sourcePath) 'ExecutionSchema.cs'
$templatePath = Join-Path $root 'compose.json'
$emptyEnv = Join-Path $root 'empty.env'
$template = Get-Content -LiteralPath $templatePath -Raw | ConvertFrom-Json
$services = @('postgres', 'redis', 'model-stub', 'control-plane', 'agent-worker')
$passed = 0

function Test-Case([string]$Name, [scriptblock]$Body) {
    & $Body
    $script:passed++
    Write-Host "PASS $Name"
}
function Expect-Error([scriptblock]$Body, [string]$Message) {
    $caught = $null
    try { & $Body } catch { $caught = $_.Exception.Message }
    Assert-True ($null -ne $caught -and $caught.Contains($Message)) "Expected rejection: $Message"
}
function New-TestState([string]$Stage) {
    return [pscustomobject]@{
        mode = 'r2-staged'; stage = $Stage; taskId = $null
        sourceHash = $schema.SourceHash; migrationHash = $schema.MigrationHash
        dependencyIdentity = 'owned'
    }
}
function With-SourceText([string]$Text, [scriptblock]$Body) {
    $path = Join-Path ([IO.Path]::GetTempPath()) ('hic-schema-source-' + [guid]::NewGuid().ToString('N') + '.cs')
    try {
        [IO.File]::WriteAllText($path, $Text)
        & $Body $path
    } finally {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    }
}

Test-Case 'Static configuration and authoritative base extraction' {
    Validate-Configuration
    Assert-True ($schema.RequiresV2 -and $schema.BaseSql.Contains('create table if not exists execution_plans')) 'Base schema not extracted.'
    Assert-True ($schema.MigrationSql -eq (Get-Content -LiteralPath $migrationPath -Raw)) 'Migration must remain verbatim.'
}
Test-Case 'Legacy start rejects R2 before Podman access' {
    Expect-Error { Assert-LegacyStart } 'start-dependencies -> prepare-schema -> start-api'
    $old = $schema
    try { $schema = [pscustomobject]@{ RequiresV2 = $false }; Assert-LegacyStart } finally { $schema = $old }
}
Test-Case 'Missing migration rejected' {
    Expect-Error { Get-SchemaBundle $sourcePath ($migrationPath + '.missing') $versionPath } 'migration/version source missing'
}
Test-Case 'Unsupported initializer rejected without schema inference' {
    Expect-Error { Get-SchemaBundle $versionPath $migrationPath $versionPath } 'Base initializer must be unique'
}
Test-Case 'Initializer logic change rejected instead of extracting partial SQL' {
    $code = Get-Content -LiteralPath $sourcePath -Raw
    $code = $code.Replace('await connection.ExecuteAsync(@"', 'Console.WriteLine("changed"); await connection.ExecuteAsync(@"')
    With-SourceText $code {
        param($path)
        Expect-Error { Get-SchemaBundle $path $migrationPath $versionPath } 'Unsupported base initializer shape'
    }
}
Test-Case 'R1 source without v2 gate still extracts base and permits legacy workflow' {
    $code = (Get-Content -LiteralPath $sourcePath -Raw).Replace('await ExecutionSchema.VerifyAsync(postgresConnectionString);', '')
    With-SourceText $code {
        param($path)
        $schema = Get-SchemaBundle $path ($migrationPath + '.missing') ($versionPath + '.missing')
        Assert-LegacyStart
        Assert-True (-not $schema.RequiresV2 -and $schema.MigrationSql -eq '') 'R1 incorrectly requires migration.'
    }
}
Test-Case 'Incorrect v2 source version rejected' {
    $code = (Get-Content -LiteralPath $versionPath -Raw).Replace('internal const int Version = 2;', 'internal const int Version = 3;')
    With-SourceText $code {
        param($path)
        Expect-Error { Get-SchemaBundle $sourcePath $migrationPath $path } 'Unsupported schema version contract'
    }
    Test-Case 'Unrecognized version gate cannot silently become legacy startup' {
        $code = (Get-Content -LiteralPath $sourcePath -Raw).Replace(
            'await ExecutionSchema.VerifyAsync(postgresConnectionString);',
            'await ExecutionSchema.VerifyAsync( postgresConnectionString );')
        With-SourceText $code {
            param($path)
            Expect-Error { Get-SchemaBundle $path $migrationPath $versionPath } 'Unsupported schema version gate ordering'
        }
    }
}
foreach ($stage in @('dependencies-starting', 'schema-preparing', 'api-starting', 'api-ready', 'invalid')) {
    Test-Case "Interrupted/wrong stage $stage rejected" {
        Expect-Error { Assert-Stage (New-TestState $stage) 'schema-ready' } 'Expected staged state'
    }
}
Test-Case 'Missing/legacy/incomplete state rejected' {
    Expect-Error { Assert-Stage $null 'schema-ready' } 'Expected staged state'
    Expect-Error { Assert-Stage ([pscustomobject]@{ stage = 'schema-ready' }) 'schema-ready' } 'Expected staged state'
    Expect-Error { Assert-Stage ([pscustomobject]@{ mode = 'r2-staged'; stage = 'schema-ready' }) 'schema-ready' } 'Schema sources changed'
}
Test-Case 'Source and migration changes rejected' {
    foreach ($field in @('sourceHash', 'migrationHash')) {
        $bad = New-TestState 'schema-ready'
        $bad.$field = 'changed'
        Expect-Error { Assert-Stage $bad 'schema-ready' } 'Schema sources changed'
    }
}
Test-Case 'Task-bearing state cannot be reinitialized' {
    $bad = New-TestState 'schema-ready'
    $bad.taskId = [guid]::NewGuid().ToString()
    Expect-Error { Assert-Stage $bad 'schema-ready' } 'task-bearing'
}
Test-Case 'Cloud endpoint and real model key rejected' {
    $original = $template.services.'agent-worker'.environment.MODEL_ENDPOINT
    try {
        $template.services.'agent-worker'.environment.MODEL_ENDPOINT = 'https://invalid.example'
        Expect-Error { Validate-Configuration } 'Only the local stub'
    } finally { $template.services.'agent-worker'.environment.MODEL_ENDPOINT = $original }
    try {
        $template.services.'agent-worker'.environment.MODEL_API_KEY = 'not-a-real-key'
        Expect-Error { Validate-Configuration } 'Only the local stub'
    } finally { $template.services.'agent-worker'.environment.MODEL_API_KEY = '' }
}
Test-Case 'External network and non-loopback API rejected' {
    $template.networks.test.internal = $false
    try { Expect-Error { Validate-Configuration } 'must be internal' } finally { $template.networks.test.internal = $true }
    $original = $template.services.'control-plane'.ports
    try {
        $template.services.'control-plane'.ports = @('0.0.0.0:18080:8080')
        Expect-Error { Validate-Configuration } 'must be loopback'
    } finally { $template.services.'control-plane'.ports = $original }
}
Test-Case 'Application database cannot point outside the isolated project' {
    $original = $template.services.'control-plane'.environment.ConnectionStrings__Postgres
    try {
        $template.services.'control-plane'.environment.ConnectionStrings__Postgres = 'Host=outside;Database=hic_test'
        Expect-Error { Validate-Configuration } 'API database must be the dedicated'
    } finally { $template.services.'control-plane'.environment.ConnectionStrings__Postgres = $original }
}
Test-Case 'Fresh dependencies action never starts applications' {
    $node = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Start-Dependencies' }, $true)
    Assert-True ($node.Extent.Text.Contains("'--no-deps', 'postgres', 'redis', 'model-stub'") -and
        -not $node.Extent.Text.Contains("'control-plane'") -and
        -not $node.Extent.Text.Contains("'agent-worker'") -and
        $node.Extent.Text.Contains("'dependencies-starting'") -and
        $node.Extent.Text.Contains('fresh project/state directory')) 'Unsafe dependency startup.'
}
Test-Case 'Authorization and legacy guard precede native entry point' {
    $text = $ast.Extent.Text
    $entry = $text.Substring($text.IndexOf("`nValidate-Configuration"))
    Assert-True ($entry.IndexOf('Assert-LegacyStart') -lt $entry.IndexOf("Invoke-Native @('info'") -and
        $entry.IndexOf('$ApproveRuntime.IsPresent') -lt $entry.IndexOf("Invoke-Native @('info'")) 'Missing early authorization/version gate.'
    Assert-True ($entry.Contains("'start-dependencies', 'prepare-schema', 'start-api', 'persistence'")) 'New mutations missing approval gate.'
}
Test-Case 'Actual resource identity guard refuses replacement' {
    $state = New-TestState 'schema-ready'
    function Get-Resources { }
    function Get-DependencyIdentity { return 'owned' }
    Assert-DependencyIdentity
    function Get-DependencyIdentity { return 'replaced' }
    Expect-Error { Assert-DependencyIdentity } 'Dependency resource identity changed'
}
Test-Case 'Actual database identity and base-table guards reject mismatches' {
    function Invoke-Sql { return 'hic_test|hic_test|hic_test' }
    Assert-DatabaseIdentity
    function Invoke-Sql { return 'wrong|hic_test|hic_test' }
    Expect-Error { Assert-DatabaseIdentity } 'Unexpected dedicated database'
    function Invoke-Sql { return '3' }
    Assert-BaseSchema
    function Invoke-Sql { return '2' }
    Expect-Error { Assert-BaseSchema } 'base tables not present'
}
Test-Case 'Actual schema-v2 verifier rejects missing version or constraints' {
    function Assert-BaseSchema { }
    function Invoke-Sql { return 'schema-v2-ok' }
    Assert-SchemaV2
    function Invoke-Sql { return 'invalid' }
    Expect-Error { Assert-SchemaV2 } 'Schema v2 version/columns/constraints/index'
}
Test-Case 'Application guard rejects even stopped containers' {
    $containers = @{}
    Assert-NoApplications
    $containers = @{ 'control-plane' = [pscustomobject]@{ State = [pscustomobject]@{ Running = $false } } }
    Expect-Error { Assert-NoApplications } 'even stopped'
}

# All remaining IO is mocked; accidental Podman invocation fails the test.
function Invoke-Native { throw 'Forbidden native runtime invocation in offline tests.' }
$events = [System.Collections.Generic.List[string]]::new()
$sqlFailure = 0
$sqlCalls = 0
$unexpectedTables = $false
$badIdentity = $false
$badSchema = $false
function Save-State { $events.Add("state:$($state.stage)") }
function Assert-DependencyIdentity {
    $events.Add('ownership')
    Assert-True (-not $badIdentity) 'Dependency resource identity changed'
}
function Assert-NoApplications {
    $events.Add('no-apps')
    Assert-True (-not $appsPresent) 'Schema preparation forbids application containers'
}
function Assert-DatabaseIdentity { $events.Add('database-identity') }
function Invoke-Sql {
    param([string]$Sql)
    if ($Sql.Contains('pg_tables')) { if ($unexpectedTables) { return '1' } else { return '0' } }
    throw 'Unexpected SQL in offline orchestration test.'
}
function Invoke-SchemaTransaction {
    param([string]$Sql)
    $script:sqlCalls++
    if ($Sql -ceq $schema.BaseSql) { $events.Add('base-transaction') }
    elseif ($Sql -ceq $schema.MigrationSql) { $events.Add('v2-transaction') }
    else { throw 'Non-authoritative SQL submitted.' }
    if ($sqlCalls -eq $sqlFailure) { throw 'Unknown SQL outcome' }
}
function Assert-BaseSchema { $events.Add('base-verified') }
function Assert-SchemaV2 {
    $events.Add('v2-verified')
    Assert-True (-not $badSchema) 'Schema v2 verification failed'
}
function Invoke-Compose {
    param([string[]]$Arguments)
    $events.Add('compose:' + ($Arguments -join ' '))
}
function Assert-Readiness { $events.Add('api-health') }
$appsPresent = $false
Test-Case 'Prepare schema preserves transaction and state ordering' {
    $state = New-TestState 'dependencies-ready'
    $events.Clear()
    Prepare-Schema
    Assert-True (($events -join ',') -eq 'ownership,no-apps,database-identity,state:schema-preparing,base-transaction,base-verified,v2-transaction,v2-verified,state:schema-ready') 'Wrong schema sequence.'
}
Test-Case 'Start API gates schema before build and never starts Worker' {
    $state = New-TestState 'schema-ready'
    $events.Clear()
    Start-PreparedApi
    Assert-True (($events -join ',') -eq 'ownership,no-apps,database-identity,v2-verified,state:api-starting,compose:build control-plane agent-worker,compose:up -d --no-build --no-deps control-plane,api-health,state:api-ready') 'Wrong API sequence.'
}
foreach ($failure in @(1, 2)) {
    Test-Case "Unknown SQL outcome at transaction $failure cannot retry" {
        $state = New-TestState 'dependencies-ready'
        $script:sqlCalls = 0
        $script:sqlFailure = $failure
        Expect-Error { Prepare-Schema } 'Unknown SQL outcome'
        Assert-True ($state.stage -eq 'schema-preparing') 'Lost ambiguity guard.'
        Expect-Error { Prepare-Schema } 'Expected staged state'
        $script:sqlFailure = 0
    }
}
Test-Case 'Existing database tables block SQL' {
    $state = New-TestState 'dependencies-ready'
    $unexpectedTables = $true
    try { Expect-Error { Prepare-Schema } 'new empty database' } finally { $unexpectedTables = $false }
    Assert-True ($state.stage -eq 'dependencies-ready') 'State mutated before preconditions.'
}
Test-Case 'Existing applications or changed resources block schema and startup' {
    foreach ($startup in @($false, $true)) {
        $state = New-TestState $(if ($startup) { 'schema-ready' } else { 'dependencies-ready' })
        $appsPresent = $true
        try {
            Expect-Error { if ($startup) { Start-PreparedApi } else { Prepare-Schema } } 'forbids application'
        } finally { $appsPresent = $false }
        $badIdentity = $true
        try {
            Expect-Error { if ($startup) { Start-PreparedApi } else { Prepare-Schema } } 'identity changed'
        } finally { $badIdentity = $false }
    }
}
Test-Case 'Invalid v2 blocks API build/start' {
    $state = New-TestState 'schema-ready'
    $events.Clear()
    $badSchema = $true
    try { Expect-Error { Start-PreparedApi } 'Schema v2 verification failed' } finally { $badSchema = $false }
    Assert-True (@($events | Where-Object { $_ -like 'compose:*' }).Count -eq 0) 'Application started before schema verification.'
}
Test-Case 'Unknown API startup outcome prevents automatic retry' {
    $state = New-TestState 'schema-ready'
    function Assert-Readiness { throw 'Unknown API startup outcome' }
    Expect-Error { Start-PreparedApi } 'Unknown API startup outcome'
    Assert-True ($state.stage -eq 'api-starting') 'Lost API ambiguity guard.'
    Expect-Error { Start-PreparedApi } 'Expected staged state'
}
Test-Case 'Schema psql transport is one transaction and stops on error' {
    $node = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Invoke-SchemaTransaction' }, $true)
    Assert-True ($node.Extent.Text.Contains("'--single-transaction'") -and
        $node.Extent.Text.Contains("'ON_ERROR_STOP=1'") -and $node.Extent.Text.Contains('-InputText $Sql')) 'Unsafe SQL transport.'
}
Write-Host "Offline harness tests: $passed passed; 0 runtime operations."
