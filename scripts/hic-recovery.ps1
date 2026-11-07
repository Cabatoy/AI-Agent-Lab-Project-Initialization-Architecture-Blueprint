# Human In Control: read-only infrastructure recovery evidence and deterministic plan.
# No network except local Podman read-only inspect/port; never creates restart authority.

function Get-HicPortExposure {
    param([string[]]$Bindings)

    if ($null -eq $Bindings -or $Bindings.Count -eq 0) { return 'unverified' }
    $lines = @($Bindings | ForEach-Object { $_ -split '\r?\n' } | Where-Object { $_.Trim().Length -gt 0 })
    if ($lines.Count -eq 0) { return 'unverified' }
    $unknown = $false
    foreach ($raw in $lines) {
        $line = $raw.Trim()
        if ($line -match '^(127\.0\.0\.1|\[::1\]):[0-9]{1,5}$') { continue }
        # A recognizable non-loopback port binding is a safety blocker.
        if ($line -match ':[0-9]{1,5}$') { return 'non-loopback' }
        $unknown = $true
    }
    if ($unknown) { return 'unverified' }
    return 'loopback-only'
}

function Get-HicRecoveryEvidence {
    param([System.Collections.IDictionary]$State)
    $evidence = [ordered]@{
        currentControlPlaneImageId = 'unverified'
        controlPlanePortExposure = 'unverified'
        databaseSchema = 'unverified'
        taskSnapshot = 'unverified'
        activeExecutions = 'unverified'
        redisQueue = 'unverified'
        backupChecksum = 'unverified'
        rollbackImage = 'unverified'
        candidateImage = 'unverified'
    }

    if ($State.podmanMachine -ne 'running' -or
        $State.controlPlaneContainer -notin @('running', 'exited', 'created')) {
        return $evidence
    }
    if (-not (Get-Command podman -ErrorAction SilentlyContinue)) { return $evidence }

    $containerName = if ($State.infrastructureProfile -eq 'managed') { 'hic-managed-control-plane' } else { 'agentlab-control-plane' }

    try {
        # Formatting returns the image ID only; never dump inspect JSON, env, volumes or credentials.
        $imageProbe = Invoke-HicBoundedPodman -Arguments @('inspect','--format','{{.Image}}',$containerName) -TimeoutMs 3500
        $imageId = ($imageProbe.Lines -join '').Trim()
        if ($imageProbe.Success -and $imageId -match '^sha256:[0-9a-fA-F]{64}$') {
            $evidence.currentControlPlaneImageId = $imageId
        }
    }
    catch { }

    try {
        # Inspect only the expected container-internal port; never expose raw host addresses.
        $portProbe = Invoke-HicBoundedPodman -Arguments @('port',$containerName,'8080/tcp') -TimeoutMs 3500
        if ($portProbe.Success) {
            $evidence.controlPlanePortExposure = Get-HicPortExposure $portProbe.Lines
        }
    }
    catch { }

    return $evidence
}

function New-HicRecoveryPlan {
    param(
        [System.Collections.IDictionary]$State,
        [System.Collections.IDictionary]$Evidence
    )
    $blockers = [System.Collections.Generic.List[string]]::new()

    if ($State.podmanMachine -ne 'running') {
        $blockers.Add('Podman machine running state is not verified. Do not start the machine automatically.')
    }
    if ($State.controlPlaneContainer -notin @('running', 'exited', 'created')) {
        $blockers.Add('Existing Control Plane container identity/state is not verified.')
    }
    if ($State.postgresContainer -ne 'running') {
        $blockers.Add('PostgreSQL container must be verified running before considering recovery.')
    }
    if ($State.redisContainer -ne 'running') {
        $blockers.Add('Redis container must be verified running before considering recovery.')
    }
    if ($State.worker -notin @('exited', 'stopped')) {
        $blockers.Add('Worker must be explicitly verified stopped; unknown, missing and running states are NOT sufficient.')
    }
    if ($Evidence.controlPlanePortExposure -ne 'loopback-only') {
        $blockers.Add('Existing Control Plane container host port must be proven loopback-only.')
    }
    if ($Evidence.currentControlPlaneImageId -eq 'unverified') {
        $blockers.Add('Existing Control Plane image ID must be verified for possible rollback.')
    }

    # The following evidence cannot be assumed from ordinary container health.
    $blockers.Add('Existing R2 schema version, task IDs/statuses and transition/plan counts need a separate read-only database snapshot.')
    $blockers.Add('Redis queue emptiness and absence of active/recovering tasks need separate read-only verification.')
    $blockers.Add('Backup integrity/checksum and immutable rollback/candidate image IDs have not been verified.')
    $blockers.Add('Separate human approval immediately before any Control Plane restart/cutover has not been granted.')

    $disposition = if ($State.controlPlane -eq 'ready') {
        'no-recovery-needed'
    }
    else {
        'manual-preflight-required'
    }

    return [ordered]@{
        operation = 'recovery-plan'
        disposition = $disposition
        restartAuthorized = $false
        actionsPerformed = @('read-only local HTTP checks', 'read-only Podman inspect/port queries')
        observed = [ordered]@{
            controlPlane = $State.controlPlane
            controlPlaneHostPort = $State.controlPlanePort
            controlPlaneContainer = $State.controlPlaneContainer
            postgresContainer = $State.postgresContainer
            redisContainer = $State.redisContainer
            podmanMachine = $State.podmanMachine
            worker = $State.worker
            currentControlPlaneImageId = $Evidence.currentControlPlaneImageId
            controlPlanePortExposure = $Evidence.controlPlanePortExposure
        }
        blockers = $blockers.ToArray()
        nextStep = 'Review local preflight evidence and Issue #13. Obtain separate explicit approval for any targeted Control Plane cutover. Do not run docker-compose up/down.'
    }
}
