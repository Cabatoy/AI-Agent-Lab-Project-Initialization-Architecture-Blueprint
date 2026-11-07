# Human In Control local launcher (phase 1: status and guarded native preview).
# Never mutates Podman, DB, Docker volumes, Worker, or an existing process.
[CmdletBinding()]
param(
    [Parameter(Position=0)]
    [ValidateSet('up', 'status', 'doctor')]
    [string]$Action = 'up',
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
if (Get-Variable PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $false
}
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

function Test-HttpReady {
    param([string]$Uri, [string]$RequiredField, [string]$ExpectedValue)
    try {
        $response = Invoke-WebRequest -Uri $Uri -Method Get -TimeoutSec 2 -UseBasicParsing -ErrorAction Stop
        if ([int]$response.StatusCode -ne 200) { return $false }
        if (-not $RequiredField) { return $true }
        $jsonResponse = $response.Content | ConvertFrom-Json
        return ($jsonResponse.$RequiredField -ceq $ExpectedValue)
    }
    catch { return $false }
}

function Get-PodmanReadOnlyStatus {
    $result = [ordered]@{
        machine = 'not-detected'
        worker  = 'unknown'
    }
    if (-not (Get-Command podman -ErrorAction SilentlyContinue)) { return $result }

    try {
        $raw = & podman machine list --format json 2>$null
        if ($LASTEXITCODE -ne 0) { return $result }
        $machines = @(($raw -join [Environment]::NewLine) | ConvertFrom-Json)
        $running = @($machines | Where-Object { $_.Running -eq $true })
        $result.machine = if ($running.Count -gt 0) { 'running' } else { 'stopped' }
        if ($running.Count -eq 0) { return $result }

        $workerOutput = & podman inspect --format '{{.State.Status}}' agentlab-worker 2>$null
        if ($LASTEXITCODE -ne 0) {
            $result.worker = 'not-found'
        }
        else {
            $result.worker = (($workerOutput -join '').Trim()).ToLowerInvariant()
            if (-not $result.worker) { $result.worker = 'unknown' }
        }
    }
    catch {
        # Reporting an unknown status is safer than assuming an unhealthy runtime is stopped.
        $result.worker = 'unknown'
    }
    return $result
}

function Get-HicStatus {
    $podman = Get-PodmanReadOnlyStatus
    $cp = Test-HttpReady 'http://127.0.0.1:8080/health' 'status' 'ok'
    $native = Test-HttpReady 'http://127.0.0.1:5248/v1/status' 'status' 'ready'
    $ui = Test-HttpReady 'http://127.0.0.1:5180/'
    return [ordered]@{
        action        = $Action
        controlPlane  = if ($cp) { 'ready' } else { 'unavailable' }
        nativeHost    = if ($native) { 'ready' } else { 'unavailable' }
        webUI         = if ($ui) { 'ready' } else { 'unavailable' }
        podmanMachine = $podman.machine
        worker        = $podman.worker
        workerSafe    = $podman.worker -in @('exited', 'stopped', 'created', 'configured', 'not-found')
    }
}

$status = Get-HicStatus
if ($Action -eq 'status' -or $Action -eq 'doctor') {
    if ($Json) {
        $status | ConvertTo-Json -Compress -Depth 4
    }
    else {
        Write-Host 'Human In Control — read-only status'
        Write-Host "Control Plane 8080 : $($status.controlPlane)"
        Write-Host "Native Host 5248   : $($status.nativeHost)"
        Write-Host "HIC Web UI 5180    : $($status.webUI)"
        Write-Host "Podman machine     : $($status.podmanMachine)"
        Write-Host "Worker             : $($status.worker)"
        if ($Action -eq 'doctor') {
            if ($status.controlPlane -ne 'ready') {
                Write-Warning 'Control Plane is not ready. Do NOT auto-start or replace containers. Finish the separately approved Issue #13 investigation/cutover.'
            }
            if (-not $status.workerSafe) {
                Write-Warning 'Worker is running or its state is unknown. Confirm it is stopped before enabling a new launcher session.'
            }
            if ($status.nativeHost -ne $status.webUI -and
                ($status.nativeHost -eq 'ready' -or $status.webUI -eq 'ready')) {
                Write-Warning 'Partial Native Host/Web UI session. Do not terminate or replace it automatically.'
            }
        }
    }
    exit 0
}

if ($Action -ne 'up') { throw 'Unsupported action.' }
if (-not $status.workerSafe) {
    throw "Worker status is $($status.worker). Refusing to launch without confirming it is stopped."
}
if ($status.controlPlane -ne 'ready') {
    throw 'Control Plane 8080 is unavailable. Guarded startup will not restart Podman, containers or databases. Run hic.cmd doctor and review Issue #13.'
}
if ($status.nativeHost -eq 'ready' -and $status.webUI -eq 'ready') {
    Write-Host 'HIC native session is already running. No action needed.'
    Write-Host 'Open http://127.0.0.1:5180'
    exit 0
}
if ($status.nativeHost -eq 'ready' -or $status.webUI -eq 'ready') {
    throw 'Only one of Native Host / HIC UI is running. Refusing to replace or terminate any existing process.'
}

$nativeLauncher = Join-Path $root 'scripts/hic-native.ps1'
if (-not (Test-Path $nativeLauncher)) { throw 'Native launcher script not found.' }
Write-Host 'Starting read-only Native Host and separate HIC Web UI on loopback.'
Write-Host 'Existing containers, data and Worker will not be touched.'
& $nativeLauncher
if (-not $?) { exit 1 }
