# Offline contract tests for HIC Launcher. Never invokes "up" or any mutating command.
$ErrorActionPreference = 'Stop'
$paths = @(
    (Join-Path $PSScriptRoot 'hic.ps1'),
    (Join-Path $PSScriptRoot 'hic-native.ps1'),
    (Join-Path $PSScriptRoot 'hic-ports.ps1'),
    (Join-Path $PSScriptRoot 'hic-decisions.ps1'),
    (Join-Path $PSScriptRoot 'hic-recovery.ps1'),
    (Join-Path $PSScriptRoot 'hic-managed-policy.ps1'),
    (Join-Path $PSScriptRoot 'hic-managed.ps1'),
    (Join-Path $PSScriptRoot 'hic-machine.ps1')
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


# Pure HIC-managed decision matrix: synthetic only; no Podman service started.
. (Join-Path $PSScriptRoot 'hic-managed-policy.ps1')
$virgin = [ordered]@{
    machine='running'
    discovery='verified'
    containers=@()
    volumes=@()
    manifestPresent=$false
    credentialsPresent=$false
    controlPlanePortState='free'
}
if ((Get-HicManagedDecision $virgin).action -ne 'bootstrap') { throw 'Fresh isolated Podman must be eligible for bootstrap.' }

$virgin.machine='stopped'
if ((Get-HicManagedDecision $virgin).action -ne 'skip') { throw 'Stopped Podman machine must not auto-start.' }
$virgin.machine='running'
$virgin.discovery='unverified'
if ((Get-HicManagedDecision $virgin).action -ne 'skip') { throw 'Unverified discovery must fail closed.' }
$virgin.discovery='verified'
$virgin.containers=@('agentlab-postgres')
if ((Get-HicManagedDecision $virgin).action -ne 'skip') { throw 'Legacy R2 container must prevent fresh installation.' }
$virgin.containers=@()
$virgin.volumes=@('agentlab_postgres-data')
if ((Get-HicManagedDecision $virgin).action -ne 'skip') { throw 'Legacy DB volume must prevent provisioning.' }
$virgin.volumes=@()
$virgin.controlPlanePortState='occupied'
if ((Get-HicManagedDecision $virgin).action -ne 'skip') { throw 'Occupied CP port must prevent provisioning.' }
$virgin.controlPlanePortState='free'
$virgin.containers=@('hic-managed-postgres')
if ((Get-HicManagedDecision $virgin).action -ne 'skip') { throw 'Partial managed install must fail closed.' }
$virgin.containers=@('hic-managed-postgres','hic-managed-redis','hic-managed-control-plane')
$virgin.volumes=@('hic-managed-postgres-data','hic-managed-redis-data')
$virgin.manifestPresent=$true
$virgin.credentialsPresent=$true
if ((Get-HicManagedDecision $virgin).action -ne 'resume') { throw 'Complete managed install should use verified resume path.' }
$virgin.manifestPresent=$false
if ((Get-HicManagedDecision $virgin).action -ne 'skip') { throw 'Managed containers without manifest must fail closed.' }

$manifestPath = Join-Path (Join-Path $PSScriptRoot '..') 'deploy/hic-managed/compose.yaml'
$composeText = Get-Content -Raw -LiteralPath $manifestPath
if ($composeText -match '(?m)^\s*agent-worker\s*:' -or
    $composeText -match '(?m)^\s*ports\s*:\s*\r?\n\s*-\s*"?5432:' -or
    $composeText -match '(?m)^\s*ports\s*:\s*\r?\n\s*-\s*"?6379:') {
    throw 'Managed Compose must not start Worker or publish database/cache ports.'
}
if ($composeText -notmatch '127\.0\.0\.1:' -or $composeText -notmatch 'HIC_DB_PASSWORD') {
    throw 'Managed Compose must use loopback CP binding and per-install credentials.'
}
Write-Host 'Managed single-command policy + Compose isolation: PASS'


# Cold-start is permitted only for exactly one known HIC-managed machine.
. (Join-Path $PSScriptRoot 'hic-machine.ps1')
$pinned = [pscustomobject]@{ version=3; project='hic-managed'; machineName='podman-machine-default' }
$single = @([pscustomobject]@{ Name='podman-machine-default'; Running=$false; UserModeNetworking=$false })
if ((Get-HicMachineStartDecision -Manifest $pinned -Machines $single -CredentialsPresent $true).action -ne 'start') {
    throw 'An exact previously pinned stopped machine should be eligible for one-command startup.'
}
$single[0].Running=$true
if ((Get-HicMachineStartDecision -Manifest $pinned -Machines $single -CredentialsPresent $true).action -ne 'noop') {
    throw 'An already-running pinned machine must not restart.'
}
$single[0].Running=$false
$single[0].UserModeNetworking=$true
if ((Get-HicMachineStartDecision -Manifest $pinned -Machines $single -CredentialsPresent $true).action -ne 'skip') {
    throw 'WSL user-mode networking must not auto-start.'
}
$single[0].UserModeNetworking=$false
$single[0].Name='other-machine'
if ((Get-HicMachineStartDecision -Manifest $pinned -Machines $single -CredentialsPresent $true).action -ne 'skip') {
    throw 'Machine name mismatch must refuse startup.'
}
$single[0].Name='podman-machine-default'
$other = @($single[0], [pscustomobject]@{Name='unrelated-machine';Running=$false;UserModeNetworking=$false})
if ((Get-HicMachineStartDecision -Manifest $pinned -Machines $other -CredentialsPresent $true).action -ne 'skip') {
    throw 'Multiple Podman machines must block unattended cold-start.'
}
if ((Get-HicMachineStartDecision -Manifest $pinned -Machines $single -CredentialsPresent $false).action -ne 'skip') {
    throw 'Missing saved credentials must block cold-start.'
}
$pinned.version=2
if ((Get-HicMachineStartDecision -Manifest $pinned -Machines $single -CredentialsPresent $true).action -ne 'skip') {
    throw 'Old/unsupported manifest version must block cold-start.'
}
Write-Host 'HIC pinned Podman machine cold-start policy: PASS'


# Regression: the user had a healthy HIC Native Host/UI, but "podman machine list"
# hung in an unresponsive WSL VM before the launcher could reuse that session.
# Verify the fast-path is reached before the Podman-using Get-HicStatus call.
$launcherScript = Get-Content -Raw (Join-Path $PSScriptRoot 'hic.ps1')
$fastPath = $launcherScript.IndexOf('$quickDecision = Get-HicStartupDecision $quickState')
$podmanStatusCall = $launcherScript.IndexOf('$status = Get-HicStatus')
if ($fastPath -lt 0 -or $podmanStatusCall -lt 0 -or $fastPath -ge $podmanStatusCall) {
    throw 'Pre-existing native session fast path must run before Podman status resolution.'
}
if ($launcherScript -notmatch "Skipping Podman/WSL inspection") {
    throw 'The fast path must explain when it skips WSL/Podman probing.'
}
Write-Host 'WSL frozen / existing HIC session fast-path ordering: PASS'
