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

    if ([string]$State.serviceIdentity -cnotmatch '^[A-Za-z0-9][A-Za-z0-9_.-]{0,62}$') {
        return [pscustomobject]@{ action='blocked'; reason='Inference service identity is missing or invalid.' }
    }
    if ($State.serviceState -ne 'running') {
        return [pscustomobject]@{ action='blocked'; reason='Inference service is not verified running.' }
    }
    switch ([string]$State.admissionGate) {
        'open' {
            if ($State.leaseState -ne 'acquired' -or
                [string]$State.leaseId -cnotmatch '^[a-f0-9]{32}$') {
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
    $parsed = 0
    return [int]::TryParse([string]$Value, [ref]$parsed) -and $parsed -ge 0
}

function Get-HicPhase2IdleUnloadDecision {
    param(
        [System.Collections.IDictionary]$State,
        [string]$ApprovedPlanId,
        [string]$CurrentPlanId
    )

    if ([string]$State.serviceIdentity -cnotmatch '^[A-Za-z0-9][A-Za-z0-9_.-]{0,62}$' -or
        $State.serviceState -ne 'running') {
        return [pscustomobject]@{ action='blocked'; reason='A verified running model identity is required.' }
    }
    if ($State.ownership -ne 'hic-managed-verified') {
        return [pscustomobject]@{ action='protected'; reason='External or unowned model services are never unloaded.' }
    }
    if ($State.unloadAdapter -ne 'verified') {
        return [pscustomobject]@{ action='blocked'; reason='Provider unload capability is not verified.' }
    }
    if ($State.inferenceLedger -ne 'verified' -or -not (Test-HicPhase2NonNegativeInteger $State.inFlightRequests)) {
        return [pscustomobject]@{ action='blocked'; reason='Inference activity is unknown; unload is not safe.' }
    }
    if ([int]$State.inFlightRequests -gt 0) {
        return [pscustomobject]@{ action='wait'; reason='Inference requests remain in flight.' }
    }
    if ($State.admissionGate -ne 'sealed') {
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
        $ApprovedPlanId -cne $CurrentPlanId -or $State.planId -cne $CurrentPlanId) {
        return [pscustomobject]@{ action='blocked'; reason='The reviewed plan is missing or stale.' }
    }
    return [pscustomobject]@{
        action = 'unload-eligible'
        reason = 'All unload preconditions verified; this policy decision does not invoke a provider or unload a model.'
    }
}

function New-HicPhase2DisposableTestDatabasePlan {
    param([string]$RunId)

    if ($RunId -cnotmatch '^[a-f0-9]{32}$') {
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

    if ($Plan.runId -cnotmatch '^[a-f0-9]{32}$' -or
        $ExpectedContainerId -cnotmatch '^[a-f0-9]{64}$' -or
        $Observed.containerId -cne $ExpectedContainerId -or
        $Observed.containerName -cne $Plan.containerName -or
        $Observed.projectName -cne $Plan.projectName -or
        $Observed.runMarker -cne $Plan.runMarker -or
        $Observed.databaseName -cne $Plan.databaseName -or
        $Observed.roleName -cne $Plan.roleName -or
        $Observed.superuser -ne $Plan.superuser -or
        $Observed.network -cne $Plan.network -or
        $Observed.storage -cne $Plan.storage -or
        $Observed.hostBinding -cne $Plan.hostBinding -or
        $Observed.credentials -cne $Plan.credentials -or
        $Observed.persistentDatabaseReuse -ne $false) {
        return [pscustomobject]@{ action='preserve'; reason='Disposable database identity does not match this run; preserve the resource.' }
    }
    if (@($Observed.namedVolumes).Count -gt 0 -or @($Observed.bindMounts).Count -gt 0) {
        return [pscustomobject]@{ action='preserve'; reason='Persistent or host-mounted data was detected; cleanup is not authorized.' }
    }
    if ($Observed.testsRunning -ne $false) {
        return [pscustomobject]@{ action='wait'; reason='Test processes have not been verified exited.' }
    }
    if ($Observed.connectionsVerified -ne $true -or
        -not (Test-HicPhase2NonNegativeInteger $Observed.activeConnections)) {
        return [pscustomobject]@{ action='preserve'; reason='Database connection state is unknown; preserve the resource.' }
    }
    if ([int]$Observed.activeConnections -gt 0) {
        return [pscustomobject]@{ action='wait'; reason='Database connections remain active.' }
    }
    if ($Observed.state -notin @('running', 'exited', 'stopped')) {
        return [pscustomobject]@{ action='preserve'; reason='Disposable database runtime state is unknown.' }
    }
    return [pscustomobject]@{
        action = 'cleanup-eligible'
        reason = 'Exact run identity verified, no mounts remain, tests exited and database connections drained.'
    }
}
