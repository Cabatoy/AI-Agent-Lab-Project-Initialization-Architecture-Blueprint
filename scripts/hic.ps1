# Human In Control local launcher (phase 2: degraded native preview and read-only doctor).
# Never mutates Podman, DB, Docker volumes, Worker, or an existing process.
[CmdletBinding()]
param(
    [Parameter(Position=0)]
    [ValidateSet('up', 'status', 'doctor', 'recovery-plan')]
    [string]$Action = 'up',
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
if (Get-Variable PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $false
}
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
. (Join-Path $PSScriptRoot 'hic-ports.ps1')
. (Join-Path $PSScriptRoot 'hic-decisions.ps1')
. (Join-Path $PSScriptRoot 'hic-recovery.ps1')
. (Join-Path $PSScriptRoot 'hic-managed.ps1')
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

function Get-LoopbackPortState {
    param([int]$Port)
    try {
        # A conservative check: report any listener on the port, including wildcard.
        $listeners = [System.Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners()
        return $(if (@($listeners | Where-Object { $_.Port -eq $Port }).Count -gt 0) { 'occupied' } else { 'free' })
    }
    catch { return 'unknown' }
}

function Get-ContainerReadOnlyState {
    param([string]$ContainerName)
    try {
        $output = & podman inspect --format '{{.State.Status}}' $ContainerName 2>$null
        if ($LASTEXITCODE -ne 0) { return 'not-found' }
        $state = ($output -join '').Trim().ToLowerInvariant()
        if ($state -in @('running', 'paused', 'created', 'exited', 'stopped', 'dead')) { return $state }
    }
    catch { }
    return 'unknown'
}

function Get-PodmanReadOnlyStatus {
    $result = [ordered]@{
        machine = 'not-detected'
        worker = 'unknown'
        profile = 'unknown'
        controlPlaneContainer = 'unknown'
        postgresContainer = 'unknown'
        redisContainer = 'unknown'
    }
    if (-not (Get-Command podman -ErrorAction SilentlyContinue)) { return $result }

    try {
        $raw = & podman machine list --format json 2>$null
        if ($LASTEXITCODE -ne 0) { return $result }
        $machines = @(($raw -join [Environment]::NewLine) | ConvertFrom-Json)
        $running = @($machines | Where-Object { $_.Running -eq $true })
        $result.machine = if ($running.Count -gt 0) { 'running' } else { 'stopped' }
        if ($running.Count -eq 0) { return $result }

        $result.worker = Get-ContainerReadOnlyState 'agentlab-worker'
        $legacyCP = Get-ContainerReadOnlyState 'agentlab-control-plane'
        $legacyPostgres = Get-ContainerReadOnlyState 'agentlab-postgres'
        $legacyRedis = Get-ContainerReadOnlyState 'agentlab-redis'
        $managedCP = Get-ContainerReadOnlyState 'hic-managed-control-plane'
        $managedPostgres = Get-ContainerReadOnlyState 'hic-managed-postgres'
        $managedRedis = Get-ContainerReadOnlyState 'hic-managed-redis'
        $hasLegacy = @($legacyCP, $legacyPostgres, $legacyRedis, $result.worker) -ne 'not-found'
        $hasManaged = @($managedCP, $managedPostgres, $managedRedis) -ne 'not-found'
        $legacyPresent = @($hasLegacy | Where-Object { $_ }).Count -gt 0
        $managedPresent = @($hasManaged | Where-Object { $_ }).Count -gt 0
        if ($managedPresent -and -not $legacyPresent) {
            $result.profile = 'managed'
            $result.controlPlaneContainer = $managedCP
            $result.postgresContainer = $managedPostgres
            $result.redisContainer = $managedRedis
        }
        elseif ($managedPresent -and $legacyPresent) {
            $result.profile = 'mixed'
            $result.controlPlaneContainer = $legacyCP
            $result.postgresContainer = $legacyPostgres
            $result.redisContainer = $legacyRedis
        }
        else {
            $result.profile = if ($legacyPresent) { 'legacy' } else { 'none' }
            $result.controlPlaneContainer = $legacyCP
            $result.postgresContainer = $legacyPostgres
            $result.redisContainer = $legacyRedis
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
        infrastructureProfile = $podman.profile
        controlPlaneContainer = $podman.controlPlaneContainer
        postgresContainer = $podman.postgresContainer
        redisContainer = $podman.redisContainer
        nativePortState = Get-LoopbackPortState $ports.NativeHost
        webUIPortState = Get-LoopbackPortState $ports.WebUI
        worker           = $podman.worker
        workerSafe       = $podman.worker -in @('exited', 'stopped', 'created', 'configured', 'not-found')
    }
}

# Fast path MUST run before any Podman machine query. A frozen WSL subsystem
# must not hide an already healthy Native Host + Web UI session.
# This path NEVER starts or changes a service, container or Worker.
if ($Action -eq 'up') {
    $nativeReady = Test-HttpReady "http://127.0.0.1:$($ports.NativeHost)/v1/status" 'status' 'ready' 'hic-native-host'
    $uiReady = Test-HicWebUI $ports.WebUI
    $legacyNativeReady = Test-HttpReady 'http://127.0.0.1:5248/v1/status' 'status' 'ready' 'hic-native-host'
    $legacyUiReady = Test-HicWebUI 5180
    $quickState = [ordered]@{
        nativeHost = if ($nativeReady) { 'ready' } else { 'unavailable' }
        webUI = if ($uiReady) { 'ready' } else { 'unavailable' }
        legacyNative = $legacyNativeReady
        legacyWebUI = $legacyUiReady
        nativePortState = Get-LoopbackPortState $ports.NativeHost
        webUIPortState = Get-LoopbackPortState $ports.WebUI
    }
    $quickDecision = Get-HicStartupDecision $quickState
    if ($quickDecision.action -eq 'reuse') {
        Write-Host 'HIC native session is already running. Skipping Podman/WSL inspection.'
        Write-Host "Open http://127.0.0.1:$($ports.WebUI)"
        exit 0
    }
    if ($quickDecision.action -eq 'reuse-legacy') {
        Write-Host 'Existing 5248/5180 HIC preview is healthy. Skipping Podman/WSL inspection.'
        Write-Host 'Open http://127.0.0.1:5180. Control Plane health is not verified by this fast path.'
        exit 0
    }
    if ($quickDecision.action -eq 'blocked') {
        throw $quickDecision.reason
    }
}

$status = Get-HicStatus
$decision = Get-HicStartupDecision $status

function Get-DoctorFindings {
    param([System.Collections.IDictionary]$State, [pscustomobject]$Decision)
    $messages = [System.Collections.Generic.List[string]]::new()
    if ($State.controlPlane -ne 'ready') {
        $messages.Add('Control Plane is unavailable. Native Host/UI can still run in degraded read-only mode. Investigate existing runtime before any approved container restart.')
        if ($State.controlPlaneContainer -eq 'running') {
            $messages.Add('Control Plane container is running but host health failed: inspect port forwarding and logs without restarting.')
        }
        elseif ($State.controlPlaneContainer -eq 'exited') {
            $messages.Add('Control Plane container exited: inspect logs, backup and rollback image before proposing a restart.')
        }
    }
    if ($State.podmanMachine -eq 'stopped') {
        $messages.Add('Podman machine is stopped. Launcher never starts the Podman machine automatically.')
    }
    if ($State.infrastructureProfile -eq 'mixed') { $messages.Add('Mixed legacy and HIC-managed containers detected. Do not reconcile automatically.') }
    if ($State.postgresContainer -ne 'running' -or $State.redisContainer -ne 'running') {
        $messages.Add('PostgreSQL/Redis container availability is not confirmed. No database actions will be taken.')
    }
    if (-not $State.workerSafe) {
        $messages.Add("Worker state is $($State.worker). Native preview does not modify or interact with the Worker.")
    }
    if ($Decision.action -eq 'blocked') { $messages.Add($Decision.reason) }
    if ($Decision.action -eq 'reuse-legacy') { $messages.Add('Legacy Native Host/UI running at 5248/5180: no second session is started or migrated.') }
    return $messages.ToArray()
}

if ($Action -eq 'recovery-plan') {
    $evidence = Get-HicRecoveryEvidence $status
    $plan = New-HicRecoveryPlan $status $evidence
    if ($Json) {
        $plan | ConvertTo-Json -Depth 6
    }
    else {
        Write-Host 'HIC Infrastructure Recovery — read-only preflight'
        Write-Host "Disposition: $($plan.disposition)"
        Write-Host "Control Plane: $($plan.observed.controlPlane) on port $($plan.observed.controlPlaneHostPort)"
        Write-Host "Container CP/PG/Redis: $($plan.observed.controlPlaneContainer) / $($plan.observed.postgresContainer) / $($plan.observed.redisContainer)"
        Write-Host "Podman: $($plan.observed.podmanMachine); Worker: $($plan.observed.worker)"
        Write-Host "Control Plane binding: $($plan.observed.controlPlanePortExposure)"
        Write-Host "Current image ID: $($plan.observed.currentControlPlaneImageId)"
        Write-Host ''
        Write-Host 'BLOCKERS / EVIDENCE REQUIRED:'
        foreach ($blocker in $plan.blockers) { Write-Host "- $blocker" }
        Write-Host ''
        Write-Host $plan.nextStep
        Write-Host 'No restart was authorized or performed.'
    }
    exit 0
}

if ($Action -eq 'status' -or $Action -eq 'doctor') {
    if ($Action -eq 'doctor') { $status['findings'] = @(Get-DoctorFindings $status $decision) }
    if ($Json) {
        $status | ConvertTo-Json -Compress -Depth 4
    }
    else {
        Write-Host 'Human In Control — read-only status'
        Write-Host "Control Plane $($status.controlPlanePort) : $($status.controlPlane)"
        Write-Host "Native Host $($status.nativeHostPort)   : $($status.nativeHost)"
        Write-Host "HIC Web UI $($status.webUIPort)    : $($status.webUI)"
        Write-Host "Podman machine     : $($status.podmanMachine)"
        Write-Host "Infrastructure profile: $($status.infrastructureProfile)"
        if ($status.legacyControlPlane) { Write-Host "Existing legacy Control Plane verified at 127.0.0.1:8080 (no migration)." }
        if ($status.legacyNative -or $status.legacyWebUI) { Write-Host "Legacy Native preview detected on 5248/5180; not touching it." }
        Write-Host "Worker             : $($status.worker)"
        Write-Host "Containers (CP/PG/Redis): $($status.controlPlaneContainer) / $($status.postgresContainer) / $($status.redisContainer)"
        Write-Host "Configured Native/UI ports: $($status.nativePortState) / $($status.webUIPortState)"
        if ($Action -eq 'doctor') {
            foreach ($finding in $status.findings) { Write-Warning $finding }
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
if ($decision.action -eq 'blocked') {
    throw $decision.reason
}
# First try isolated fresh/resumable infrastructure; never touch legacy R2 resources.
# A healthy legacy CP is reused, and old Native preview sessions are not replaced.
if ($status.controlPlane -ne 'ready' -and $decision.action -ne 'reuse-legacy') {
    try {
        $managedResult = Start-HicManagedInfrastructure -Status $status -ControlPlanePort $ports.ControlPlane
        if ($managedResult.outcome -eq 'ready') {
            Write-Host $managedResult.reason
            $status = Get-HicStatus
            $decision = Get-HicStartupDecision $status
            if ($status.controlPlane -ne 'ready') {
                Write-Warning 'Managed infrastructure returned but Control Plane health is not ready.'
            }
        }
        else {
            Write-Warning "Managed infrastructure unchanged: $($managedResult.reason)"
        }
    }
    catch {
        Write-Warning "Managed infrastructure not ready: $($_.Exception.Message)"
        Write-Warning 'Partial new-installation resources, if any, are left untouched for safe investigation.'
    }
}
if ($decision.action -eq 'reuse') {
    Write-Host 'HIC native session is already running. No action needed.'
    Write-Host "Open http://127.0.0.1:$($status.webUIPort)"
    exit 0
}
if ($decision.action -eq 'reuse-legacy') {
    Write-Host 'Existing native preview on 5248/5180 is running. No second Native Host started.'
    Write-Host 'Open http://127.0.0.1:5180. No automatic port migration.'
    exit 0
}
if ($status.controlPlane -ne 'ready') {
    Write-Warning 'Control Plane unavailable. Opening HIC in read-only degraded mode; Tasks/Plans will show unavailable until CP recovery is separately approved.'
}
if (-not $status.workerSafe) {
    Write-Warning "Worker state is $($status.worker). Native preview is read-only and will not interact with it."
}
# Pass an identity-verified legacy CP port (or configured new port) to the Native UI proxy.
$env:HIC_CONTROL_PLANE_PORT = [string]$status.controlPlanePort

$nativeLauncher = Join-Path $root 'scripts/hic-native.ps1'
if (-not (Test-Path $nativeLauncher)) { throw 'Native launcher script not found.' }
Write-Host 'Starting read-only Native Host and separate HIC Web UI on loopback.'
Write-Host 'Podman, containers, data, running UI, and Worker will not be touched.'
& $nativeLauncher
if (-not $?) { exit 1 }
