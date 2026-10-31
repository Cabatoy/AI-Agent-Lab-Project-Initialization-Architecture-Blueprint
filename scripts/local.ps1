[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('new-build', 'rebuild-all', 'delete-all', 'status', 'bootstrap-dev')]
    [string]$Action,
    [switch]$ConfirmDataRemoval,
    [switch]$AllowWslNetworkingChange,
    [switch]$Reset
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

function Get-AiLabContainerName {
    $containers = & podman ps --format '{{.Names}} {{.Image}}' 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $containers) {
        return $null
    }

    $candidate = $containers |
        Where-Object { $_ -match 'podman-ai-lab|llama-stack|instructlab|llama\.cpp|ramalama-llama-server|ramalama' } |
        Select-Object -First 1

    if (-not $candidate) {
        return $null
    }

    return ($candidate -split '\s+')[0]
}

function Connect-AiLabAlias {
    param(
        [string]$NetworkName = 'enterprise_default',
        [string]$Alias = 'ai-lab'
    )

    $aiLabContainerName = Get-AiLabContainerName
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

    $aiLabContainerName = Get-AiLabContainerName
    if (-not $aiLabContainerName) {
        return
    }

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
        Write-Host "AI Lab container detached from $NetworkName before recreate."
        return
    }

    $outputText = ($disconnectOutput -join [Environment]::NewLine)
    if ($outputText -match 'is not connected|no such network|network not found|no static ips provided') {
        return
    }

    Write-Warning "AI Lab network detach skipped: $outputText"
}

function Get-AiLabModelId {
    $response = $null
    try {
        $response = & podman run --rm --network enterprise_default curlimages/curl:8.10.1 -sS -m 20 http://ai-lab:8000/v1/models 2>$null
    }
    catch {
        return $null
    }

    if ($LASTEXITCODE -ne 0 -or -not $response) {
        return $null
    }

    try {
        $payload = $response | ConvertFrom-Json
        if ($payload.data -and $payload.data.Count -gt 0 -and $payload.data[0].id) {
            return [string]$payload.data[0].id
        }

        if ($payload.models -and $payload.models.Count -gt 0 -and $payload.models[0].model) {
            return [string]$payload.models[0].model
        }
    }
    catch {
    }

    return $null
}

function Set-LocalEnvFile {
    param([string]$ModelId)

    $envPath = Join-Path $repositoryRoot '.env'

    if (-not $ModelId) {
        $content = @(
            'MODEL_PROVIDER=disabled'
            'MODEL_ENDPOINT='
            'MODEL_NAME='
            'MODEL_API_KEY='
            'MODEL_TIMEOUT_SECONDS=180'
        ) -join [Environment]::NewLine

        Set-Content -Path $envPath -Value $content -Encoding utf8
        Write-Warning '.env created with MODEL_PROVIDER=disabled because AI Lab model id could not be detected.'
        return
    }

    $content = @(
        'MODEL_PROVIDER=podman-ai-lab'
        'MODEL_ENDPOINT=http://ai-lab:8000'
        "MODEL_NAME=$ModelId"
        'MODEL_API_KEY='
        'MODEL_TIMEOUT_SECONDS=180'
    ) -join [Environment]::NewLine

    Set-Content -Path $envPath -Value $content -Encoding utf8
    Write-Host '.env updated for AI Lab endpoint and detected model.'
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

            Invoke-Compose @('up', '-d', '--build')
            $aliasReady = Connect-AiLabAlias
            $modelId = $null
            if ($aliasReady) {
                $modelId = Get-AiLabModelId
            }

            Set-LocalEnvFile -ModelId $modelId

            $apiBaseUrl = Wait-ControlPlane
            Write-Host "Control Plane: $apiBaseUrl"

            if ($modelId) {
                Invoke-SmokeTest -BaseUrl $apiBaseUrl
            }
            else {
                Write-Warning 'Smoke test skipped because AI Lab model id was not detected and provider was set to disabled.'
            }
        }
    }
}
finally {
    Pop-Location
}