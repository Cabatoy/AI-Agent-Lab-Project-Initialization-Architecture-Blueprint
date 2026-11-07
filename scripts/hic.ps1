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
. (Join-Path $PSScriptRoot 'hic-ports.ps1')
$ports = Get-HicPorts

function Test-HttpReady {
    param([string]$Uri, [string]$RequiredField, [string]$ExpectedValue, [string]$ExpectedService = '')
    try {
        $response = Invoke-WebRequest -Uri $Uri -Method Get -TimeoutSec 2 -UseBasicParsing -ErrorAction Stop
        if ([int]$response.StatusCode -ne 200) { return $false }
        if (-not $RequiredField) { return $true }
        $jsonResponse = $response.Content | ConvertFrom-Json
        return (($jsonResponse.$RequiredField -ceq $ExpectedValue) -and
                ((-not $ExpectedService) -or ($jsonResponse.service -ceq $ExpectedService)))
    }
    catch { return $false }
}

function Test-HicWebUI {
    param([int]$Port)
    try {
        $response = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/" -Method Get -TimeoutSec 2 -UseBasicParsing -ErrorAction Stop
        return ([int]$response.StatusCode -eq 200 -and
            $response.Content -like '*<title>HIC | Control Dashboard</title>*')
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
    $cpPort = $ports.ControlPlane
    $cp = Test-HttpReady "http://127.0.0.1:$cpPort/health" 'status' 'ok' 'control-plane'
    $legacyCP = $false
    if (-not $cp -and -not $env:HIC_CONTROL_PLANE_PORT -and $cpPort -ne 8080) {
        # Preserve installed 8080 runtimes, but only when the service identity matches HIC.
        $legacyCP = Test-HttpReady 'http://127.0.0.1:8080/health' 'status' 'ok' 'control-plane'
        if ($legacyCP) { $cp = $true; $cpPort = 8080 }
    }

    $native = Test-HttpReady "http://127.0.0.1:$($ports.NativeHost)/v1/status" 'status' 'ready' 'hic-native-host'
    $ui = Test-HicWebUI $ports.WebUI
    # Existing PR #15 preview can remain on its old ports. Never replace it implicitly.
    $oldNative = Test-HttpReady 'http://127.0.0.1:5248/v1/status' 'status' 'ready' 'hic-native-host'
    $oldUI = Test-HicWebUI 5180
    return [ordered]@{
        action           = $Action
        controlPlane     = if ($cp) { 'ready' } else { 'unavailable' }
        controlPlanePort = $cpPort
        legacyControlPlane = $legacyCP
        nativeHost       = if ($native) { 'ready' } else { 'unavailable' }
        nativeHostPort   = $ports.NativeHost
        webUI            = if ($ui) { 'ready' } else { 'unavailable' }
        webUIPort        = $ports.WebUI
        legacyNative     = $oldNative
        legacyWebUI      = $oldUI
        podmanMachine    = $podman.machine
        worker           = $podman.worker
        workerSafe       = $podman.worker -in @('exited', 'stopped', 'created', 'configured', 'not-found')
    }
}

$status = Get-HicStatus
if ($Action -eq 'status' -or $Action -eq 'doctor') {
    if ($Json) {
        $status | ConvertTo-Json -Compress -Depth 4
    }
    else {
        Write-Host 'Human In Control — read-only status'
        Write-Host "Control Plane $($status.controlPlanePort) : $($status.controlPlane)"
        Write-Host "Native Host $($status.nativeHostPort)   : $($status.nativeHost)"
        Write-Host "HIC Web UI $($status.webUIPort)    : $($status.webUI)"
        Write-Host "Podman machine     : $($status.podmanMachine)"
        if ($status.legacyControlPlane) { Write-Host "Existing legacy Control Plane verified at 127.0.0.1:8080 (no migration)." }
        if ($status.legacyNative -or $status.legacyWebUI) { Write-Host "Legacy Native preview detected on 5248/5180; not touching it." }
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
    throw "Control Plane on configured port $($status.controlPlanePort) is unavailable. No container restart or automatic port migration. Run hic.cmd doctor and review Issue #13."
}
if ($status.nativeHost -eq 'ready' -and $status.webUI -eq 'ready') {
    Write-Host 'HIC native session is already running. No action needed.'
    Write-Host "Open http://127.0.0.1:$($status.webUIPort)"
    exit 0
}
if ($status.nativeHost -eq 'ready' -or $status.webUI -eq 'ready') {
    throw 'Only one of Native Host / HIC UI is running. Refusing to replace or terminate any existing process.'
}
if ($status.legacyNative -or $status.legacyWebUI) {
    if ($status.legacyNative -and $status.legacyWebUI) {
        Write-Host 'Existing native preview on 5248/5180 is running. No second Native Host started.'
        Write-Host 'Open http://127.0.0.1:5180. Change ports only after an independently approved manual cutover.'
        exit 0
    }
    throw 'Partial legacy Native Host/UI session detected. Refusing to start a second native process.'
}
# Share the selected, identity-verified legacy or configured CP port with the UI proxy.
$env:HIC_CONTROL_PLANE_PORT = [string]$status.controlPlanePort

$nativeLauncher = Join-Path $root 'scripts/hic-native.ps1'
if (-not (Test-Path $nativeLauncher)) { throw 'Native launcher script not found.' }
Write-Host 'Starting read-only Native Host and separate HIC Web UI on loopback.'
Write-Host 'Existing containers, data and Worker will not be touched.'
& $nativeLauncher
if (-not $?) { exit 1 }
