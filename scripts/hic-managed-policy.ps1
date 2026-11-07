# Pure, offline-testable, fail-closed decision. Only a brand-new, isolated stack
# is bootstrapped; an existing HIC-managed stack may be resumed after identity checks.
function Get-HicManagedDecision {
    param([System.Collections.IDictionary]$Snapshot)

    if ($Snapshot.machine -ne 'running') {
        return [pscustomobject]@{ action = 'skip'; reason = 'Podman machine is not running; no automatic machine startup.' }
    }
    if ($Snapshot.discovery -ne 'verified') {
        return [pscustomobject]@{ action = 'skip'; reason = 'Cannot verify existing Podman containers/volumes; refusing infrastructure mutation.' }
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
    if ($Snapshot.controlPlanePortState -ne 'free') {
        return [pscustomobject]@{ action = 'skip'; reason = 'Configured Control Plane port is occupied or unknown; will not replace listener.' }
    }
    return [pscustomobject]@{ action = 'bootstrap'; reason = 'Fresh isolated HIC-managed runtime may be provisioned.' }
}
