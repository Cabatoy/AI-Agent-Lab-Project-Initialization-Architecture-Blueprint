[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('new-build', 'rebuild-all', 'delete-all', 'status', 'bootstrap-dev')]
    [string]$Action,
    [switch]$ConfirmDataRemoval,
    [switch]$AllowWslNetworkingChange,
    [switch]$Reset,
    [ValidateSet('auto', 'disabled', 'podman-ai-lab', 'ollama', 'openai-compatible')]
    [string]$LlmProvider = 'auto',
    [string]$LlmEndpoint,
    [string]$LlmModel,
    [string]$LlmApiKey
)

$ErrorActionPreference = 'Stop'
if (Get-Variable PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $false
}
$repositoryRoot = Resolve-Path (Join-Path $PSScriptRoot '..')

function Invoke-Podman {
    param([string[]]$Arguments)

    & podman @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Podman command failed: podman $($Arguments -join ' ')"
    }
}

function Invoke-Compose {
    param([string[]]$Arguments)

    Invoke-Podman (@('compose') + $Arguments)
}

function Initialize-PodmanMachine {
    if (-not (Get-Command podman -ErrorAction SilentlyContinue)) {
        throw 'Podman CLI is not installed. Install Podman Desktop, then rerun this command.'
    }

    $machineJson = (& podman machine list --format json) -join [Environment]::NewLine
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not list Podman machines.'
    }

    $machines = @($machineJson | ConvertFrom-Json)
    if ($machines.Count -eq 0) {
        if ($Action -eq 'delete-all' -or $Action -eq 'status') {
            Write-Host 'No Podman machine exists; there is nothing to inspect or delete.'
            return $false
        }

        if (-not $AllowWslNetworkingChange) {
            throw 'Windows user-mode networking can affect all WSL distributions while active. Rerun with -AllowWslNetworkingChange after confirming.'
        }

        Invoke-Podman @('machine', 'init', '--now', '--update-connection', '--user-mode-networking', '--cpus', '4', '--memory', '4096', '--disk-size', '100')
    }
    else {
        $machine = $machines | Select-Object -First 1
        if (($Action -eq 'new-build' -or $Action -eq 'rebuild-all') -and -not $machine.UserModeNetworking) {
            if (-not $AllowWslNetworkingChange) {
                throw 'Windows user-mode networking can affect all WSL distributions while active. Rerun with -AllowWslNetworkingChange after confirming.'
            }
            if ($machine.Running) {
                Invoke-Podman @('machine', 'stop')
            }
            Invoke-Podman @('machine', 'set', '--user-mode-networking')
            Invoke-Podman @('machine', 'start')
        }
        elseif (-not $machine.Running) {
            Invoke-Podman @('machine', 'start')
        }
    }

    if ($Action -eq 'new-build' -or $Action -eq 'rebuild-all') {
        $cgroupPath = '/sys/fs/cgroup/non-systemd/cgroup.subtree_control'
        $cgroupState = (& podman machine ssh -- cat $cgroupPath 2>$null) -join ' '
        if ($LASTEXITCODE -eq 0 -and $cgroupState -notmatch '(^|\s)pids(\s|$)') {
            Invoke-Podman @('machine', 'ssh', '--', 'sudo', 'sh', '-c', "echo +pids > $cgroupPath")
        }
    }

    $null = & podman compose version
    if ($LASTEXITCODE -ne 0) {
        throw 'Podman Compose is unavailable. Install a supported Compose provider and retry.'
    }

    return $true
}

function Get-ApiBaseUrl {
    $candidates = @('http://localhost:8080')
    $addressOutput = & podman machine ssh -- ip -o -4 addr show dev eth0 2>$null
    if ($LASTEXITCODE -eq 0) {
        $addressMatch = [regex]::Match(($addressOutput -join ' '), '\binet\s+((?:\d{1,3}\.){3}\d{1,3})/')
        if ($addressMatch.Success) {
            $candidates += "http://$($addressMatch.Groups[1].Value):8080"
        }
    }

    foreach ($candidate in $candidates) {
        try {
            $health = Invoke-RestMethod -Uri "$candidate/health" -TimeoutSec 2
            if ($health.status -eq 'ok') {
                return $candidate
            }
        }
        catch {
        }
    }

    return $candidates[-1]
}

function Get-AiLabContainers {
    $containers = & podman ps --format '{{.Names}}|{{.Image}}' 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $containers) {
        return @()
    }

    $candidates = @($containers |
        Where-Object { $_ -match 'podman-ai-lab|llama-stack|instructlab|llama\.cpp|ramalama-llama-server|ramalama' } |
        ForEach-Object { ($_ -split '\|')[0] } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) })

    return $candidates
}

function Get-AiLabModelIdFromHostPort {
    param([string]$ContainerName)

    if (-not $ContainerName) {
        return $null
    }

    $portOutput = & podman port $ContainerName '8000/tcp' 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $portOutput) {
        return $null
    }

    $portMatch = [regex]::Match(($portOutput -join ' '), ':(\d+)')
    if (-not $portMatch.Success) {
        return $null
    }

    $port = $portMatch.Groups[1].Value
    try {
        $payload = Invoke-RestMethod -Uri "http://localhost:$port/v1/models" -TimeoutSec 5
        if ($payload.data -and $payload.data.Count -gt 0 -and $payload.data[0].id) {
            return [string]$payload.data[0].id
        }
    }
    catch {
    }

    return $null
}

function Select-AiLabContainerFromPrompt {
    param([string[]]$Containers)

    $available = @($Containers | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($available.Count -eq 0) {
        return $null
    }

    if ($available.Count -eq 1) {
        return $available[0]
    }

    if ([Console]::IsInputRedirected) {
        Write-Warning "Input is redirected. Defaulting to the first detected AI Lab service: $($available[0])"
        return $available[0]
    }

    Write-Host 'Available Podman AI Lab model services:'
    for ($index = 0; $index -lt $available.Count; $index++) {
        $containerName = $available[$index]
        $modelId = Get-AiLabModelIdFromHostPort -ContainerName $containerName
        if ($modelId) {
            Write-Host "[$($index + 1)] $containerName -> $modelId"
        }
        else {
            Write-Host "[$($index + 1)] $containerName"
        }
    }

    while ($true) {
        $selection = Read-Host "Select AI Lab service number (1-$($available.Count), Enter=1)"
        if (-not $selection) {
            return $available[0]
        }

        $selectedIndex = 0
        if ([int]::TryParse($selection, [ref]$selectedIndex) -and $selectedIndex -ge 1 -and $selectedIndex -le $available.Count) {
            return $available[$selectedIndex - 1]
        }

        Write-Warning 'Invalid selection. Try again.'
    }
}

function Connect-AiLabAlias {
    param(
        [string]$NetworkName = 'enterprise_default',
        [string]$Alias = 'ai-lab',
        [string]$ContainerName
    )

    $aiLabContainerName = $ContainerName
    if (-not $aiLabContainerName) {
        $aiLabContainerName = (Get-AiLabContainers | Select-Object -First 1)
    }

    if (-not $aiLabContainerName) {
        return $false
    }

    $connectOutput = $null
    $exitCode = 0
    try {
        $connectOutput = & podman network connect --alias $Alias $NetworkName $aiLabContainerName 2>&1
        $exitCode = $LASTEXITCODE
    }
    catch {
        $connectOutput = $_.Exception.Message
        $exitCode = 1
    }

    if ($exitCode -eq 0) {
        Write-Host "AI Lab alias is ready: http://${Alias}:8000"
        return $true
    }

    $outputText = ($connectOutput -join [Environment]::NewLine)
    if ($outputText -match 'already exists|already connected|is already connected|name already in use') {
        Write-Host "AI Lab alias already attached: http://${Alias}:8000"
        return $true
    }

    Write-Warning "AI Lab alias attach skipped: $outputText"
    return $false
}

function Disconnect-AiLabFromNetwork {
    param([string]$NetworkName = 'enterprise_default')

    $aiLabContainers = @(Get-AiLabContainers)
    if ($aiLabContainers.Count -eq 0) {
        return
    }

    foreach ($aiLabContainerName in $aiLabContainers) {
        $disconnectOutput = $null
        $exitCode = 0
        try {
            $disconnectOutput = & podman network disconnect $NetworkName $aiLabContainerName 2>&1
            $exitCode = $LASTEXITCODE
        }
        catch {
            $disconnectOutput = $_.Exception.Message
            $exitCode = 1
        }

        if ($exitCode -eq 0) {
            Write-Host "AI Lab container detached from $NetworkName before recreate: $aiLabContainerName"
            continue
        }

        $outputText = ($disconnectOutput -join [Environment]::NewLine)
        if ($outputText -match 'is not connected|no such network|network not found|no static ips provided') {
            continue
        }

        Write-Warning "AI Lab network detach skipped for ${aiLabContainerName}: $outputText"
    }
}

function Get-ModelIdsFromEndpoint {
    param([string]$Endpoint)

    if (-not $Endpoint) {
        return @()
    }

    $response = $null
    try {
        $response = & podman run --rm --network enterprise_default curlimages/curl:8.10.1 -sS -m 20 "$Endpoint/v1/models" 2>$null
    }
    catch {
        return @()
    }

    if ($LASTEXITCODE -ne 0 -or -not $response) {
        return @()
    }

    try {
        $payload = $response | ConvertFrom-Json
        $modelIds = New-Object System.Collections.Generic.List[string]

        if ($payload.data) {
            foreach ($item in $payload.data) {
                if ($item.id) {
                    $modelIds.Add([string]$item.id)
                }
            }
        }

        if ($payload.models) {
            foreach ($item in $payload.models) {
                if ($item.model) {
                    $modelIds.Add([string]$item.model)
                }
            }
        }

        return @($modelIds | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)
    }
    catch {
        return @()
    }
}

function Select-ModelIdFromCandidates {
    param(
        [string[]]$Candidates,
        [string]$Endpoint
    )

    $available = @($Candidates | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($available.Count -eq 0) {
        return $null
    }

    if ($available.Count -eq 1) {
        Write-Host "Detected AI Lab model: $($available[0])"
        return $available[0]
    }

    Write-Host "Detected AI Lab models on ${Endpoint}:"
    for ($index = 0; $index -lt $available.Count; $index++) {
        Write-Host "[$($index + 1)] $($available[$index])"
    }

    if ([Console]::IsInputRedirected) {
        Write-Warning "Input is redirected. Defaulting to the first detected model: $($available[0])"
        return $available[0]
    }

    while ($true) {
        $selection = Read-Host "Select model number (1-$($available.Count), Enter=1)"
        if (-not $selection) {
            return $available[0]
        }

        $selectedIndex = 0
        if ([int]::TryParse($selection, [ref]$selectedIndex) -and $selectedIndex -ge 1 -and $selectedIndex -le $available.Count) {
            return $available[$selectedIndex - 1]
        }

        Write-Warning 'Invalid selection. Try again.'
    }
}

function Select-LlmProviderFromPrompt {
    param([bool]$AiLabAliasReady)

    if ([Console]::IsInputRedirected) {
        Write-Warning 'Input is redirected. Using default provider selection: auto.'
        return 'auto'
    }

    $options = @(
        [pscustomobject]@{ Value = 'auto'; Description = 'auto (podman-ai-lab if alias exists, otherwise disabled)' }
        [pscustomobject]@{ Value = 'disabled'; Description = 'disabled' }
    )

    if ($AiLabAliasReady) {
        $options += [pscustomobject]@{ Value = 'podman-ai-lab'; Description = 'podman-ai-lab (ai-lab alias detected)' }
    }

    $options += [pscustomobject]@{ Value = 'ollama'; Description = 'ollama (host.containers.internal:11434)' }
    $options += [pscustomobject]@{ Value = 'openai-compatible'; Description = 'openai-compatible (requires endpoint/model)' }

    Write-Host 'Reset mode LLM provider selection:'
    for ($index = 0; $index -lt $options.Count; $index++) {
        Write-Host "[$($index + 1)] $($options[$index].Description)"
    }

    while ($true) {
        $selection = Read-Host "Select provider number (1-$($options.Count), Enter=1)"
        if (-not $selection) {
            return $options[0].Value
        }

        $selectedIndex = 0
        if ([int]::TryParse($selection, [ref]$selectedIndex) -and $selectedIndex -ge 1 -and $selectedIndex -le $options.Count) {
            return $options[$selectedIndex - 1].Value
        }

        Write-Warning 'Invalid selection. Try again.'
    }
}

function Resolve-ModelConfiguration {
    param(
        [string]$ProviderSelection,
        [bool]$AiLabAliasReady,
        [string]$RequestedEndpoint,
        [string]$RequestedModel,
        [string]$RequestedApiKey
    )

    $provider = $ProviderSelection
    $endpoint = $RequestedEndpoint
    $model = $RequestedModel
    $apiKey = $RequestedApiKey

    if ($provider -eq 'auto') {
        if ($AiLabAliasReady) {
            $provider = 'podman-ai-lab'
        }
        else {
            $provider = 'disabled'
        }
    }

    switch ($provider) {
        'disabled' {
            return [pscustomobject]@{
                Provider = 'disabled'
                Endpoint = ''
                Model = ''
                ApiKey = ''
                Message = '.env updated with MODEL_PROVIDER=disabled.'
            }
        }
        'podman-ai-lab' {
            if (-not $endpoint) {
                $endpoint = 'http://ai-lab:8000'
            }

            if (-not $model) {
                $detectedModels = Get-ModelIdsFromEndpoint -Endpoint $endpoint
                $model = Select-ModelIdFromCandidates -Candidates $detectedModels -Endpoint $endpoint
            }

            if (-not $model) {
                return [pscustomobject]@{
                    Provider = 'disabled'
                    Endpoint = ''
                    Model = ''
                    ApiKey = ''
                    Message = '.env created with MODEL_PROVIDER=disabled because podman-ai-lab model id could not be detected.'
                }
            }

            return [pscustomobject]@{
                Provider = 'podman-ai-lab'
                Endpoint = $endpoint
                Model = $model
                ApiKey = $apiKey
                Message = '.env updated for podman-ai-lab endpoint and selected model.'
            }
        }
        'ollama' {
            if (-not $endpoint) {
                $endpoint = 'http://host.containers.internal:11434'
            }

            if (-not $model) {
                $model = 'llama3.1'
            }

            return [pscustomobject]@{
                Provider = 'ollama'
                Endpoint = $endpoint
                Model = $model
                ApiKey = $apiKey
                Message = '.env updated for ollama endpoint and selected model.'
            }
        }
        'openai-compatible' {
            if (-not $endpoint) {
                if (-not [Console]::IsInputRedirected) {
                    $endpoint = Read-Host 'Enter openai-compatible endpoint URL (e.g. https://api.openai.com)'
                }

                if (-not $endpoint) {
                    throw 'LlmProvider=openai-compatible requires -LlmEndpoint.'
                }
            }

            if (-not $model) {
                if (-not [Console]::IsInputRedirected) {
                    $model = Read-Host 'Enter openai-compatible model name (e.g. gpt-4o-mini)'
                }

                if (-not $model) {
                    throw 'LlmProvider=openai-compatible requires -LlmModel.'
                }
            }

            if (-not $apiKey -and -not [Console]::IsInputRedirected) {
                $apiKey = Read-Host 'Enter API key (optional, Enter to skip)'
            }

            return [pscustomobject]@{
                Provider = 'openai-compatible'
                Endpoint = $endpoint
                Model = $model
                ApiKey = $apiKey
                Message = '.env updated for openai-compatible endpoint and selected model.'
            }
        }
        default {
            throw "Unsupported LlmProvider: $provider"
        }
    }
}

function Set-LocalEnvFile {
    param(
        [string]$Provider,
        [string]$Endpoint,
        [string]$Model,
        [string]$ApiKey,
        [int]$TimeoutSeconds = 180
    )

    $envPath = Join-Path $repositoryRoot '.env'

    $content = @(
        "MODEL_PROVIDER=$Provider"
        "MODEL_ENDPOINT=$Endpoint"
        "MODEL_NAME=$Model"
        "MODEL_API_KEY=$ApiKey"
        "MODEL_TIMEOUT_SECONDS=$TimeoutSeconds"
    ) -join [Environment]::NewLine

    $utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($envPath, $content, $utf8WithoutBom)
}

function Wait-ControlPlane {
    param([int]$MaxAttempts = 20)

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        $baseUrl = Get-ApiBaseUrl
        try {
            $health = Invoke-RestMethod -Uri "$baseUrl/health" -TimeoutSec 3
            if ($health.status -eq 'ok') {
                return $baseUrl
            }
        }
        catch {
        }

        Start-Sleep -Seconds 2
    }

    throw 'Control Plane health check timed out.'
}

function Invoke-SmokeTest {
    param([string]$BaseUrl)

    $payload = @{ title = 'LLM bootstrap smoke test'; description = 'kisa bir uygulama plani uret' }
    $task = Invoke-RestMethod -Method Post -Uri "$BaseUrl/tasks" -ContentType 'application/json' -Body ($payload | ConvertTo-Json)
    $taskId = $task.id

    $null = Invoke-RestMethod -Method Post -Uri "$BaseUrl/tasks/$taskId/approve"

    for ($i = 1; $i -le 20; $i++) {
        Start-Sleep -Seconds 2
        $state = Invoke-RestMethod -Method Get -Uri "$BaseUrl/tasks/$taskId"
        if ($state.status -eq 'Completed' -or $state.status -eq 'Failed') {
            Write-Host "Smoke test task: $taskId"
            Write-Host "Smoke test status: $($state.status)"
            if ($state.status -eq 'Failed') {
                Write-Warning "Smoke test error: $($state.errorMessage)"
            }
            return
        }
    }

    Write-Warning 'Smoke test did not reach Completed/Failed within timeout.'
}

function Write-ActiveModelSummary {
    $envPath = Join-Path $repositoryRoot '.env'
    if (Test-Path $envPath) {
        $envLines = Get-Content -Path $envPath
        $envMap = @{}
        foreach ($line in $envLines) {
            if ($line -match '^([^=]+)=(.*)$') {
                $envMap[$matches[1]] = $matches[2]
            }
        }

        Write-Host 'Configured model (.env):'
        Write-Host "  Provider: $($envMap['MODEL_PROVIDER'])"
        Write-Host "  Endpoint: $($envMap['MODEL_ENDPOINT'])"
        Write-Host "  Model: $($envMap['MODEL_NAME'])"
    }

    $runtime = & podman exec agentlab-worker /bin/sh -c "printenv MODEL_PROVIDER; printenv MODEL_ENDPOINT; printenv MODEL_NAME" 2>$null
    if ($LASTEXITCODE -eq 0 -and $runtime.Count -ge 3) {
        Write-Host 'Runtime model (agentlab-worker):'
        Write-Host "  Provider: $($runtime[0])"
        Write-Host "  Endpoint: $($runtime[1])"
        Write-Host "  Model: $($runtime[2])"
    }

    $aliasTarget = $null
    try {
        $networkInspect = & podman network inspect enterprise_default 2>$null
        if ($LASTEXITCODE -eq 0 -and $networkInspect) {
            $network = @($networkInspect | ConvertFrom-Json)
            if ($network.Count -gt 0 -and $network[0].containers) {
                foreach ($property in $network[0].containers.PSObject.Properties) {
                    $container = $property.Value
                    if ($container.aliases -and ($container.aliases -contains 'ai-lab')) {
                        $aliasTarget = $container.name
                        break
                    }
                }
            }
        }
    }
    catch {
    }

    if ($aliasTarget) {
        Write-Host "ai-lab alias target: $aliasTarget"
    }
}

if (($Action -eq 'rebuild-all' -or $Action -eq 'delete-all') -and -not $ConfirmDataRemoval) {
    throw 'This operation removes local task data. Rerun with -ConfirmDataRemoval after confirming.'
}

Push-Location $repositoryRoot
try {
    $machineReady = Initialize-PodmanMachine
    if (-not $machineReady) {
        return
    }

    switch ($Action) {
        'new-build' {
            Disconnect-AiLabFromNetwork
            Invoke-Compose @('down', '--remove-orphans')
            Invoke-Compose @('up', '-d', '--build')
            Connect-AiLabAlias | Out-Null
            $apiBaseUrl = Get-ApiBaseUrl
            Write-Host "Control Plane: $apiBaseUrl"
            Write-Host 'Task data is preserved.'
        }
        'rebuild-all' {
            Disconnect-AiLabFromNetwork
            Invoke-Compose @('down', '--remove-orphans', '--rmi', 'all', '--volumes')
            Invoke-Compose @('build', '--no-cache')
            Invoke-Compose @('up', '-d')
            Connect-AiLabAlias | Out-Null
            Write-Host "Control Plane: $(Get-ApiBaseUrl)"
            Write-Host 'Local task data was removed and all project images were rebuilt without cache.'
        }
        'delete-all' {
            Invoke-Compose @('down', '--remove-orphans', '--rmi', 'all', '--volumes')
            Write-Host 'This repository stack, its images, network, and data volumes were removed.'
            Write-Host 'The Podman machine and unrelated Podman resources were left untouched.'
        }
        'status' {
            Invoke-Compose @('ps')
            Write-Host "Control Plane: $(Get-ApiBaseUrl)"
        }
        'bootstrap-dev' {
            if ($Reset) {
                Write-Host 'Reset mode enabled: local task data will be removed.'
                Disconnect-AiLabFromNetwork
                Invoke-Compose @('down', '--remove-orphans', '--rmi', 'all', '--volumes')
            }
            else {
                Disconnect-AiLabFromNetwork
                Invoke-Compose @('down', '--remove-orphans')
            }

            Invoke-Compose @('up', '-d', 'postgres', 'redis', 'control-plane')
            $aliasReady = Connect-AiLabAlias

            $providerSelection = $LlmProvider
            if ($Reset -and -not $PSBoundParameters.ContainsKey('LlmProvider')) {
                $providerSelection = Select-LlmProviderFromPrompt -AiLabAliasReady $aliasReady
            }

            if ($providerSelection -eq 'podman-ai-lab') {
                $selectedAiLabContainer = Select-AiLabContainerFromPrompt -Containers (Get-AiLabContainers)
                if ($selectedAiLabContainer) {
                    Disconnect-AiLabFromNetwork
                    $aliasReady = Connect-AiLabAlias -ContainerName $selectedAiLabContainer
                }
            }

            $modelConfig = Resolve-ModelConfiguration `
                -ProviderSelection $providerSelection `
                -AiLabAliasReady $aliasReady `
                -RequestedEndpoint $LlmEndpoint `
                -RequestedModel $LlmModel `
                -RequestedApiKey $LlmApiKey

            Set-LocalEnvFile -Provider $modelConfig.Provider -Endpoint $modelConfig.Endpoint -Model $modelConfig.Model -ApiKey $modelConfig.ApiKey
            Write-Host $modelConfig.Message

            Invoke-Compose @('up', '-d', '--build')

            $apiBaseUrl = Wait-ControlPlane
            Write-Host "Control Plane: $apiBaseUrl"

            if ($modelConfig.Provider -ne 'disabled') {
                Invoke-SmokeTest -BaseUrl $apiBaseUrl
            }
            else {
                Write-Warning 'Smoke test skipped because MODEL_PROVIDER is disabled.'
            }

            Write-ActiveModelSummary
        }
    }
}
finally {
    Pop-Location
}