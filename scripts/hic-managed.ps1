# HIC-managed local Podman stack. Deliberate invocation from hic.cmd up only.
# No modifications to legacy agentlab containers/volumes, no Worker, no migrations of existing DBs.
. (Join-Path $PSScriptRoot 'hic-managed-policy.ps1')
. (Join-Path $PSScriptRoot 'hic-machine.ps1')

$HicManagedNames = @('hic-managed-postgres', 'hic-managed-redis', 'hic-managed-control-plane')

function Get-HicManagedPaths {
    if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        throw 'LOCALAPPDATA unavailable. Cannot safely store the per-user managed stack state.'
    }
    $directory = Join-Path $env:LOCALAPPDATA 'HumanInControl\managed'
    return [pscustomobject]@{
        directory = $directory
        credentials = Join-Path $directory 'runtime.env'
        manifest = Join-Path $directory 'manifest.json'
        compose = Join-Path (Join-Path $PSScriptRoot '..') 'deploy\hic-managed\compose.yaml'
    }
}

function Get-HicPodmanMachines {
    if (-not (Get-Command podman -ErrorAction SilentlyContinue)) {
        throw 'Podman CLI unavailable. Cannot verify machine identity.'
    }
    $probe = Invoke-HicBoundedPodman -Arguments @('machine','list','--format','json') -TimeoutMs 5000
    if (-not $probe.Success -or $probe.Lines.Count -eq 0) {
        throw 'Podman machine inventory timed out or failed. No automatic infrastructure changes.'
    }
    return @(($probe.Lines -join [Environment]::NewLine) | ConvertFrom-Json)
}

function Get-HicSingleRunningMachineName {
    $machines = @(Get-HicPodmanMachines)
    if ($machines.Count -ne 1 -or $machines[0].Running -ne $true) {
        throw 'The managed profile requires exactly one currently running Podman machine.'
    }
    $name = [string]$machines[0].Name
    if ($name -cnotmatch '^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$') {
        throw 'Podman machine name is invalid or unavailable.'
    }
    return $name
}

function Start-HicPinnedPodmanMachine {
    param([System.Collections.IDictionary]$Status, [pscustomobject]$Paths)
    if (-not (Test-Path -LiteralPath $Paths.manifest) -or
        -not (Test-Path -LiteralPath $Paths.credentials)) {
        return [pscustomobject]@{ outcome='skipped'; reason='No existing managed profile to resume; Podman machine not started.' }
    }
    $manifest = Get-Content -LiteralPath $Paths.manifest -Raw | ConvertFrom-Json
    $digest = (Get-FileHash -Algorithm SHA256 -LiteralPath $Paths.credentials).Hash.ToLowerInvariant()
    if ($manifest.credentialsSha256 -cne $digest -or $digest -cnotmatch '^[0-9a-f]{64}$') {
        return [pscustomobject]@{ outcome='skipped'; reason='Stored managed credentials digest does not verify; Podman machine was not started.' }
    }
    $machines = @(Get-HicPodmanMachines)
    $decision = Get-HicMachineStartDecision -Manifest $manifest -Machines $machines -CredentialsPresent $true
    if ($decision.action -eq 'skip') {
        return [pscustomobject]@{ outcome='skipped'; reason=$decision.reason }
    }
    if ($decision.action -eq 'start') {
        Write-Host "Starting previously pinned HIC Podman machine $($decision.machineName); no machine configuration changes."
        & podman machine start $decision.machineName | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Pinned HIC Podman machine did not start.' }
    }
    $verifiedMachines = @(Get-HicPodmanMachines)
    if ($verifiedMachines.Count -ne 1 -or
        $verifiedMachines[0].Name -cne $manifest.machineName -or
        $verifiedMachines[0].Running -ne $true) {
        throw 'Podman machine identity/running state did not verify after start.'
    }
    $Status['podmanMachine'] = 'running'
    return [pscustomobject]@{ outcome='ready'; reason='Pinned managed Podman machine verified running.' }
}

function Invoke-HicPodmanReadOnly {
    param([string[]]$CommandArgs)
    $probe = Invoke-HicBoundedPodman -Arguments $CommandArgs -TimeoutMs 5000
    if (-not $probe.Success) { throw 'Podman read-only inspection timed out or failed; refusing changes.' }
    return $probe.Lines
}

function Get-HicManagedSnapshot {
    param([System.Collections.IDictionary]$Status, [int]$ControlPlanePort)
    $paths = Get-HicManagedPaths
    $snapshot = [ordered]@{
        machine = $Status.podmanMachine
        discovery = 'unverified'
        containers = @()
        volumes = @()
        manifestPresent = Test-Path -LiteralPath $paths.manifest
        credentialsPresent = Test-Path -LiteralPath $paths.credentials
        controlPlanePortState = Get-LoopbackPortState $ControlPlanePort
    }
    if ($snapshot.machine -ne 'running' -or -not (Get-Command podman -ErrorAction SilentlyContinue)) {
        return $snapshot
    }
    try {
        $snapshot.containers = @(Invoke-HicPodmanReadOnly -CommandArgs @('ps', '-a', '--format', '{{.Names}}'))
        $snapshot.volumes = @(Invoke-HicPodmanReadOnly -CommandArgs @('volume', 'ls', '--format', '{{.Name}}'))
        $snapshot.discovery = 'verified'
    }
    catch { }
    return $snapshot
}

function Get-HicContainerRecord {
    param([string]$Name)
    $raw = @(Invoke-HicPodmanReadOnly -CommandArgs @('inspect', '--format', '{{.Id}}|{{.Image}}|{{.State.Status}}', $Name))
    if ($raw.Count -ne 1 -or $raw[0] -notmatch '^([0-9a-fA-F]{64})\|(sha256:[0-9a-fA-F]{64})\|(running|exited|created)$') {
        throw "Container identity/state for $Name cannot be validated."
    }
    return [ordered]@{ id = $Matches[1].ToLowerInvariant(); image = $Matches[2].ToLowerInvariant(); status = $Matches[3] }
}

function Assert-HicManagedMount {
    param([string]$Name, [string]$ExpectedVolume, [string]$ExpectedDestination)
    $raw = @(Invoke-HicPodmanReadOnly -CommandArgs @('inspect', '--format', '{{json .Mounts}}', $Name))
    if ($raw.Count -ne 1) { throw "Cannot inspect mounts for $Name." }
    $mounts = @(($raw[0] | ConvertFrom-Json))
    if (@($mounts | Where-Object { $_.Type -eq 'volume' -and $_.Name -eq $ExpectedVolume -and
        $_.Destination -eq $ExpectedDestination }).Count -ne 1) {
        throw "Expected data volume binding for $Name is missing. Refusing to start."
    }
}

function Assert-HicManagedCPPort {
    param([int]$ExpectedPort)
    $lines = @(Invoke-HicPodmanReadOnly -CommandArgs @('port', 'hic-managed-control-plane', '8080/tcp'))
    if ($lines.Count -ne 1 -or $lines[0].Trim() -cne "127.0.0.1:$ExpectedPort") {
        throw 'Control Plane port binding is not precisely the expected loopback port.'
    }
}

function Get-HicManagedContainerRecords {
    $records = [ordered]@{}
    foreach ($name in $HicManagedNames) { $records[$name] = Get-HicContainerRecord $name }
    return $records
}

function Assert-HicManagedIdentity {
    param([string]$ManifestPath, [string]$CredentialsPath, [int]$ExpectedPort)
    $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    if ($manifest.version -notin @(2, 3) -or $manifest.project -cne 'hic-managed' -or
        $manifest.controlPlanePort -ne $ExpectedPort) {
        throw 'Managed manifest differs from configured runtime. No automatic migration.'
    }
    if ($manifest.version -eq 3 -and $manifest.machineName -cne (Get-HicSingleRunningMachineName)) {
        throw 'Runtime Podman machine differs from the originally pinned HIC machine.'
    }
    $digest = (Get-FileHash -Algorithm SHA256 -LiteralPath $CredentialsPath).Hash.ToLowerInvariant()
    if ($manifest.credentialsSha256 -cne $digest) {
        throw 'Managed credentials file changed. Refusing to start services with an unknown database credential.'
    }
    $current = Get-HicManagedContainerRecords
    foreach ($name in $HicManagedNames) {
        $record = $current[$name]
        $stored = $manifest.containers.$name
        if ($null -eq $stored -or $record.id -cne $stored.id -or $record.image -cne $stored.image) {
            throw "Managed container $name was replaced or image changed; refusing an unreviewed start."
        }
    }
    Assert-HicManagedMount 'hic-managed-postgres' 'hic-managed-postgres-data' '/var/lib/postgresql/data'
    Assert-HicManagedMount 'hic-managed-redis' 'hic-managed-redis-data' '/data'
    Assert-HicManagedCPPort $ExpectedPort
    return $current
}

function Test-HicManagedCPReady {
    param([int]$Port)
    try {
        $r = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/health" -Method Get -TimeoutSec 2 -ErrorAction Stop
        return ($r.status -ceq 'ok' -and $r.service -ceq 'control-plane')
    }
    catch { return $false }
}

function Wait-HicManagedCP {
    param([int]$Port)
    for ($i = 0; $i -lt 45; $i++) {
        if (Test-HicManagedCPReady $Port) { return }
        Start-Sleep -Seconds 2
    }
    throw 'Managed Control Plane did not become healthy. Containers and data are left intact for investigation.'
}

function Invoke-HicPodmanMutation {
    param([string[]]$CommandArgs)
    & podman @CommandArgs | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Managed Podman operation failed. No cleanup is performed.' }
}

function New-HicManagedCredentials {
    param([string]$Path, [int]$ControlPlanePort)
    $bytes = New-Object byte[] 32
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    $password = [BitConverter]::ToString($bytes).Replace('-', '').ToLowerInvariant()
    [Array]::Clear($bytes, 0, $bytes.Length)
    # Atomic, fail-if-present file creation under the current user's LOCALAPPDATA.
    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    try {
        $value = "HIC_DB_PASSWORD=$password" + [Environment]::NewLine +
            "HIC_CONTROL_PLANE_PORT=$ControlPlanePort" + [Environment]::NewLine
        $encoded = [System.Text.Encoding]::UTF8.GetBytes($value)
        $stream.Write($encoded, 0, $encoded.Length)
    }
    finally {
        $stream.Dispose()
        $password = $null
    }
}

function Start-HicManagedInfrastructure {
    param([System.Collections.IDictionary]$Status, [int]$ControlPlanePort)

    $paths = Get-HicManagedPaths
    if ($Status.podmanMachine -ne 'running') {
        $machineResult = Start-HicPinnedPodmanMachine -Status $Status -Paths $paths
        if ($machineResult.outcome -ne 'ready') {
            return [pscustomobject]@{ outcome='skipped'; reason=$machineResult.reason }
        }
    }
    $snapshot = Get-HicManagedSnapshot $Status $ControlPlanePort
    $decision = Get-HicManagedDecision $snapshot
    if ($decision.action -eq 'skip') {
        return [pscustomobject]@{ outcome = 'skipped'; reason = $decision.reason }
    }

    $mutex = New-Object System.Threading.Mutex($false, 'Local\HIC.Managed.Infrastructure')
    $locked = $false
    try {
        $locked = $mutex.WaitOne([TimeSpan]::FromSeconds(2))
        if (-not $locked) { throw 'Another managed HIC startup is running.' }
        # Re-inspect after acquiring the single-machine mutex.
        $snapshot = Get-HicManagedSnapshot $Status $ControlPlanePort
        $decision = Get-HicManagedDecision $snapshot
        if ($decision.action -eq 'skip') {
            return [pscustomobject]@{ outcome = 'skipped'; reason = $decision.reason }
        }

        if ($decision.action -eq 'bootstrap') {
            if (-not (Test-Path -LiteralPath $paths.compose)) {
                throw 'Dedicated HIC-managed Compose manifest is missing.'
            }
            # Prevent ambient shell variables from overriding the newly generated env file.
            if ([Environment]::GetEnvironmentVariable('HIC_DB_PASSWORD')) {
                throw 'An ambient HIC_DB_PASSWORD would override the private managed credential. Unset it before first provisioning.'
            }
            # Pin the current single Podman machine on first clean install.
            $machineName = Get-HicSingleRunningMachineName
            # A dedicated Compose file excludes Worker and never publishes DB/Redis ports.
            $null = @(Invoke-HicPodmanReadOnly -CommandArgs @('compose', 'version'))
            [System.IO.Directory]::CreateDirectory($paths.directory) | Out-Null
            New-HicManagedCredentials $paths.credentials $ControlPlanePort

            Write-Host 'Provisioning isolated NEW HIC-managed PostgreSQL and Redis only (no Worker).'
            & podman compose --project-name hic-managed --file $paths.compose --env-file $paths.credentials up --detach postgres redis
            if ($LASTEXITCODE -ne 0) { throw 'Fresh managed database/cache provisioning failed. No cleanup performed.' }

            # A fresh PostgreSQL instance has no R2 schema. The CP refuses to start until
            # v2 exists. Run the single-transaction guard ONLY in this bootstrap branch,
            # where an empty, new managed volume was verified before any container creation.
            Write-Host 'Initializing R2 schema v2 on the NEW empty HIC-managed PostgreSQL volume.'
            & podman compose --project-name hic-managed --profile fresh-bootstrap-only --file $paths.compose --env-file $paths.credentials run --rm schema-init
            if ($LASTEXITCODE -ne 0) { throw 'Fresh schema v2 initialization failed. Database is preserved for inspection; no retry/migration on an existing volume.' }

            Write-Host 'Starting Control Plane against verified new schema v2 (Worker remains absent).'
            & podman compose --project-name hic-managed --file $paths.compose --env-file $paths.credentials up --detach --build control-plane
            if ($LASTEXITCODE -ne 0) { throw 'New managed Control Plane provisioning failed. No automatic cleanup.' }

            Wait-HicManagedCP $ControlPlanePort
            Assert-HicManagedMount 'hic-managed-postgres' 'hic-managed-postgres-data' '/var/lib/postgresql/data'
            Assert-HicManagedMount 'hic-managed-redis' 'hic-managed-redis-data' '/data'
            Assert-HicManagedCPPort $ControlPlanePort
            $records = Get-HicManagedContainerRecords

            $manifest = [ordered]@{
                version = 3
                project = 'hic-managed'
                machineName = $machineName
                controlPlanePort = $ControlPlanePort
                credentialsSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $paths.credentials).Hash.ToLowerInvariant()
                containers = $records
            } | ConvertTo-Json -Depth 6
            $temporaryManifest = "$($paths.manifest).tmp"
            [System.IO.File]::WriteAllText($temporaryManifest, $manifest, [System.Text.Encoding]::UTF8)
            [System.IO.File]::Move($temporaryManifest, $paths.manifest)
            return [pscustomobject]@{ outcome = 'ready'; reason = 'Fresh managed HIC infrastructure initialized.' }
        }

        $records = Assert-HicManagedIdentity -ManifestPath $paths.manifest -CredentialsPath $paths.credentials -ExpectedPort $ControlPlanePort
        $startOrder = @('hic-managed-postgres', 'hic-managed-redis', 'hic-managed-control-plane')
        foreach ($name in $startOrder) {
            $state = $records[$name].status
            if ($state -eq 'running') { continue }
            if ($state -notin @('exited', 'created')) {
                throw "Unexpected $name state $state. No restart performed."
            }
            if ($name -eq 'hic-managed-control-plane' -and (Get-LoopbackPortState $ControlPlanePort) -ne 'free') {
                throw 'Control Plane host port is occupied. Will not start stopped container.'
            }
            Write-Host "Starting verified existing managed container $name (not rebuilding or recreating)."
            Invoke-HicPodmanMutation -CommandArgs @('start', $name)
        }
        Wait-HicManagedCP $ControlPlanePort
        return [pscustomobject]@{ outcome = 'ready'; reason = 'Existing HIC-managed services verified and resumed.' }
    }
    finally {
        if ($locked) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
}
