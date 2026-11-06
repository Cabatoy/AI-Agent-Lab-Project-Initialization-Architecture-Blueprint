[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('validate', 'preflight', 'start', 'readiness', 'persistence', 'worker', 'stop')]
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

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Validate-Configuration {
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
    Assert-True (-not ((Get-Content -LiteralPath $templatePath -Raw).Contains('${'))) 'Environment interpolation is forbidden.'
    Write-Host 'Static configuration validation passed.'
}

function Invoke-Native {
    param([string[]]$Arguments)
    $ErrorActionPreference = 'Continue'
    $PSNativeCommandUseErrorActionPreference = $false
    try { $output = @(& podman @Arguments 2>&1) }
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
Assert-True (-not [string]::IsNullOrWhiteSpace($Project)) 'Specify a unique hic-net10-<12 hex digits> project.'
if ($Action -in @('start', 'persistence', 'worker', 'stop')) {
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
}
if (Test-Path -LiteralPath $configPath) {
    $existing = (Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json) | ConvertTo-Json -Depth 20
    Assert-True ($existing -eq (Get-IsolatedConfiguration)) 'Generated configuration differs from the isolated template; refusing execution.'
}
$null = Invoke-Native @('info', '--format', '{{.Host.OS}}')
Get-Resources
if ($Action -eq 'preflight' -or $Action -eq 'start') {
    if (-not $containers.ContainsKey('control-plane') -or -not $containers['control-plane'].State.Running) {
        $listeners = @(Get-NetTCPConnection -State Listen -ErrorAction Stop | Where-Object { $_.LocalPort -eq $ApiPort })
        Assert-True ($listeners.Count -eq 0) 'Requested host port is in use.'
    } else { Assert-ApiBinding }
}
if ($Action -eq 'preflight') {
    Write-Host 'Ownership and host port preflight passed. No machine or service was started.'
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
if ($Action -eq 'stop') {
    Get-Resources
    $ids = @($containers.Values | Where-Object { $_.State.Running } | ForEach-Object { $_.Id })
    if ($ids.Count -gt 0) { $null = Invoke-Native (@('stop', '--time', '30') + $ids) }
    Get-Resources
    Assert-True (@($containers.Values | Where-Object { $_.State.Running }).Count -eq 0) 'Test containers did not stop.'
    Write-Host 'Only owned test containers stopped; volumes and networks retained.'
    return
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
