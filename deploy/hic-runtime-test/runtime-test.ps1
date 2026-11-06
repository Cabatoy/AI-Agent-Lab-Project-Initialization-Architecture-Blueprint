[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('validate', 'preflight', 'start', 'start-dependencies', 'prepare-schema', 'start-api', 'readiness', 'persistence', 'worker', 'stop')]
    [string]$Action,
    [ValidatePattern('^hic-net10-[a-f0-9]{12}$')]
    [string]$Project,
    [ValidateRange(1024, 65535)]
    [int]$ApiPort = 18080,
    [switch]$ApproveRuntime
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
$root = $PSScriptRoot
$templatePath = Join-Path $root 'compose.json'
$emptyEnv = Join-Path $root 'empty.env'
$template = Get-Content -LiteralPath $templatePath -Raw | ConvertFrom-Json
$services = @('postgres', 'redis', 'model-stub', 'control-plane', 'agent-worker')
$sourcePath = [IO.Path]::GetFullPath((Join-Path $root '..\..\agent-lab\control-plane\Program.cs'))
$migrationPath = Join-Path (Split-Path $sourcePath) 'Migrations\002-execution-ownership.sql'
$versionPath = Join-Path (Split-Path $sourcePath) 'ExecutionSchema.cs'

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Get-SchemaBundle {
    param([string]$Source, [string]$Migration, [string]$Version)
    $code = Get-Content -LiteralPath $Source -Raw
    $signature = 'static async Task InitializeSchemaAsync(string postgresConnectionString)'
    Assert-True ([regex]::Matches($code, [regex]::Escape($signature)).Count -eq 1) 'Base initializer must be unique.'
    $pattern = '(?s)' + [regex]::Escape($signature) +
        '\s*\{\s*await using var connection = new NpgsqlConnection\(postgresConnectionString\);\s*await connection\.ExecuteAsync\(@"(?<sql>(?:[^"]|"")*)"\);\s*\}'
    $match = [regex]::Match($code, $pattern)
    Assert-True $match.Success 'Unsupported base initializer shape; do not infer or duplicate schema.'
    $sql = $match.Groups['sql'].Value.Replace('""', '"')
    foreach ($table in @('tasks', 'task_status_transitions', 'execution_plans')) {
        Assert-True ($sql -match "create table if not exists $table\s*\(") 'Required authoritative base table missing.'
    }
    $requiresV2 = $code -match '\bExecutionSchema\b'
    if ($requiresV2) {
        Assert-True ($code -match '(?s)await ExecutionSchema\.VerifyAsync\(postgresConnectionString\);\s*await InitializeSchemaAsync\(postgresConnectionString\);') 'Unsupported schema version gate ordering.'
        Assert-True ((Test-Path -LiteralPath $Migration) -and (Test-Path -LiteralPath $Version)) 'R2 migration/version source missing.'
        $versionCode = Get-Content -LiteralPath $Version -Raw
        Assert-True ($versionCode -match 'internal const int Version = 2;' -and
            $versionCode.Contains('where version = @Version')) 'Unsupported schema version contract.'
        $migrationSql = Get-Content -LiteralPath $Migration -Raw
        Assert-True ($migrationSql -match 'insert into app_schema_versions\(version\) values \(2\)') 'Unexpected migration version.'
    } else {
        $migrationSql = ''
    }
    return [pscustomobject]@{
        BaseSql = $sql
        MigrationSql = $migrationSql
        RequiresV2 = $requiresV2
        SourceHash = (Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash
        MigrationHash = if ($requiresV2) { (Get-FileHash -LiteralPath $Migration -Algorithm SHA256).Hash } else { '' }
    }
}

function Assert-Stage {
    param($State, [string]$Expected)
    Assert-True ($null -ne $State -and (Get-Property $State 'mode') -eq 'r2-staged' -and
        (Get-Property $State 'stage') -eq $Expected) "Expected staged state '$Expected'; incomplete/unknown outcome requires inspection and a fresh project, not retry."
    Assert-True ((Get-Property $State 'sourceHash') -eq $schema.SourceHash -and
        (Get-Property $State 'migrationHash') -eq $schema.MigrationHash) 'Schema sources changed since dependency startup; use a fresh project.'
    Assert-True ($State.PSObject.Properties.Name -contains 'taskId') 'Incomplete staged task state.'
    Assert-True ($null -eq (Get-Property $State 'taskId')) 'Schema/startup actions cannot reuse a task-bearing project.'
}

function Assert-LegacyStart {
    Assert-True (-not $schema.RequiresV2) 'R2 requires start-dependencies -> prepare-schema -> start-api; legacy start did not run.'
}

function Validate-Configuration {
    $script:schema = Get-SchemaBundle $sourcePath $migrationPath $versionPath
    Assert-True (@(Get-Content -LiteralPath $emptyEnv | Where-Object { $_ -match '^\s*[^#\s]' }).Count -eq 0) 'Environment file must be empty.'
    foreach ($service in $template.services.PSObject.Properties) {
        $value = $service.Value
        Assert-True (-not ($value.PSObject.Properties.Name -contains 'container_name')) 'Fixed container names are forbidden.'
        Assert-True (-not ($value.PSObject.Properties.Name -contains 'env_file')) 'Service environment files are forbidden.'
        Assert-True ($value.labels.'org.hic.runtime-test' -eq 'v1') 'Missing ownership label.'
        Assert-True (@($value.networks).Count -eq 1 -and $value.networks[0] -eq 'test') 'Unexpected service network.'
        if ($value.PSObject.Properties.Name -contains 'build') {
            $context = [IO.Path]::GetFullPath((Join-Path $root $value.build.context))
            Assert-True (Test-Path -LiteralPath (Join-Path $context $value.build.dockerfile)) 'Missing build context or Dockerfile.'
        }
        if ($service.Name -ne 'control-plane') {
            Assert-True (-not ($value.PSObject.Properties.Name -contains 'ports')) 'Only the API may publish a port.'
        }
    }
    Assert-True ($template.services.'control-plane'.ports[0] -eq '127.0.0.1:18080:8080') 'API binding must be loopback.'
    Assert-True ($template.networks.test.internal -eq $true) 'Test network must be internal.'
    Assert-True ($template.networks.test.PSObject.Properties.Name -notcontains 'external' -and $template.networks.test.PSObject.Properties.Name -notcontains 'name') 'External or explicit network names are forbidden.'
    foreach ($volume in $template.volumes.PSObject.Properties) {
        Assert-True ($volume.Value.PSObject.Properties.Name -notcontains 'external') 'External volumes are forbidden.'
        Assert-True ($volume.Value.PSObject.Properties.Name -notcontains 'name') 'Explicit volume names are forbidden.'
    }
    $worker = $template.services.'agent-worker'.environment
    Assert-True ($worker.MODEL_ENDPOINT -eq 'http://model-stub:8000' -and $worker.MODEL_API_KEY -eq '') 'Only the local stub is permitted.'
    Assert-True ($worker.TASK_QUEUE_KEY -eq 'agentlab:task-queue') 'Queue must match the fixed Control Plane key.'
    Assert-True ($worker.ConnectionStrings__Redis -eq $template.services.'control-plane'.environment.ConnectionStrings__Redis) 'Redis endpoints differ.'
    Assert-True ($worker.ConnectionStrings__Redis -eq 'redis:6379' -and
        $worker.CONTROL_PLANE_URL -eq 'http://control-plane:8080' -and
        $worker.MODEL_PROVIDER -eq 'openai-compatible' -and
        $worker.MODEL_NAME -eq 'hic-deterministic-stub') 'Only isolated service endpoints and the deterministic model are permitted.'
    $postgres = $template.services.postgres.environment
    Assert-True ($postgres.POSTGRES_USER -eq 'hic_test' -and $postgres.POSTGRES_DB -eq 'hic_test' -and
        $postgres.POSTGRES_PASSWORD -eq 'synthetic-hic-test-only') 'Only synthetic test database settings are permitted.'
    Assert-True ($template.services.'control-plane'.environment.ConnectionStrings__Postgres -eq
        "Host=postgres;Port=5432;Database=hic_test;Username=hic_test;Password=$($postgres.POSTGRES_PASSWORD)") 'API database must be the dedicated PostgreSQL service.'
    Assert-True (-not ((Get-Content -LiteralPath $templatePath -Raw).Contains('${'))) 'Environment interpolation is forbidden.'
    Write-Host 'Static configuration validation passed.'
}

function Invoke-Native {
    param([string[]]$Arguments, [string]$InputText)
    $ErrorActionPreference = 'Continue'
    $PSNativeCommandUseErrorActionPreference = $false
    try {
        if ($PSBoundParameters.ContainsKey('InputText')) { $output = @($InputText | & podman @Arguments 2>&1) }
        else { $output = @(& podman @Arguments 2>&1) }
    }
    catch { throw 'Podman invocation failed; raw output withheld.' }
    if ($LASTEXITCODE -ne 0) { throw "Podman operation failed (exit $LASTEXITCODE); raw output withheld." }
    return ($output -join "`n")
}

function Invoke-Compose {
    param([string[]]$Arguments)
    $null = Invoke-Native (@('compose', '--env-file', $emptyEnv, '-p', $Project, '-f', $configPath) + $Arguments)
}

function Get-Property {
    param($Object, [string]$Name)
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function ConvertFrom-InspectJson {
    param([string]$Json, [string[]]$RequiredProperties)
    Assert-True (-not [string]::IsNullOrWhiteSpace($Json)) 'Empty inspect response.'
    try { $parsed = ConvertFrom-Json -InputObject $Json -ErrorAction Stop }
    catch { throw 'Invalid inspect JSON; raw output withheld.' }
    if ($parsed -is [array]) {
        Assert-True ($parsed.Count -eq 1) 'Inspect must return exactly one resource.'
        $item = $parsed[0]
    } else {
        $item = $parsed
    }
    Assert-True ($item -is [System.Management.Automation.PSCustomObject]) 'Inspect resource must be a JSON object.'
    foreach ($name in $RequiredProperties) {
        $property = $item.PSObject.Properties[$name]
        Assert-True ($null -ne $property) "Inspect resource is missing required field: $name"
        Assert-True ($null -ne $property.Value) "Inspect resource has null required field: $name"
    }
    return $item
}

function Assert-Labels {
    param($Labels)
    Assert-True ((Get-Property $Labels 'org.hic.runtime-test') -eq 'v1') 'Resource ownership marker mismatch.'
    Assert-True ((Get-Property $Labels 'com.docker.compose.project') -eq $Project) 'Resource Compose project mismatch.'
}

function Get-Resources {
    $script:containers = @{}
    $all = Invoke-Native @('ps', '-a', '--format', '{{.ID}}|{{.Names}}')
    foreach ($line in ($all -split "`n" | Where-Object { $_.Trim() })) {
        $parts = $line -split '\|', 2
        if ($parts[1] -like "$Project-*" -or $parts[1] -like "${Project}_*") {
            $candidate = ConvertFrom-InspectJson (Invoke-Native @('inspect', $parts[0])) @('Config')
            Assert-Labels (Get-Property $candidate.Config 'Labels')
        }
    }
    $ids = Invoke-Native @('ps', '-a', '--filter', "label=com.docker.compose.project=$Project", '--format', '{{.ID}}')
    if ($ids.Trim()) {
        Assert-True (Test-Path -LiteralPath $statePath) 'Existing resources without local ownership state; refusing adoption.'
        foreach ($id in ($ids -split "`n" | Where-Object { $_.Trim() })) {
            $item = ConvertFrom-InspectJson (Invoke-Native @('inspect', $id.Trim())) @('Id', 'Config', 'HostConfig', 'Mounts', 'NetworkSettings', 'State')
            $labels = Get-Property $item.Config 'Labels'
            Assert-Labels $labels
            $service = Get-Property $labels 'com.docker.compose.service'
            Assert-True ($services -contains $service -and -not $containers.ContainsKey($service)) 'Unknown or duplicate service.'
            Assert-True ((Get-Property $item.State 'Running') -is [bool]) 'Missing or invalid container running state.'
            $expectedService = Get-Property $template.services $service
            $expectedEnvironment = Get-Property $expectedService 'environment'
            if ($null -ne $expectedEnvironment) {
                foreach ($entry in $expectedEnvironment.PSObject.Properties) {
                    Assert-True ((Get-Property $item.Config 'Env') -contains "$($entry.Name)=$($entry.Value)") 'Runtime environment differs from isolated settings.'
                }
            }
            if ($service -ne 'control-plane') {
                $ports = Get-Property $item.HostConfig 'PortBindings'
                Assert-True ($null -eq $ports -or @($ports.PSObject.Properties).Count -eq 0) 'Unexpected published service port.'
            }
            Assert-True ($item.Mounts -is [array]) 'Container mounts must be a JSON array.'
            foreach ($mount in $item.Mounts) {
                $expectedVolume = if ($service -eq 'postgres') { "${Project}_postgres-data" } elseif ($service -eq 'redis') { "${Project}_redis-data" } else { '' }
                Assert-True ((Get-Property $mount 'Type') -eq 'volume' -and (Get-Property $mount 'Name') -eq $expectedVolume) 'Unexpected container mount.'
            }
            $attachedNetworks = Get-Property $item.NetworkSettings 'Networks'
            Assert-True ($attachedNetworks -is [System.Management.Automation.PSCustomObject]) 'Missing or invalid container networks.'
            $networks = @($attachedNetworks.PSObject.Properties | ForEach-Object { $_.Name })
            Assert-True ($networks.Count -eq 1 -and $networks[0] -eq "${Project}_test") 'Unexpected attached network.'
            $containers[$service] = $item
        }
    }
    $volumes = Invoke-Native @('volume', 'ls', '--format', '{{.Name}}')
    foreach ($name in ($volumes -split "`n" | Where-Object { $_.Trim() })) {
        if ($name -notlike "${Project}_*" -and $name -notlike "$Project-*") { continue }
        Assert-True (Test-Path -LiteralPath $statePath) 'Existing volume without local ownership state.'
        Assert-True ($name -in @("${Project}_postgres-data", "${Project}_redis-data")) 'Unexpected project volume.'
        $volume = ConvertFrom-InspectJson (Invoke-Native @('volume', 'inspect', $name)) @('Labels')
        Assert-Labels $volume.Labels
    }
    $networks = Invoke-Native @('network', 'ls', '--format', '{{.Name}}')
    foreach ($name in ($networks -split "`n" | Where-Object { $_.Trim() })) {
        if ($name -notlike "${Project}_*" -and $name -notlike "$Project-*") { continue }
        Assert-True (Test-Path -LiteralPath $statePath) 'Existing network without local ownership state.'
        Assert-True ($name -eq "${Project}_test") 'Unexpected project network.'
        $network = ConvertFrom-InspectJson (Invoke-Native @('network', 'inspect', $name)) @('labels', 'internal')
        Assert-Labels $network.labels
        Assert-True ($network.internal -eq $true) 'Runtime network is not internal.'
    }
}

function Get-ContainerId {
    param([string]$Service)
    Assert-True ($containers.ContainsKey($Service) -and $containers[$Service].State.Running) "Required test service not running: $Service"
    return $containers[$Service].Id
}

function Assert-ApiBinding {
    $null = Get-ContainerId 'control-plane'
    $bindings = @(Get-Property $containers['control-plane'].HostConfig.PortBindings '8080/tcp')
    Assert-True ($bindings.Count -eq 1 -and $bindings[0].HostIp -eq '127.0.0.1' -and $bindings[0].HostPort -eq "$ApiPort") 'Runtime API binding mismatch.'
}

function Invoke-Api {
    param([string]$Path, [string]$Method = 'Get', $Body = $null, [int]$ExpectedStatus = 200)
    Assert-ApiBinding
    $parameters = @{ Uri = "http://127.0.0.1:$ApiPort$Path"; Method = $Method; TimeoutSec = 5; UseBasicParsing = $true }
    if ($null -ne $Body) {
        $parameters.ContentType = 'application/json'
        $parameters.Body = $Body | ConvertTo-Json -Compress
    }
    try { $response = Invoke-WebRequest @parameters }
    catch { throw "Isolated API request failed: $Method $Path (response withheld)." }
    Assert-True ([int]$response.StatusCode -eq $ExpectedStatus) 'Unexpected HTTP status.'
    return ($response.Content | ConvertFrom-Json)
}

function Invoke-Sql {
    param([string]$Sql)
    return (Invoke-Native @('exec', (Get-ContainerId 'postgres'), 'psql', '-U', 'hic_test', '-d', 'hic_test', '-v', 'ON_ERROR_STOP=1', '-At', '-c', $Sql)).Trim()
}

function Assert-NoApplications {
    Assert-True (-not $containers.ContainsKey('control-plane') -and -not $containers.ContainsKey('agent-worker')) 'Schema preparation forbids even stopped application containers.'
}

function Get-DependencyIdentity {
    $ids = @()
    foreach ($service in @('postgres', 'redis', 'model-stub')) {
        $ids += Get-ContainerId $service
        if ($service -in @('postgres', 'redis')) {
            $mounts = @($containers[$service].Mounts)
            Assert-True ($mounts.Count -eq 1) 'Exactly one dedicated data volume is required.'
            $volume = ConvertFrom-InspectJson (Invoke-Native @('volume', 'inspect', $mounts[0].Name)) @('Name', 'CreatedAt', 'Mountpoint', 'Labels')
            Assert-Labels $volume.Labels
            $ids += "$($volume.Name)|$($volume.CreatedAt)|$($volume.Mountpoint)"
        }
    }
    $network = ConvertFrom-InspectJson (Invoke-Native @('network', 'inspect', "${Project}_test")) @('id', 'labels', 'internal')
    Assert-Labels $network.labels
    Assert-True $network.internal 'Dependency network must be internal.'
    $ids += $network.id
    return ($ids -join ';')
}

function Assert-DependencyIdentity {
    Get-Resources
    Assert-True ((Get-Property $state 'dependencyIdentity') -eq (Get-DependencyIdentity)) 'Dependency resource identity changed; refusing schema/API startup.'
}

function Assert-DatabaseIdentity {
    $identity = Invoke-Sql "SELECT current_database() || '|' || current_user || '|' || pg_get_userbyid(datdba) FROM pg_database WHERE datname=current_database();"
    Assert-True ($identity -eq 'hic_test|hic_test|hic_test') 'Unexpected dedicated database/role/owner.'
}

function Assert-BaseSchema {
    $result = Invoke-Sql @'
SELECT count(*) FROM information_schema.tables
WHERE table_schema='public' AND table_type='BASE TABLE'
AND table_name IN ('tasks','task_status_transitions','execution_plans');
'@
    Assert-True ($result -eq '3') 'Authoritative base tables not present.'
}

function Assert-SchemaV2 {
    Assert-BaseSchema
    $result = Invoke-Sql @'
SELECT CASE WHEN
    (SELECT count(*) FROM app_schema_versions WHERE version=2)=1
    AND (SELECT count(*) FROM app_schema_versions)=1
    AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public'
        AND table_name='tasks' AND column_name='execution_generation' AND data_type='bigint' AND is_nullable='NO')
    AND to_regclass('public.task_executions') IS NOT NULL
    AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public'
        AND table_name='task_status_transitions' AND column_name='execution_id' AND data_type='uuid')
    AND (SELECT count(*) FROM information_schema.columns WHERE table_schema='public'
        AND table_name='task_executions' AND column_name IN
        ('id','task_id','worker_id','generation','status','started_at_utc','completed_at_utc'))=7
    AND EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid='public.task_executions'::regclass
        AND contype='p' AND convalidated AND pg_get_constraintdef(oid)='PRIMARY KEY (id)')
    AND EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid='public.task_executions'::regclass
        AND contype='u' AND convalidated AND pg_get_constraintdef(oid)='UNIQUE (task_id, generation)')
    AND EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid='public.task_executions'::regclass
        AND contype='f' AND convalidated AND confrelid='public.tasks'::regclass
        AND pg_get_constraintdef(oid)='FOREIGN KEY (task_id) REFERENCES tasks(id)')
    AND EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid='public.task_executions'::regclass
        AND contype='c' AND convalidated AND pg_get_constraintdef(oid) LIKE '%generation > 0%')
    AND EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid='public.task_executions'::regclass
        AND contype='c' AND convalidated AND pg_get_constraintdef(oid) LIKE '%completed_at_utc IS NULL%'
        AND pg_get_constraintdef(oid) LIKE '%completed_at_utc IS NOT NULL%')
    AND EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid='public.task_status_transitions'::regclass
        AND confrelid='public.task_executions'::regclass AND contype='f' AND convalidated
        AND pg_get_constraintdef(oid)='FOREIGN KEY (execution_id) REFERENCES task_executions(id)')
    AND EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid='public.tasks'::regclass
        AND contype='c' AND convalidated AND pg_get_constraintdef(oid) LIKE '%execution_generation >= 0%')
    AND EXISTS (SELECT 1 FROM pg_index WHERE indexrelid='public.ix_task_executions_one_active'::regclass
        AND indisunique AND indisvalid AND pg_get_expr(indpred,indrelid)='(completed_at_utc IS NULL)'
        AND indkey::text=(SELECT attnum::text FROM pg_attribute
            WHERE attrelid='public.task_executions'::regclass AND attname='task_id'))
THEN 'schema-v2-ok' ELSE 'invalid' END;
'@
    Assert-True ($result -eq 'schema-v2-ok') 'Schema v2 version/columns/constraints/index verification failed.'
}

function Invoke-SchemaTransaction {
    param([string]$Sql)
    $null = Invoke-Native -Arguments @('exec', '-i', (Get-ContainerId 'postgres'), 'psql',
        '-X', '-U', 'hic_test', '-d', 'hic_test', '-v', 'ON_ERROR_STOP=1', '--single-transaction', '-f', '-') -InputText $Sql
}

function Prepare-Schema {
    Assert-Stage $state 'dependencies-ready'
    Assert-DependencyIdentity
    Assert-NoApplications
    Assert-DatabaseIdentity
    Assert-True ((Invoke-Sql "SELECT count(*) FROM pg_tables WHERE schemaname !~ '^pg_' AND schemaname <> 'information_schema';") -eq '0') 'Schema preparation requires a new empty database.'
    $state.stage = 'schema-preparing'
    Save-State
    Invoke-SchemaTransaction $schema.BaseSql
    Assert-BaseSchema
    Invoke-SchemaTransaction $schema.MigrationSql
    Assert-SchemaV2
    $state.stage = 'schema-ready'
    Save-State
    Write-Host 'Authoritative base schema and explicit v2 migration verified; applications have not started.'
}

function Start-PreparedApi {
    Assert-Stage $state 'schema-ready'
    Assert-DependencyIdentity
    Assert-NoApplications
    Assert-DatabaseIdentity
    Assert-SchemaV2
    $state.stage = 'api-starting'
    Save-State
    Invoke-Compose @('build', 'control-plane', 'agent-worker')
    Invoke-Compose @('up', '-d', '--no-build', '--no-deps', 'control-plane')
    Assert-Readiness
    $state.stage = 'api-ready'
    Save-State
    Write-Host 'R2 API ready; Worker image built but Worker not started.'
}

function Start-Dependencies {
    Assert-True $schema.RequiresV2 'Staged actions require the R2 schema contract.'
    Assert-True ($null -eq $state -and -not (Test-Path -LiteralPath $stateDirectory)) 'Dependency startup requires a fresh project/state directory.'
    $null = New-Item -ItemType Directory -Path $stateDirectory
    $script:state = [pscustomobject]@{
        project = $Project; port = $ApiPort; root = $root; taskId = $null
        mode = 'r2-staged'; stage = 'dependencies-starting'
        sourceHash = $schema.SourceHash; migrationHash = $schema.MigrationHash
        dependencyIdentity = $null
    }
    Save-State
    Get-IsolatedConfiguration | Set-Content -LiteralPath $configPath -Encoding UTF8
    Invoke-Compose @('up', '-d', '--build', '--no-deps', 'postgres', 'redis', 'model-stub')
    Assert-DependenciesReady
    Assert-NoApplications
    $state.dependencyIdentity = Get-DependencyIdentity
    $state.stage = 'dependencies-ready'
    Save-State
    Write-Host 'Only isolated dependencies started; run prepare-schema next.'
}

function Assert-DependenciesReady {
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    do {
        Get-Resources
        $ready = $true
        foreach ($service in @('postgres', 'redis', 'model-stub')) {
            $null = Get-ContainerId $service
            $health = Get-Property $containers[$service].State 'Health'
            if ($null -eq $health -or $health.Status -ne 'healthy') { $ready = $false }
        }
        if ($ready) { break }
        Assert-True ([DateTime]::UtcNow -lt $deadline) 'Dependency readiness timed out.'
        Start-Sleep -Seconds 1
    } while ($true)
    Assert-True ((Invoke-Sql 'SELECT 1;') -eq '1') 'PostgreSQL readiness failed.'
    Assert-True ((Invoke-Native @('exec', (Get-ContainerId 'redis'), 'redis-cli', 'PING')).Trim() -eq 'PONG') 'Redis readiness failed.'
}

function Get-QueueLength {
    param([string]$Key)
    return [int](Invoke-Native @('exec', (Get-ContainerId 'redis'), 'redis-cli', 'LLEN', $Key)).Trim()
}

function Assert-Readiness {
    Get-Resources
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    foreach ($service in @('postgres', 'redis', 'model-stub', 'control-plane')) {
        $null = Get-ContainerId $service
    }
    while ($true) {
        Get-Resources
        $ready = $true
        foreach ($service in @('postgres', 'redis', 'model-stub')) {
            $health = Get-Property $containers[$service].State 'Health'
            if ($null -eq $health -or $health.Status -ne 'healthy') { $ready = $false }
        }
        if ($ready -and (Invoke-Sql 'SELECT 1;') -eq '1') { break }
        if ([DateTime]::UtcNow -ge $deadline) { throw 'Dependency readiness timed out.' }
        Start-Sleep -Seconds 1
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while ($true) {
        $null = Get-ContainerId 'control-plane'
        $url = "http://127.0.0.1:$ApiPort/health"
        Assert-ApiBinding
        try { $response = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 3 }
        catch {
            if ([DateTime]::UtcNow -ge $deadline) { throw 'API readiness timed out (response withheld).' }
            Start-Sleep -Seconds 1
            Get-Resources
            continue
        }
        Assert-True ([int]$response.StatusCode -eq 200 -and ($response.Content | ConvertFrom-Json).status -eq 'ok') 'Unexpected health response.'
        break
    }
    Assert-True ((Invoke-Native @('exec', (Get-ContainerId 'redis'), 'redis-cli', 'PING')).Trim() -eq 'PONG') 'Redis is not ready.'
    Write-Host 'API, PostgreSQL, Redis and stub readiness passed.'
}

function Save-State {
    $state | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding UTF8
}

function Get-IsolatedConfiguration {
    $configuration = Get-Content -LiteralPath $templatePath -Raw | ConvertFrom-Json
    foreach ($service in $configuration.services.PSObject.Properties) {
        if ($service.Value.PSObject.Properties.Name -contains 'build') {
            $service.Value.build.context = [IO.Path]::GetFullPath((Join-Path $root $service.Value.build.context))
        }
    }
    $configuration.services.'control-plane'.ports = @("127.0.0.1:${ApiPort}:8080")
    return ($configuration | ConvertTo-Json -Depth 20)
}

Validate-Configuration
if ($Action -eq 'validate') { return }
if ($Action -eq 'start') { Assert-LegacyStart }
Assert-True (-not [string]::IsNullOrWhiteSpace($Project)) 'Specify a unique hic-net10-<12 hex digits> project.'
if ($Action -in @('start', 'start-dependencies', 'prepare-schema', 'start-api', 'persistence', 'worker', 'stop')) {
    Assert-True $ApproveRuntime.IsPresent 'This action needs explicit runtime approval and -ApproveRuntime.'
}
Assert-True ($null -ne (Get-Command podman -ErrorAction SilentlyContinue)) 'Podman CLI is required.'
$stateDirectory = Join-Path (Join-Path $root '.state') $Project
$statePath = Join-Path $stateDirectory 'state.json'
$configPath = Join-Path $stateDirectory 'compose.json'
$state = $null
if (Test-Path -LiteralPath $statePath) {
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    Assert-True ($state.project -eq $Project -and $state.port -eq $ApiPort -and $state.root -eq $root) 'Local state identity mismatch.'
    if ((Get-Property $state 'mode') -eq 'r2-staged') {
        Assert-True ((Get-Property $state 'stage') -in @('dependencies-starting', 'dependencies-ready', 'schema-preparing', 'schema-ready', 'api-starting', 'api-ready')) 'Invalid staged progress.'
    }
}
if (Test-Path -LiteralPath $configPath) {
    $existing = (Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json) | ConvertTo-Json -Depth 20
    Assert-True ($existing -eq (Get-IsolatedConfiguration)) 'Generated configuration differs from the isolated template; refusing execution.'
}
$null = Invoke-Native @('info', '--format', '{{.Host.OS}}')
Get-Resources
if ($Action -in @('preflight', 'start', 'start-dependencies', 'start-api')) {
    if (-not $containers.ContainsKey('control-plane') -or -not $containers['control-plane'].State.Running) {
        $listeners = @(Get-NetTCPConnection -State Listen -ErrorAction Stop | Where-Object { $_.LocalPort -eq $ApiPort })
        Assert-True ($listeners.Count -eq 0) 'Requested host port is in use.'
    } else { Assert-ApiBinding }
}
if ($Action -eq 'preflight') {
    Write-Host 'Ownership and host port preflight passed. No machine or service was started.'
    return
}
if ($Action -eq 'start-dependencies') {
    Start-Dependencies
    return
}
if ($Action -eq 'start') {
    if ($null -eq $state) {
        $null = New-Item -ItemType Directory -Path $stateDirectory -Force
        $state = [pscustomobject]@{ project = $Project; port = $ApiPort; root = $root; taskId = $null }
        Save-State
    }
    Get-IsolatedConfiguration | Set-Content -LiteralPath $configPath -Encoding UTF8
    Invoke-Compose @('up', '-d', '--build', 'postgres', 'redis', 'model-stub', 'control-plane')
    Assert-Readiness
    return
}
Assert-True ($null -ne $state -and (Test-Path -LiteralPath $configPath)) 'Run the approved isolated startup first.'
if ($Action -in @('prepare-schema', 'start-api')) {
    Assert-True $schema.RequiresV2 'Staged actions require R2.'
    if ($Action -eq 'prepare-schema') { Prepare-Schema; return }
    Start-PreparedApi
    return
}
if ($Action -eq 'stop') {
    Get-Resources
    $ids = @($containers.Values | Where-Object { $_.State.Running } | ForEach-Object { $_.Id })
    if ($ids.Count -gt 0) { $null = Invoke-Native (@('stop', '--time', '30') + $ids) }
    Get-Resources
    Assert-True (@($containers.Values | Where-Object { $_.State.Running }).Count -eq 0) 'Test containers did not stop.'
    Write-Host 'Only owned test containers stopped; volumes and networks retained.'
    return
}
if ($schema.RequiresV2) {
    Assert-True ((Get-Property $state 'mode') -eq 'r2-staged' -and
        (Get-Property $state 'stage') -eq 'api-ready') 'R2 application actions require completed staged startup.'
    Assert-DependencyIdentity
    Assert-DatabaseIdentity
    Assert-SchemaV2
}
Assert-Readiness
if ($Action -eq 'readiness') { return }
if ($Action -eq 'persistence') {
    Assert-True (-not $containers.ContainsKey('agent-worker') -or -not $containers['agent-worker'].State.Running) 'Worker must be stopped for persistence validation.'
    Assert-True ($null -eq $state.taskId) 'A test task already exists; use worker validation or a fresh project.'
    Assert-True ((Get-QueueLength 'agentlab:task-queue') -eq 0 -and (Get-QueueLength 'agentlab:task-queue:dead-letter') -eq 0) 'Unexpected queue or dead-letter data.'
    $task = Invoke-Api '/tasks' 'Post' @{ title = "HIC runtime $Project"; description = 'Synthetic isolated test data' } 201
    $state.taskId = ([guid]$task.id).ToString()
    Save-State
    Assert-True ($task.status -eq 'Draft') 'Created task is not Draft.'
    $id = $state.taskId
    Assert-True ((Invoke-Sql "SELECT status FROM tasks WHERE id = '$id';") -eq 'Draft') 'PostgreSQL task persistence failed.'
    Assert-True ((Invoke-Api "/tasks/$id").status -eq 'Draft') 'Task read-back failed.'
    Write-Host 'Synthetic Draft task persisted in PostgreSQL and read back through the API.'
    return
}
if ($Action -eq 'worker') {
    Assert-True ($null -ne $state.taskId) 'Run persistence validation first.'
    $id = ([guid]$state.taskId).ToString()
    $task = Invoke-Api "/tasks/$id"
    Assert-True ($task.title -eq "HIC runtime $Project") 'Test task identity mismatch.'
    Assert-True ($task.status -eq 'Draft') 'Worker validation requires the recorded Draft task; no automatic retry or cleanup.'
    Assert-True (-not $containers.ContainsKey('agent-worker') -or -not $containers['agent-worker'].State.Running) 'Worker must be stopped before queue assertion.'
    Assert-True ((Get-QueueLength 'agentlab:task-queue') -eq 0 -and (Get-QueueLength 'agentlab:task-queue:dead-letter') -eq 0) 'Unexpected queue data.'
    $approval = Invoke-Api "/tasks/$id/approve" 'Post'
    Assert-True ($approval.status -eq 'Approved' -and (Get-QueueLength 'agentlab:task-queue') -eq 1) 'Task approval or queue dispatch failed.'
    $queued = (Invoke-Native @('exec', (Get-ContainerId 'redis'), 'redis-cli', 'LINDEX', 'agentlab:task-queue', '0')).Trim()
    Assert-True ($queued -eq $id) 'Queued task ID mismatch.'
    Get-Resources
    Invoke-Compose @('up', '-d', '--no-build', '--no-recreate', '--no-deps', 'agent-worker')
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    do {
        Get-Resources
        $null = Get-ContainerId 'agent-worker'
        $task = Invoke-Api "/tasks/$id"
        if ($task.status -in @('Failed', 'Blocked', 'Cancelled', 'Rejected')) { throw "Task ended unexpectedly: $($task.status)" }
        if ($task.status -eq 'Completed') { break }
        if ([DateTime]::UtcNow -ge $deadline) { throw 'Worker task completion timed out.' }
        Start-Sleep -Seconds 1
    } while ($true)
    Assert-True ($task.result -eq 'HIC_RUNTIME_TEST_OK' -and $task.attemptCount -eq 1) 'Unexpected task output or attempt count.'
    Assert-True ((Invoke-Sql "SELECT status FROM tasks WHERE id = '$id';") -eq 'Completed') 'Completed task not persisted.'
    $history = Invoke-Sql "SELECT string_agg(from_status || '>' || to_status, ',' ORDER BY changed_at_utc) FROM task_status_transitions WHERE task_id = '$id';"
    Assert-True ($history -eq 'Draft>Approved,Approved>Executing,Executing>Completed') 'Unexpected transition audit history.'
    Assert-True ((Get-QueueLength 'agentlab:task-queue') -eq 0 -and (Get-QueueLength 'agentlab:task-queue:dead-letter') -eq 0) 'Queue or DLQ assertion failed.'
    Write-Host 'Worker result, PostgreSQL persistence, queue and audit assertions passed.'
}
