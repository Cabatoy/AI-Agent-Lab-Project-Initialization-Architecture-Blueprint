# Offline contract tests for HIC Launcher. Never invokes "up" or any mutating command.
$ErrorActionPreference = 'Stop'
$paths = @(
    (Join-Path $PSScriptRoot 'hic.ps1'),
    (Join-Path $PSScriptRoot 'hic-native.ps1'),
    (Join-Path $PSScriptRoot 'hic-ports.ps1'),
    (Join-Path $PSScriptRoot 'hic-decisions.ps1'),
    (Join-Path $PSScriptRoot 'hic-recovery.ps1')
)
foreach ($path in $paths) {
    $tokens = $null
    $errors = $null
    $null = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
    if ($errors.Count -gt 0) {
        throw "PowerShell syntax errors in $path : $($errors -join '; ')"
    }
}
$launcherFile = Join-Path (Join-Path $PSScriptRoot '..') 'hic.cmd'
if (-not (Test-Path $launcherFile)) { throw 'hic.cmd not found.' }
$launcher = Get-Content -Raw $launcherFile
if ($launcher -notmatch 'scripts\\hic\.ps1' -or $launcher -notmatch '\bup\b') {
    throw 'Expected launcher entry point was not found.'
}
foreach ($mode in @('status', 'doctor')) {
    $response = (& (Join-Path $PSScriptRoot 'hic.ps1') $mode -Json) | ConvertFrom-Json
    if ($response.action -ne $mode) { throw "Incorrect action returned for $mode." }
    foreach ($name in @('controlPlane', 'nativeHost', 'webUI', 'podmanMachine', 'worker', 'workerSafe')) {
        if ($null -eq $response.$name) { throw "Missing status field $name." }
    }
    if ($response.controlPlane -notin @('ready', 'unavailable') -or
        $response.nativeHost -notin @('ready', 'unavailable') -or
        $response.webUI -notin @('ready', 'unavailable')) {
        throw 'Unexpected status value.'
    }
    Write-Host "$mode read-only report: PASS"
}
Write-Host 'HIC launcher offline tests: PASS'

# The same per-machine overrides must be reflected by status and doctor.
$previous = @{}
foreach ($key in @('HIC_CONTROL_PLANE_PORT', 'HIC_NATIVE_PORT', 'HIC_WEB_PORT')) {
    $previous[$key] = [Environment]::GetEnvironmentVariable($key)
}
try {
    $env:HIC_CONTROL_PLANE_PORT = '20580'
    $env:HIC_NATIVE_PORT = '20581'
    $env:HIC_WEB_PORT = '20582'
    $result = (& (Join-Path $PSScriptRoot 'hic.ps1') status -Json) | ConvertFrom-Json
    if ($result.controlPlanePort -ne 20580 -or $result.nativeHostPort -ne 20581 -or $result.webUIPort -ne 20582) {
        throw 'Port overrides were not applied consistently.'
    }
    # Invalid cases must throw in the pure port resolver; do not invoke the launcher
    # as a child process because pwsh treats intentional failure as a fatal test error.
    . (Join-Path $PSScriptRoot 'hic-ports.ps1')
    $env:HIC_WEB_PORT = '20581'
    $rejected = $false
    try { $null = Get-HicPorts } catch { $rejected = $true }
    if (-not $rejected) { throw 'Duplicate HIC host ports must be rejected.' }
    $env:HIC_WEB_PORT = '70000'
    $rejected = $false
    try { $null = Get-HicPorts } catch { $rejected = $true }
    if (-not $rejected) { throw 'Out-of-range HIC ports must be rejected.' }
}
finally {
    foreach ($key in $previous.Keys) {
        [Environment]::SetEnvironmentVariable($key, $previous[$key])
    }
}
Write-Host 'HIC port overrides / collision validation: PASS'

# Pure decision matrix: does not start a Native Host or touch any local service.
. (Join-Path $PSScriptRoot 'hic-decisions.ps1')
$state = [ordered]@{
    controlPlane = 'unavailable'
    nativeHost = 'unavailable'
    webUI = 'unavailable'
    legacyNative = $false
    legacyWebUI = $false
    nativePortState = 'free'
    webUIPortState = 'free'
    worker = 'unknown'
}
$decision = Get-HicStartupDecision $state
if ($decision.action -ne 'launch-native') { throw 'Degraded mode must allow read-only native startup without a Control Plane.' }

$state.nativePortState = 'occupied'
if ((Get-HicStartupDecision $state).action -ne 'blocked') { throw 'An occupied port must block startup.' }
$state.nativePortState = 'unknown'
if ((Get-HicStartupDecision $state).action -ne 'blocked') { throw 'An unknown port must block startup.' }
$state.nativePortState = 'free'
$state.webUIPortState = 'occupied'
if ((Get-HicStartupDecision $state).action -ne 'blocked') { throw 'An occupied UI port must block startup.' }
$state.webUIPortState = 'free'
$state.nativeHost = 'ready'
if ((Get-HicStartupDecision $state).action -ne 'blocked') { throw 'A partial Native Host session must block startup.' }
$state.webUI = 'ready'
if ((Get-HicStartupDecision $state).action -ne 'reuse') { throw 'A complete session must be reused.' }
$state.nativeHost = 'unavailable'
$state.webUI = 'unavailable'
$state.legacyNative = $true
if ((Get-HicStartupDecision $state).action -ne 'blocked') { throw 'A partial legacy session must block startup.' }
$state.legacyWebUI = $true
if ((Get-HicStartupDecision $state).action -ne 'reuse-legacy') { throw 'A healthy legacy session must be reused.' }
Write-Host 'HIC startup decision matrix: PASS'


# Recovery plan tests: pure plan + binding classification with synthetic evidence only.
. (Join-Path $PSScriptRoot 'hic-recovery.ps1')
if ((Get-HicPortExposure @('127.0.0.1:19480')) -ne 'loopback-only') { throw 'Loopback-only binding was rejected.' }
if ((Get-HicPortExposure @('[::1]:19480')) -ne 'loopback-only') { throw 'IPv6 loopback-only binding was rejected.' }
if ((Get-HicPortExposure @('0.0.0.0:19480')) -ne 'non-loopback') { throw 'Public wildcard binding not blocked.' }
if ((Get-HicPortExposure @('127.0.0.1:19480', '0.0.0.0:19480')) -ne 'non-loopback') { throw 'Mixed binding not blocked.' }
if ((Get-HicPortExposure @('unknown')) -ne 'unverified') { throw 'Unparseable binding must fail closed.' }
if ((Get-HicPortExposure @()) -ne 'unverified') { throw 'Missing binding must fail closed.' }

$sampleState = [ordered]@{
    controlPlane = 'unavailable'
    controlPlanePort = 19480
    controlPlaneContainer = 'exited'
    postgresContainer = 'running'
    redisContainer = 'running'
    podmanMachine = 'running'
    worker = 'exited'
}
$sampleEvidence = [ordered]@{
    currentControlPlaneImageId = 'sha256:' + ('a' * 64)
    controlPlanePortExposure = 'loopback-only'
}
$plan = New-HicRecoveryPlan $sampleState $sampleEvidence
if ($plan.disposition -ne 'manual-preflight-required' -or $plan.restartAuthorized -ne $false) {
    throw 'Recovery must remain gated even when container state appears favorable.'
}
if ($plan.blockers.Count -lt 4) { throw 'Recovery evidence gates must not be omitted.' }
$sampleState.worker = 'running'
$plan = New-HicRecoveryPlan $sampleState $sampleEvidence
if ($plan.restartAuthorized -ne $false -or
    -not (@($plan.blockers | Where-Object { $_ -like '*Worker must be explicitly*' }).Count -eq 1)) {
    throw 'Running Worker must block recovery.'
}
$sampleState.controlPlane = 'ready'
$plan = New-HicRecoveryPlan $sampleState $sampleEvidence
if ($plan.disposition -ne 'no-recovery-needed' -or $plan.restartAuthorized -ne $false) {
    throw 'Healthy Control Plane must never trigger restart authorization.'
}
$report = (& (Join-Path $PSScriptRoot 'hic.ps1') recovery-plan -Json) | ConvertFrom-Json
if ($report.operation -ne 'recovery-plan' -or $report.restartAuthorized -ne $false -or
    $null -eq $report.blockers -or $null -eq $report.observed.controlPlane) {
    throw 'Recovery-plan command must emit a complete non-authorizing report.'
}
Write-Host 'HIC recovery-plan safety matrix: PASS'
