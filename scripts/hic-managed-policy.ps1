# Pure, offline-testable, fail-closed decision. Only a brand-new, isolated stack
# is bootstrapped; an existing HIC-managed stack may be resumed after identity checks.
function Get-HicManagedWorkerState {
    param([object[]]$Containers)

    $workers = @($Containers | Where-Object {
        $_.service -ceq 'agent-worker' -or $_.name -match '(^|-)worker($|-)'
    })
    if ($workers.Count -eq 0) { return 'not-detected' }
    if (@($workers | Where-Object { $_.state -eq 'running' }).Count -gt 0) { return 'running' }
    if (@($workers | Where-Object { $_.state -notin @('exited', 'stopped', 'created', 'configured') }).Count -gt 0) {
        return 'unknown'
    }
    return 'stopped'
}

function Get-HicManagedDecision {
    param([System.Collections.IDictionary]$Snapshot)

    if ($Snapshot.machine -ne 'running') {
        return [pscustomobject]@{ action = 'skip'; reason = 'Podman machine is not running; no automatic machine startup.' }
    }
    if ($Snapshot.discovery -ne 'verified') {
        return [pscustomobject]@{ action = 'skip'; reason = 'Cannot verify existing Podman containers/volumes; refusing infrastructure mutation.' }
    }
    if ($Snapshot.worker -in @('running', 'unknown')) {
        return [pscustomobject]@{ action = 'skip'; reason = "Worker state is $($Snapshot.worker); refusing managed infrastructure mutation." }
    }
    $names = @($Snapshot.containers)
    $volumes = @($Snapshot.volumes)
    if (@($names | Where-Object { $_ -match '^(agentlab-|hic-managed-worker)' }).Count -gt 0 -or
        @($volumes | Where-Object { $_ -match '(^|_)postgres-data$|(^|_)redis-data$|^agentlab[-_]' }).Count -gt 0) {
        return [pscustomobject]@{ action = 'skip'; reason = 'Legacy R2/agentlab resources detected; recovery-plan and separate consent required.' }
    }
    $expectedNames = @('hic-managed-postgres', 'hic-managed-redis', 'hic-managed-control-plane')
    $present = @($expectedNames | Where-Object { $names -contains $_ })
    $expectedVolumes = @('hic-managed-postgres-data', 'hic-managed-redis-data')
    $presentVolumes = @($expectedVolumes | Where-Object { $volumes -contains $_ })
    if ($present.Count -gt 0 -or $presentVolumes.Count -gt 0 -or
        $Snapshot.manifestPresent -or $Snapshot.credentialsPresent) {
        if ($present.Count -ne 3 -or $presentVolumes.Count -ne 2 -or
            -not $Snapshot.manifestPresent -or -not $Snapshot.credentialsPresent) {
            return [pscustomobject]@{ action = 'skip'; reason = 'Partial managed stack or missing credentials/manifest. Manual verification required.' }
        }
        return [pscustomobject]@{ action = 'resume'; reason = 'Managed resources present; validate immutable container IDs and volumes before start.' }
    }
    if ($names.Count -gt 0 -or $volumes.Count -gt 0) {
        return [pscustomobject]@{ action = 'skip'; reason = 'Unrecognized containers or volumes appeared; refusing a fresh managed bootstrap.' }
    }
    if ($Snapshot.controlPlanePortState -ne 'free') {
        return [pscustomobject]@{ action = 'skip'; reason = 'Configured Control Plane port is occupied or unknown; will not replace listener.' }
    }
    return [pscustomobject]@{ action = 'bootstrap'; reason = 'Fresh isolated HIC-managed runtime may be provisioned.' }
}
