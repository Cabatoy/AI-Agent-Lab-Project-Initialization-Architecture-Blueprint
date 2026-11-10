function Get-HicPhase2LifecycleDesign {
    return [ordered]@{
        operation = 'phase2-lifecycle-design'
        status = 'design-only'
        changesMade = $false
        runtimeMutationEnabled = $false
        workerEnabled = $false
        modelIdleUnload = [ordered]@{
            enabled = $false
            providerAdapter = 'unselected'
            idleTimeoutSeconds = $null
            eligibility = @(
                'explicit model identity',
                'verified HIC ownership',
                'verified unload adapter',
                'verified inference lease ledger with zero in-flight requests',
                'atomic admission gate sealed before unload',
                'idle threshold elapsed and exact reviewed plan ID'
            )
            externalModelPolicy = 'never unload or stop'
        }
        inferenceAdmission = [ordered]@{
            protocol = 'acquire lease before provider request; release after all retries and cancellation cleanup'
            gateDuringUnload = 'queue new inference until unload completes; never discard an active request'
            unknownState = 'fail closed'
        }
        disposableTestDatabase = [ordered]@{
            container = 'unique per-run identity'
            storage = 'ephemeral container layer; no named or bind mounts'
            network = 'isolated per run'
            hostBinding = 'loopback with runtime-assigned port only'
            credentials = 'per-run, runtime-only, never logged'
            cleanup = 'exact container ID and run marker after test processes exit and connections drain'
            persistentDatabaseReuse = $false
            workerEnabled = $false
        }
    }
}

function Get-HicPhase2InferenceAdmissionDecision {
    param([System.Collections.IDictionary]$State)

    if ($null -eq $State -or
        $State.serviceIdentity -isnot [string] -or
        $State.serviceIdentity -cnotmatch '\A[A-Za-z0-9][A-Za-z0-9_.-]{0,62}\z') {
        return [pscustomobject]@{ action='blocked'; reason='Inference service identity is missing or invalid.' }
    }
    if ($State.serviceState -isnot [string] -or $State.serviceState -cne 'running') {
        return [pscustomobject]@{ action='blocked'; reason='Inference service is not verified running.' }
    }
    if ($State.admissionGate -isnot [string]) {
        return [pscustomobject]@{ action='blocked'; reason='Inference admission state is unknown; fail closed.' }
    }
    switch -CaseSensitive ($State.admissionGate) {
        'open' {
            if ($State.leaseState -isnot [string] -or $State.leaseState -cne 'acquired' -or
                $State.leaseId -isnot [string] -or $State.leaseId -cnotmatch '\A[a-f0-9]{32}\z') {
                return [pscustomobject]@{ action='acquire-lease'; reason='Acquire a unique inference lease before sending the request.' }
            }
            return [pscustomobject]@{ action='admit'; reason='A verified inference lease protects this request.' }
        }
        'sealed' { return [pscustomobject]@{ action='wait'; reason='Model unload owns the sealed admission gate; keep the request queued.' } }
        default { return [pscustomobject]@{ action='blocked'; reason='Inference admission state is unknown; fail closed.' } }
    }
}

function Test-HicPhase2NonNegativeInteger {
    param([object]$Value)
    if (($Value -isnot [int] -and $Value -isnot [long] -and $Value -isnot [string]) -or
        [string]$Value -cnotmatch '\A(?:0|[1-9][0-9]*)\z') { return $false }
    $parsed = 0
    return [int]::TryParse([string]$Value, [ref]$parsed) -and $parsed -ge 0
}

function Get-HicPhase2IdleUnloadDecision {
    param(
        [System.Collections.IDictionary]$State,
        [string]$ApprovedPlanId,
        [string]$CurrentPlanId
    )

    if ($null -eq $State -or
        $State.serviceIdentity -isnot [string] -or
        $State.serviceIdentity -cnotmatch '\A[A-Za-z0-9][A-Za-z0-9_.-]{0,62}\z' -or
        $State.serviceState -isnot [string] -or $State.serviceState -cne 'running') {
        return [pscustomobject]@{ action='blocked'; reason='A verified running model identity is required.' }
    }
    if ($State.ownership -isnot [string] -or $State.ownership -cne 'hic-managed-verified') {
        return [pscustomobject]@{ action='protected'; reason='External or unowned model services are never unloaded.' }
    }
    if ($State.unloadAdapter -isnot [string] -or $State.unloadAdapter -cne 'verified') {
        return [pscustomobject]@{ action='blocked'; reason='Provider unload capability is not verified.' }
    }
    if ($State.inferenceLedger -isnot [string] -or $State.inferenceLedger -cne 'verified' -or
        -not (Test-HicPhase2NonNegativeInteger $State.inFlightRequests)) {
        return [pscustomobject]@{ action='blocked'; reason='Inference activity is unknown; unload is not safe.' }
    }
    if ([int]$State.inFlightRequests -gt 0) {
        return [pscustomobject]@{ action='wait'; reason='Inference requests remain in flight.' }
    }
    if ($State.admissionGate -isnot [string] -or $State.admissionGate -cne 'sealed') {
        return [pscustomobject]@{ action='seal-admission-first'; reason='Atomically seal new inference admission before rechecking active leases.' }
    }
    if (-not (Test-HicPhase2NonNegativeInteger $State.idleSeconds) -or
        -not (Test-HicPhase2NonNegativeInteger $State.idleTimeoutSeconds) -or
        [int]$State.idleTimeoutSeconds -lt 1 -or [int]$State.idleTimeoutSeconds -gt 86400) {
        return [pscustomobject]@{ action='blocked'; reason='Verified idle duration and a bounded positive timeout are required.' }
    }
    if ([int]$State.idleSeconds -lt [int]$State.idleTimeoutSeconds) {
        return [pscustomobject]@{ action='wait'; reason='Configured idle timeout has not elapsed.' }
    }
    if ([string]::IsNullOrWhiteSpace($CurrentPlanId) -or
        $ApprovedPlanId -cne $CurrentPlanId -or
        $State.planId -isnot [string] -or $State.planId -cne $CurrentPlanId) {
        return [pscustomobject]@{ action='blocked'; reason='The reviewed plan is missing or stale.' }
    }
    return [pscustomobject]@{
        action = 'unload-eligible'
        reason = 'All unload preconditions verified; this policy decision does not invoke a provider or unload a model.'
    }
}

function New-HicPhase2DisposableTestDatabasePlan {
    param([string]$RunId)

    if ($RunId -cnotmatch '\A[a-f0-9]{32}\z') {
        throw 'A lowercase 32-character run ID is required for an isolated test database plan.'
    }
    return [ordered]@{
        operation = 'disposable-test-database-plan'
        runId = $RunId
        containerName = "hic-test-pg-$RunId"
        projectName = "hic-test-$RunId"
        runMarker = "HIC:TESTDB:$RunId"
        databaseName = 'hic_test'
        roleName = 'hic_test_runner'
        superuser = $false
        storage = 'ephemeral-container-writable-layer'
        namedVolumes = @()
        bindMounts = @()
        network = 'isolated-per-run'
        hostBinding = '127.0.0.1:runtime-assigned-port'
        credentials = 'per-run-runtime-only-not-logged'
        persistentDatabaseReuse = $false
        workerEnabled = $false
        requiresExplicitApply = $true
        mutationsAuthorized = $false
    }
}

function Get-HicPhase2DisposableDatabaseCleanupDecision {
    param(
        [System.Collections.IDictionary]$Plan,
        [string]$ExpectedContainerId,
        [System.Collections.IDictionary]$Observed
    )

    if ($null -eq $Plan -or $null -eq $Observed -or
        $Plan.runId -isnot [string] -or $Plan.runId -cnotmatch '\A[a-f0-9]{32}\z' -or
        $ExpectedContainerId -cnotmatch '\A[a-f0-9]{64}\z') {
        return [pscustomobject]@{ action='preserve'; reason='Disposable database plan or expected identity is invalid.' }
    }
    # Matching observations cannot make a tampered or persistent database plan disposable.
    $canonicalPlan = New-HicPhase2DisposableTestDatabasePlan -RunId $Plan.runId
    foreach ($key in $canonicalPlan.Keys) {
        $expected = $canonicalPlan[$key]
        $actual = $Plan[$key]
        if ($expected -is [array]) {
            $matches = $actual -is [array] -and $actual.Count -eq 0
        }
        elseif ($expected -is [bool]) {
            $matches = $actual -is [bool] -and $actual -eq $expected
        }
        else {
            $matches = $actual -is [string] -and $actual -ceq $expected
        }
        if (-not $matches) {
            return [pscustomobject]@{ action='preserve'; reason="Disposable database plan field '$key' is missing or unsafe." }
        }
    }
    foreach ($key in @('containerName', 'projectName', 'runMarker', 'databaseName', 'roleName',
        'network', 'storage', 'hostBinding', 'credentials')) {
        if ($Observed[$key] -isnot [string] -or $Observed[$key] -cne $canonicalPlan[$key]) {
            return [pscustomobject]@{ action='preserve'; reason='Disposable database identity does not match this run; preserve the resource.' }
        }
    }
    if ($Observed.containerId -isnot [string] -or $Observed.containerId -cne $ExpectedContainerId -or
        $Observed.superuser -isnot [bool] -or $Observed.superuser -ne $false -or
        $Observed.persistentDatabaseReuse -isnot [bool] -or $Observed.persistentDatabaseReuse -ne $false) {
        return [pscustomobject]@{ action='preserve'; reason='Disposable database identity does not match this run; preserve the resource.' }
    }
    if ($Observed.namedVolumes -isnot [array] -or $Observed.namedVolumes.Count -gt 0 -or
        $Observed.bindMounts -isnot [array] -or $Observed.bindMounts.Count -gt 0) {
        return [pscustomobject]@{ action='preserve'; reason='Persistent or host-mounted data was detected; cleanup is not authorized.' }
    }
    if ($Observed.testsRunning -isnot [bool] -or $Observed.testsRunning -ne $false) {
        return [pscustomobject]@{ action='wait'; reason='Test processes have not been verified exited.' }
    }
    if ($Observed.connectionsVerified -isnot [bool] -or $Observed.connectionsVerified -ne $true -or
        -not (Test-HicPhase2NonNegativeInteger $Observed.activeConnections)) {
        return [pscustomobject]@{ action='preserve'; reason='Database connection state is unknown; preserve the resource.' }
    }
    if ([int]$Observed.activeConnections -gt 0) {
        return [pscustomobject]@{ action='wait'; reason='Database connections remain active.' }
    }
    if ($Observed.state -isnot [string] -or $Observed.state -cnotin @('running', 'exited', 'stopped')) {
        return [pscustomobject]@{ action='preserve'; reason='Disposable database runtime state is unknown.' }
    }
    return [pscustomobject]@{
        action = 'cleanup-eligible'
        reason = 'Exact run identity verified, no mounts remain, tests exited and database connections drained.'
    }
}
