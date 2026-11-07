# Pure decision to allow cold-start of an exact previously pinned HIC machine.
# Nothing happens without a deliberate local "hic.cmd up"; no machine init/set/update.
function Get-HicMachineStartDecision {
    param(
        [pscustomobject]$Manifest,
        [object[]]$Machines,
        [bool]$CredentialsPresent
    )
    if (-not $CredentialsPresent -or $null -eq $Manifest -or
        $Manifest.version -ne 3 -or $Manifest.project -cne 'hic-managed') {
        return [pscustomobject]@{ action='skip'; reason='No verified HIC-managed installation manifest and credentials.' }
    }
    $name = [string]$Manifest.machineName
    if ($name -cnotmatch '^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$') {
        return [pscustomobject]@{ action='skip'; reason='Pinned Podman machine name is missing or invalid.' }
    }
    if (@($Machines).Count -ne 1) {
        return [pscustomobject]@{ action='skip'; reason='Cannot uniquely identify a dedicated previously pinned Podman machine.' }
    }
    $machine = $Machines[0]
    if ($null -eq $machine -or $machine.Name -cne $name) {
        return [pscustomobject]@{ action='skip'; reason='Available Podman machine does not match the saved HIC identity.' }
    }
    if ($machine.Running -eq $true) {
        return [pscustomobject]@{ action='noop'; reason='Verified pinned machine is already running.' }
    }
    if ($machine.Running -ne $false) {
        return [pscustomobject]@{ action='skip'; reason='Podman machine running state is unverified.' }
    }
    # Starting a user-mode-networking WSL machine could affect unrelated distributions.
    if ($machine.UserModeNetworking -ne $false) {
        return [pscustomobject]@{ action='skip'; reason='Cannot auto-start a machine with unknown or enabled WSL user-mode networking.' }
    }
    return [pscustomobject]@{ action='start'; reason='Verified stopped pinned HIC-managed Podman machine.'; machineName=$name }
}
