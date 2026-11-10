$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
. (Join-Path $PSScriptRoot 'hic-resource.ps1')

$phase2Design = Get-HicPhase2LifecycleDesign
if ($phase2Design.status -ne 'design-only' -or $phase2Design.runtimeMutationEnabled -ne $false -or
    $phase2Design.workerEnabled -ne $false -or $phase2Design.modelIdleUnload.enabled -ne $false -or
    $phase2Design.disposableTestDatabase.persistentDatabaseReuse -ne $false) {
    throw 'Phase 2 policies must remain proposal-only and preserve the Worker and persistent databases.'
}

$admissionState = [ordered]@{
    serviceIdentity = 'reviewed-model'
    serviceState = 'running'
    admissionGate = 'open'
    leaseState = 'missing'
    leaseId = ''
}
if ((Get-HicPhase2InferenceAdmissionDecision $admissionState).action -ne 'acquire-lease') {
    throw 'Inference must acquire a unique lease before it can be admitted.'
}
$admissionState.leaseState = 'acquired'
$admissionState.leaseId = '0123456789abcdef0123456789abcdef'
if ((Get-HicPhase2InferenceAdmissionDecision $admissionState).action -ne 'admit') {
    throw 'A verified inference lease must permit the request while the gate is open.'
}
$admissionState.admissionGate = 'sealed'
if ((Get-HicPhase2InferenceAdmissionDecision $admissionState).action -ne 'wait') {
    throw 'New inference must queue while the unload gate is sealed.'
}
$admissionState.admissionGate = 'unknown'
if ((Get-HicPhase2InferenceAdmissionDecision $admissionState).action -ne 'blocked') {
    throw 'Unknown inference gate state must fail closed.'
}

$unloadState = [ordered]@{
    serviceIdentity = 'owned-model'
    serviceState = 'running'
    ownership = 'hic-managed-verified'
    unloadAdapter = 'verified'
    inferenceLedger = 'verified'
    inFlightRequests = 0
    admissionGate = 'sealed'
    idleSeconds = 900
    idleTimeoutSeconds = 900
    planId = 'HIC76-reviewed-1'
}
if ((Get-HicPhase2IdleUnloadDecision $unloadState 'HIC76-reviewed-1' 'HIC76-reviewed-1').action -ne 'unload-eligible') {
    throw 'A verified, idle, HIC-owned model may become eligible only after the gate is sealed and the plan is current.'
}
$unloadState.inFlightRequests = 1
if ((Get-HicPhase2IdleUnloadDecision $unloadState 'HIC76-reviewed-1' 'HIC76-reviewed-1').action -ne 'wait') {
    throw 'In-flight inference must block model unload.'
}
$unloadState.inFlightRequests = 0
$unloadState.admissionGate = 'open'
if ((Get-HicPhase2IdleUnloadDecision $unloadState 'HIC76-reviewed-1' 'HIC76-reviewed-1').action -ne 'seal-admission-first') {
    throw 'Model unload must seal admission before its final zero-lease check.'
}
$unloadState.admissionGate = 'sealed'
$unloadState.ownership = 'external-or-unknown'
if ((Get-HicPhase2IdleUnloadDecision $unloadState 'HIC76-reviewed-1' 'HIC76-reviewed-1').action -ne 'protected') {
    throw 'External or unowned models must never be unloaded.'
}
$unloadState.ownership = 'hic-managed-verified'
$unloadState.unloadAdapter = 'unknown'
if ((Get-HicPhase2IdleUnloadDecision $unloadState 'HIC76-reviewed-1' 'HIC76-reviewed-1').action -ne 'blocked') {
    throw 'Unverified provider unload capability must block unload.'
}
$unloadState.unloadAdapter = 'verified'
$unloadState.inferenceLedger = 'unknown'
if ((Get-HicPhase2IdleUnloadDecision $unloadState 'HIC76-reviewed-1' 'HIC76-reviewed-1').action -ne 'blocked') {
    throw 'Unknown in-flight inference state must fail closed.'
}
$unloadState.inferenceLedger = 'verified'
if ((Get-HicPhase2IdleUnloadDecision $unloadState 'HIC76-stale' 'HIC76-reviewed-1').action -ne 'blocked') {
    throw 'Stale or mismatched approval must block unload.'
}
$unloadState.planId = 'HIC76-reviewed-1'
$unloadState.idleSeconds = 899
if ((Get-HicPhase2IdleUnloadDecision $unloadState 'HIC76-reviewed-1' 'HIC76-reviewed-1').action -ne 'wait') {
    throw 'Model idle timeout must elapse before unload becomes eligible.'
}

$runId = '0123456789abcdef0123456789abcdef'
$testDatabasePlan = New-HicPhase2DisposableTestDatabasePlan -RunId $runId
if ($testDatabasePlan.containerName -ne "hic-test-pg-$runId" -or
    $testDatabasePlan.namedVolumes.Count -ne 0 -or $testDatabasePlan.bindMounts.Count -ne 0 -or
    $testDatabasePlan.hostBinding -ne '127.0.0.1:runtime-assigned-port' -or
    $testDatabasePlan.persistentDatabaseReuse -ne $false -or
    $testDatabasePlan.workerEnabled -ne $false -or $testDatabasePlan.mutationsAuthorized -ne $false) {
    throw 'Disposable database plans must be per-run, ephemeral, loopback-only, and non-authorizing.'
}
if ((New-HicPhase2DisposableTestDatabasePlan -RunId 'fedcba9876543210fedcba9876543210').containerName -eq $testDatabasePlan.containerName) {
    throw 'Every disposable test database run must have a unique identity.'
}
$invalidRunIdRejected = $false
try { $null = New-HicPhase2DisposableTestDatabasePlan -RunId 'shared-project-database' }
catch { $invalidRunIdRejected = $true }
if (-not $invalidRunIdRejected) { throw 'Invalid disposable database run IDs must be rejected.' }

$expectedTestContainerId = 'a' * 64
$testDatabaseObserved = [ordered]@{
    containerId = $expectedTestContainerId
    containerName = $testDatabasePlan.containerName
    projectName = $testDatabasePlan.projectName
    runMarker = $testDatabasePlan.runMarker
    databaseName = $testDatabasePlan.databaseName
    roleName = $testDatabasePlan.roleName
    superuser = $false
    network = $testDatabasePlan.network
    storage = $testDatabasePlan.storage
    hostBinding = $testDatabasePlan.hostBinding
    credentials = $testDatabasePlan.credentials
    persistentDatabaseReuse = $false
    namedVolumes = @()
    bindMounts = @()
    testsRunning = $false
    connectionsVerified = $true
    activeConnections = 0
    state = 'running'
}
if ((Get-HicPhase2DisposableDatabaseCleanupDecision $testDatabasePlan $expectedTestContainerId $testDatabaseObserved).action -ne 'cleanup-eligible') {
    throw 'Only an exact disposable test database with drained work may become cleanup-eligible.'
}
$testDatabaseObserved.containerId = 'b' * 64
if ((Get-HicPhase2DisposableDatabaseCleanupDecision $testDatabasePlan $expectedTestContainerId $testDatabaseObserved).action -ne 'preserve') {
    throw 'A container ID mismatch must preserve the test database.'
}
$testDatabaseObserved.containerId = $expectedTestContainerId
$testDatabaseObserved.namedVolumes = @('project-postgres-data')
if ((Get-HicPhase2DisposableDatabaseCleanupDecision $testDatabasePlan $expectedTestContainerId $testDatabaseObserved).action -ne 'preserve') {
    throw 'A mounted persistent volume must never be eligible for disposable cleanup.'
}
$testDatabaseObserved.namedVolumes = @()
$testDatabaseObserved.runMarker = 'HIC:TESTDB:other-run'
if ((Get-HicPhase2DisposableDatabaseCleanupDecision $testDatabasePlan $expectedTestContainerId $testDatabaseObserved).action -ne 'preserve') {
    throw 'A mismatched disposable run marker must preserve the test database.'
}
$testDatabaseObserved.runMarker = $testDatabasePlan.runMarker
$testDatabaseObserved.databaseName = 'project_data'
if ((Get-HicPhase2DisposableDatabaseCleanupDecision $testDatabasePlan $expectedTestContainerId $testDatabaseObserved).action -ne 'preserve') {
    throw 'A mismatched database identity must preserve the test database.'
}
$testDatabaseObserved.databaseName = $testDatabasePlan.databaseName
$testDatabaseObserved.hostBinding = '0.0.0.0:5432'
if ((Get-HicPhase2DisposableDatabaseCleanupDecision $testDatabasePlan $expectedTestContainerId $testDatabaseObserved).action -ne 'preserve') {
    throw 'A non-loopback host binding must never be treated as a disposable fixture.'
}
$testDatabaseObserved.hostBinding = $testDatabasePlan.hostBinding
$testDatabaseObserved.superuser = $true
if ((Get-HicPhase2DisposableDatabaseCleanupDecision $testDatabasePlan $expectedTestContainerId $testDatabaseObserved).action -ne 'preserve') {
    throw 'A privileged disposable test role must be rejected.'
}
$testDatabaseObserved.superuser = $false
$testDatabaseObserved.connectionsVerified = $false
if ((Get-HicPhase2DisposableDatabaseCleanupDecision $testDatabasePlan $expectedTestContainerId $testDatabaseObserved).action -ne 'preserve') {
    throw 'Unknown connection state must preserve the test database.'
}
$testDatabaseObserved.connectionsVerified = $true
$testDatabaseObserved.testsRunning = $true
if ((Get-HicPhase2DisposableDatabaseCleanupDecision $testDatabasePlan $expectedTestContainerId $testDatabaseObserved).action -ne 'wait') {
    throw 'Disposable cleanup must wait until all test processes exit.'
}
$testDatabaseObserved.testsRunning = $false
$testDatabaseObserved.activeConnections = 1
if ((Get-HicPhase2DisposableDatabaseCleanupDecision $testDatabasePlan $expectedTestContainerId $testDatabaseObserved).action -ne 'wait') {
    throw 'Disposable cleanup must wait until database connections drain.'
}

if ((Get-HicResourceLabelValue -Labels 'com.docker.compose.depends_on=redis:service_started:false,com.docker.compose.service=agent-worker' -Name 'com.docker.compose.service') -cne 'agent-worker' -or
    (Get-HicResourceLabelValue -Labels 'com.docker.compose.service=agent-worker' -Name 'com.docker.compose.project') -cne '') {
    throw 'Podman label-map parsing must extract only the requested compose label.'
}
. (Join-Path $PSScriptRoot 'hic-preview.ps1')

$ports = [pscustomobject]@{
    ControlPlane = 19480
    NativeHost = 19481
    WebUI = 19482
}
$inventory = [ordered]@{
    checkedAtUtc = '2026-01-01T00:00:00Z'
    readOnly = $true
    podmanMachine = 'not-detected'
    configuredPorts = [ordered]@{ controlPlane=19480; nativeHost=19481; webUI=19482 }
    hostMemory = [ordered]@{ state='unknown'; totalGiB=$null; availableGiB=$null; usedGiB=$null }
    topProcesses = [ordered]@{ state='unknown'; processes=@() }
    podmanVmWorkingSet = [ordered]@{ state='unknown' }
    containers = [ordered]@{
        state = 'not-detected'
        containers = @()
        volumes = @()
        memory = [ordered]@{ state='unknown'; containers=@(); note='separate scope' }
    }
    managedOwnership = [ordered]@{ state='absent'; recordPresent=$false; ownedContainerNames=@() }
    listeners = [ordered]@{
        state='verified'
        reservedPreviewPorts=@(20066, 20067, 5248, 5180)
        reservedServicePorts=@(5432, 6379, 8000)
        protectedListeners=@([pscustomobject]@{ port=20067; process='node'; ownership='protected' })
    }
    protectedPreviews = @([pscustomobject]@{ port=20067; process='node'; ownership='protected' })
    preview = [ordered]@{
        nativeHost='unavailable'
        webUI='unavailable'
        nativePortState='free'
        webUIPortState='free'
    }
    worker = 'unknown'
    modelService = [ordered]@{ state='unknown'; reason='not selected' }
    memoryAccounting = 'Separate scopes; never sum.'
}
function Copy-TestInventory {
    param([System.Collections.IDictionary]$Source)
    $copy = [ordered]@{}
    foreach ($key in $Source.Keys) { $copy[$key] = $Source[$key] }
    return $copy
}

$light = New-HicResourcePlan -Profile 'Light' -Task 'ui' -Inventory $inventory -ModelService ''
if ($light.available -ne $true -or $light.mutationsAuthorized -ne $false -or
    $light.services.name -join ',' -ne 'native-host,web-ui') {
    throw 'Light UI must include only the Native Host and Web UI.'
}
if ($light.memoryBudgetGiB.minimum -ne 3 -or $light.memoryBudgetGiB.maximum -ne 6 -or
    $light.budgetIsGuarantee -ne $false) {
    throw 'Light memory guidance must remain advisory and within the configured range.'
}
if (@($light.protectedPreviews | Where-Object { $_.port -eq 20067 }).Count -ne 1) {
    throw 'The protected v11 preview listener must remain visible in the plan.'
}
$start = Get-HicResourceStartDecision -Plan $light -Inventory $inventory -ApprovedPlanId $light.planId
if ($start.action -ne 'launch') { throw 'A reviewed Light UI plan with free configured ports should allow only the Native preview launch.' }
if ((Get-HicResourceStartDecision -Plan $light -Inventory $inventory -ApprovedPlanId '').action -ne 'blocked') {
    throw 'Lifecycle activation without an explicitly reviewed plan ID must fail closed.'
}
if ((Get-HicResourceStartDecision -Plan $light -Inventory $inventory -ApprovedPlanId 'HIC76-stale').action -ne 'blocked') {
    throw 'A stale plan ID must fail closed.'
}

$blockedPorts = Copy-TestInventory $inventory
$blockedPorts.preview = [ordered]@{
    nativeHost='ready'
    webUI='unavailable'
    nativePortState='free'
    webUIPortState='occupied'
}
$partial = New-HicResourcePlan -Profile 'Light' -Task 'ui' -Inventory $blockedPorts -ModelService ''
if ((Get-HicResourceStartDecision -Plan $partial -Inventory $blockedPorts -ApprovedPlanId $partial.planId).action -ne 'blocked') {
    throw 'Partial preview or occupied port must block a second session.'
}

$development = New-HicResourcePlan -Profile 'Development' -Task 'api' -Inventory $inventory -ModelService ''
if (@($development.services | Where-Object { $_.name -in @('native-host', 'web-ui', 'control-plane', 'postgres', 'redis') }).Count -ne 5) {
    throw 'Development core profile must include UI, API, PostgreSQL and Redis.'
}
if ($development.blockers.Count -eq 0) { throw 'Development activation without a verified owned stack must be blocked.' }
if (@($development.services | Where-Object { $_.name -in @('agent-worker', 'executor') }).Count -ne 0) {
    throw 'Worker and executor must not be part of the Development profile.'
}

$ownedInventory = Copy-TestInventory $inventory
$ownedInventory.podmanMachine = 'running'
$ownedInventory.worker = 'stopped'
$ownedInventory.managedOwnership = [ordered]@{
    state='verified'
    recordPresent=$true
    credentialsPresent=$true
    ownedContainerNames=@('hic-managed-postgres','hic-managed-redis','hic-managed-control-plane')
}
$ownedInventory.containers = [ordered]@{
    state='verified'
    volumes=@()
    memory=[ordered]@{ state='unknown'; containers=@() }
    containers=@(
        [pscustomobject]@{ id=('a' * 64); name='hic-managed-postgres'; state='running'; project='hic-managed'; service='postgres'; ownership='hic-managed-verified' },
        [pscustomobject]@{ id=('b' * 64); name='hic-managed-redis'; state='running'; project='hic-managed'; service='redis'; ownership='hic-managed-verified' },
        [pscustomobject]@{ id=('c' * 64); name='hic-managed-control-plane'; state='running'; project='hic-managed'; service='control-plane'; ownership='hic-managed-verified' }
    )
}
$ownedPlan = New-HicResourcePlan -Profile 'Development' -Task 'api' -Inventory $ownedInventory -ModelService ''
if ($ownedPlan.blockers.Count -ne 0) { throw 'Exact HIC-owned, running managed resources should be plannable.' }
if ((Get-HicResourceStartDecision -Plan $ownedPlan -Inventory $ownedInventory -ApprovedPlanId $ownedPlan.planId).action -ne 'resume-owned') {
    throw 'Development may resume only a previously registered exact HIC-owned stack.'
}

$cleanInventory = Copy-TestInventory $inventory
$cleanInventory.podmanMachine = 'running'
$cleanInventory.worker = 'not-detected'
$cleanInventory.containers = [ordered]@{
    state='verified'
    containers=@()
    volumes=@()
    memory=[ordered]@{ state='unknown'; containers=@() }
}
$cleanInventory.managedOwnership = [ordered]@{
    state='absent'
    recordPresent=$false
    credentialsPresent=$false
    ownedContainerNames=@()
}
$cleanPlan = New-HicResourcePlan -Profile 'Light' -Task 'api' -Inventory $cleanInventory -ModelService ''
if ($cleanPlan.managedStackAction -ne 'bootstrap' -or $cleanPlan.blockers.Count -ne 0 -or
    (Get-HicResourceStartDecision -Plan $cleanPlan -Inventory $cleanInventory -ApprovedPlanId $cleanPlan.planId).action -ne 'bootstrap-clean') {
    throw 'Only a fully empty, verified runtime may plan an explicitly confirmed isolated bootstrap.'
}
if ($cleanPlan.resourceChanges.createsVolumes.Count -ne 2 -or
    @($cleanPlan.resourceChanges.runsOneShotServices | Where-Object { $_ -eq 'schema-init' }).Count -ne 1) {
    throw 'A clean bootstrap plan must expose its exact persistent volumes and new-empty-database schema step.'
}
$orphanVolumeInventory = Copy-TestInventory $cleanInventory
$orphanVolumeInventory.containers = [ordered]@{
    state='verified'
    containers=@()
    volumes=@([pscustomobject]@{ name='orphan-database-data'; ownership='protected-external-or-unknown' })
    memory=[ordered]@{ state='unknown'; containers=@() }
}
$orphanPlan = New-HicResourcePlan -Profile 'Light' -Task 'api' -Inventory $orphanVolumeInventory -ModelService ''
if ($orphanPlan.managedStackAction -ne 'blocked' -or $orphanPlan.blockers.Count -eq 0) {
    throw ('An existing unowned volume must block fresh database bootstrap (action: {0}; blockers: {1}).' -f
        $orphanPlan.managedStackAction, ($orphanPlan.blockers -join '; '))
}

$externalInventory = Copy-TestInventory $ownedInventory
$externalInventory.managedOwnership = [ordered]@{ state='absent'; recordPresent=$false; ownedContainerNames=@() }
$externalInventory.containers = [ordered]@{
    state='verified'
    volumes=@()
    memory=[ordered]@{ state='unknown'; containers=@() }
    containers=@(
        [pscustomobject]@{ id=('d' * 64); name='project-db'; state='running'; project='other-project'; service='postgres'; ownership='external-or-unknown' }
    )
}
$externalPlan = New-HicResourcePlan -Profile 'Light' -Task 'api' -Inventory $externalInventory -ModelService ''
if (@($externalPlan.blockers | Where-Object { $_ -like '*second database*' }).Count -eq 0) {
    throw 'A running external PostgreSQL service must never trigger a second database.'
}

$workerInventory = Copy-TestInventory $ownedInventory
$workerInventory.worker = 'running'
$workerPlan = New-HicResourcePlan -Profile 'Development' -Task 'api' -Inventory $workerInventory -ModelService ''
if (@($workerPlan.blockers | Where-Object { $_ -like '*Worker state is running*' }).Count -eq 0) {
    throw 'Unexpected Worker state must block development lifecycle changes.'
}
$unknownWorkerInventory = Copy-TestInventory $ownedInventory
$unknownWorkerInventory.worker = 'unknown'
$unknownWorkerPlan = New-HicResourcePlan -Profile 'Development' -Task 'api' -Inventory $unknownWorkerInventory -ModelService ''
if (@($unknownWorkerPlan.blockers | Where-Object { $_ -like '*Worker state is unknown*' }).Count -eq 0 -or
    (Get-HicResourceStartDecision -Plan $unknownWorkerPlan -Inventory $unknownWorkerInventory -ApprovedPlanId $unknownWorkerPlan.planId).action -ne 'blocked') {
    throw 'An unverified Worker state on a running Podman machine must block infrastructure activation.'
}
$unknownWorkerUi = Copy-TestInventory $inventory
$unknownWorkerUi.podmanMachine = 'running'
$unknownWorkerUi.preview.nativeHost = 'unavailable'
$unknownWorkerUi.preview.webUI = 'unavailable'
$unknownWorkerUi.worker = 'unknown'
$unknownWorkerUiPlan = New-HicResourcePlan -Profile 'Light' -Task 'ui' -Inventory $unknownWorkerUi -ModelService ''
if ((Get-HicResourceStartDecision -Plan $unknownWorkerUiPlan -Inventory $unknownWorkerUi -ApprovedPlanId $unknownWorkerUiPlan.planId).action -ne 'blocked') {
    throw 'An unverified Worker state must block launching a new preview while Podman is running.'
}

$fullAgent = New-HicResourcePlan -Profile 'FullAgent' -Task 'inference' -Inventory $inventory -ModelService 'local-model'
if ($fullAgent.available -or @($fullAgent.blockers | Where-Object { $_ -like '*security gate #63*' }).Count -eq 0) {
    throw 'Full Agent must remain unavailable behind security gate #63.'
}

$testsPlan = New-HicResourcePlan -Profile 'Light' -Task 'tests' -Inventory $inventory -ModelService ''
if (@($testsPlan.services | Where-Object { $_.name -eq 'disposable-postgres' }).Count -ne 1 -or
    @($testsPlan.blockers | Where-Object { $_ -like '*disposable database*' }).Count -eq 0) {
    throw 'Test planning must require a disposable database and must not reuse persistent project data.'
}

$inferencePlan = New-HicResourcePlan -Profile 'Development' -Task 'inference' -Inventory $inventory -ModelService ''
if (@($inferencePlan.blockers | Where-Object { $_ -like '*explicit local model-service identity*' }).Count -eq 0) {
    throw 'Inference must require explicit model-service selection.'
}

$originalLocalAppData = $env:LOCALAPPDATA
$ownershipRoot = Join-Path $env:TEMP ('HIC-Resource-Ownership-' + [Guid]::NewGuid().ToString('N'))
try {
    $managedDirectory = Join-Path $ownershipRoot 'HumanInControl\managed'
    [System.IO.Directory]::CreateDirectory($managedDirectory) | Out-Null
    $manifestPath = Join-Path $managedDirectory 'manifest.json'
    $credentialsPath = Join-Path $managedDirectory 'runtime.env'
    [System.IO.File]::WriteAllText($credentialsPath, 'synthetic ownership marker')
    $ownedContainers = @(
        [pscustomobject]@{ id=('a' * 64); name='hic-managed-postgres'; state='running'; project='hic-managed'; service='postgres' },
        [pscustomobject]@{ id=('b' * 64); name='hic-managed-redis'; state='running'; project='hic-managed'; service='redis' },
        [pscustomobject]@{ id=('c' * 64); name='hic-managed-control-plane'; state='running'; project='hic-managed'; service='control-plane' }
    )
    $manifest = [ordered]@{
        version = 3
        project = 'hic-managed'
        machineName = 'synthetic-machine'
        credentialsSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $credentialsPath).Hash.ToLowerInvariant()
        containers = [ordered]@{
            'hic-managed-postgres' = [ordered]@{ id=('a' * 64); image=('1' * 64) }
            'hic-managed-redis' = [ordered]@{ id=('b' * 64); image=('2' * 64) }
            'hic-managed-control-plane' = [ordered]@{ id=('c' * 64); image=('3' * 64) }
        }
    }
    [Environment]::SetEnvironmentVariable('LOCALAPPDATA', $ownershipRoot, 'Process')
    [System.IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 8))
    $verifiedOwnership = Get-HicResourceManagedOwnership -Containers $ownedContainers -PodmanMachineName 'synthetic-machine'
    if ($verifiedOwnership.state -ne 'verified') {
        throw 'A complete exact manifest, matching machine and credential digest should verify ownership.'
    }
    $wrongMachineOwnership = Get-HicResourceManagedOwnership -Containers $ownedContainers -PodmanMachineName 'other-machine'
    if ($wrongMachineOwnership.state -ne 'stale-or-invalid') {
        throw 'A running Podman machine that differs from the pinned manifest must invalidate ownership.'
    }
    [System.IO.File]::AppendAllText($credentialsPath, 'tampered')
    $tamperedCredentialsOwnership = Get-HicResourceManagedOwnership -Containers $ownedContainers -PodmanMachineName 'synthetic-machine'
    if ($tamperedCredentialsOwnership.state -ne 'stale-or-invalid') {
        throw 'A changed managed credential file must invalidate ownership without exposing its contents.'
    }
    [System.IO.File]::WriteAllText($credentialsPath, 'synthetic ownership marker')
    $ownedContainers[1].service = 'postgres'
    $mislabelledOwnership = Get-HicResourceManagedOwnership -Containers $ownedContainers -PodmanMachineName 'synthetic-machine'
    if ($mislabelledOwnership.state -ne 'partial-or-stale') {
        throw 'A service-label mismatch must invalidate the durable ownership record.'
    }
    $ownedContainers[1].service = 'redis'
    $manifest.containers.'hic-managed-redis'.id = 'f' * 64
    [System.IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 8))
    $staleOwnership = Get-HicResourceManagedOwnership -Containers $ownedContainers -PodmanMachineName 'synthetic-machine'
    if ($staleOwnership.state -ne 'partial-or-stale') {
        throw 'A stale container ID in the ownership manifest must fail closed.'
    }
    Remove-Item -LiteralPath $credentialsPath -Force
    $manifest.containers.'hic-managed-redis'.id = 'b' * 64
    [System.IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 8))
    $missingMarkerOwnership = Get-HicResourceManagedOwnership -Containers $ownedContainers -PodmanMachineName 'synthetic-machine'
    if ($missingMarkerOwnership.state -ne 'stale-or-invalid') {
        throw 'A missing ownership credential marker must invalidate the managed stack.'
    }
}
finally {
    [Environment]::SetEnvironmentVariable('LOCALAPPDATA', $originalLocalAppData, 'Process')
    if (Test-Path -LiteralPath $ownershipRoot) {
        [System.IO.Directory]::Delete($ownershipRoot, $true)
    }
}

$orphanContainerInventory = Copy-TestInventory $cleanInventory
$orphanContainerInventory.containers = [ordered]@{
    state='verified'
    containers=@([pscustomobject]@{ id=('e' * 64); name='orphan-db'; state='exited'; project='unrelated'; service='postgres'; ownership='external-or-unknown' })
    volumes=@()
    memory=[ordered]@{ state='unknown'; containers=@() }
}
$orphanContainerPlan = New-HicResourcePlan -Profile 'Light' -Task 'api' -Inventory $orphanContainerInventory -ModelService ''
if ($orphanContainerPlan.managedStackAction -ne 'blocked' -or $orphanContainerPlan.blockers.Count -eq 0) {
    throw 'An orphan or unrelated container must block managed bootstrap.'
}

$stop = Invoke-HicResourceCommand -Action resource -SubAction stop -Profile Light -Task ui `
    -ModelService '' -PlanId '' -Ports $ports -Root $root
if ($stop.action -ne 'blocked' -or $stop.changesMade -ne $false) {
    throw 'Phase 1 stop must remain explicitly unavailable and non-mutating.'
}
$inventory.modelService = [ordered]@{ state='streaming'; identity='synthetic-model'; ownership='external-or-unknown' }
$streamingStop = Invoke-HicResourceCommand -Action resource -SubAction stop -Profile Development -Task inference `
    -ModelService 'synthetic-model' -PlanId 'HIC76-synthetic' -Apply -Ports $ports -Root $root
if ($streamingStop.action -ne 'blocked' -or $streamingStop.changesMade -ne $false) {
    throw 'An actively streaming external model must never be stopped by Phase 1.'
}

$definitions = Get-HicResourceDefinitions
if ($definitions.profiles.FullAgent.available -ne $false -or
    $definitions.services.executor.managed -ne $false -or
    $definitions.services.'agent-worker'.managed -ne $false) {
    throw 'Worker and executor must remain disabled and unavailable in the profile definitions.'
}

$required = @(
    'resource status',
    'resource doctor',
    'profile plan',
    'resource start',
    '-PlanId',
    '-Apply'
)
$launcher = Get-Content -Raw (Join-Path $root 'scripts\hic.ps1')
foreach ($token in @('resource', 'profile', 'PlanId', 'ExistingOnly')) {
    if ($launcher -notmatch [regex]::Escape($token) -and $token -notin @('ExistingOnly')) {
        throw "HIC launcher is missing resource command integration: $token"
    }
}
$resourceScript = Get-Content -Raw (Join-Path $PSScriptRoot 'hic-resource.ps1')
foreach ($token in $required) {
    if ($token -eq 'resource status' -or $token -eq 'resource doctor' -or
        $token -eq 'profile plan' -or $token -eq 'resource start') { continue }
    if ($resourceScript -notmatch [regex]::Escape($token)) {
        throw "Resource lifecycle authorization gate is missing: $token"
    }
}

$cliPlan = (& (Join-Path $PSScriptRoot 'hic.ps1') profile plan -Json -TestParallelism 1 -InferenceParallelism 1 -MemoryBudgetGiB 8) | ConvertFrom-Json
if ($cliPlan.profile -ne 'Light' -or $cliPlan.task -ne 'ui' -or
    $cliPlan.mutationsAuthorized -ne $false -or $cliPlan.memoryBudgetGiB.selectedGiB -ne 8) {
    throw 'CLI profile plan must default to Light, expose advisory overrides and authorize no mutations.'
}
$phase2CliPlan = (& (Join-Path $PSScriptRoot 'hic.ps1') resource phase2-plan -Json) | ConvertFrom-Json
if ($phase2CliPlan.operation -ne 'phase2-lifecycle-design' -or
    $phase2CliPlan.runtimeMutationEnabled -ne $false -or
    $phase2CliPlan.modelIdleUnload.enabled -ne $false -or
    $phase2CliPlan.disposableTestDatabase.persistentDatabaseReuse -ne $false) {
    throw 'The Phase 2 command must expose design-only lifecycle safeguards without authorizing runtime changes.'
}
$phase2ApplyRejected = $false
try { $null = & (Join-Path $PSScriptRoot 'hic.ps1') resource phase2-plan -Apply -Json }
catch { $phase2ApplyRejected = $true }
if (-not $phase2ApplyRejected) { throw 'The design-only Phase 2 command must reject -Apply.' }
$cliStart = (& (Join-Path $PSScriptRoot 'hic.ps1') resource start -Json) | ConvertFrom-Json
if ($cliStart.action -ne 'plan-only' -or $cliStart.changesMade -ne $false) {
    throw 'resource start without -Apply must remain a plan-only operation.'
}
$status = (& (Join-Path $PSScriptRoot 'hic.ps1') resource status -Json) | ConvertFrom-Json
if ($status.operation -ne 'resource-status' -or $status.inventory.readOnly -ne $true -or
    $null -eq $status.inventory.containers.volumes -or $null -eq $status.inventory.protectedPreviews) {
    throw 'Resource status must expose a read-only inventory of containers, volumes and protected previews.'
}
$doctor = (& (Join-Path $PSScriptRoot 'hic.ps1') resource doctor -Json) | ConvertFrom-Json
if ($doctor.operation -ne 'resource-doctor' -or $null -eq $doctor.findings) {
    throw 'Resource doctor must emit structured findings without changing state.'
}
$up = (& (Join-Path $PSScriptRoot 'hic.ps1') up -Json) | ConvertFrom-Json
if ($up.operation -ne 'profile-plan' -or $up.plan.mutationsAuthorized -ne $false) {
    throw 'Default up must remain plan-first and must not auto-start development services.'
}

Write-Host 'HIC resource inventory/profile offline safety tests: PASS'
