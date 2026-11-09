function Get-HicResourceDefinitions {
    $path = Join-Path (Join-Path $PSScriptRoot '..') 'deploy\hic-resources\profiles.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw 'HIC resource profile definition is missing.'
    }
    $definitions = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($definitions.schemaVersion -ne 1 -or $null -eq $definitions.profiles -or $null -eq $definitions.services) {
        throw 'HIC resource profile definition has an unsupported schema.'
    }
    return $definitions
}

function Get-HicResourceProperty {
    param([object]$Object, [string]$Name)
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties | Where-Object { $_.Name -ceq $Name } | Select-Object -First 1
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Get-HicResourceLabelValue {
    param([string]$Labels, [string]$Name)
    if ([string]::IsNullOrWhiteSpace($Labels) -or [string]::IsNullOrWhiteSpace($Name)) { return '' }
    $pattern = '(?:^|,)' + [regex]::Escape($Name) + '=([^,]*)'
    if ($Labels -match $pattern) { return $Matches[1] }
    return ''
}

function Get-HicResourceProfile {
    param([string]$Name)
    $definitions = Get-HicResourceDefinitions
    $profile = Get-HicResourceProperty $definitions.profiles $Name
    if ($null -eq $profile) { throw "Unknown HIC resource profile: $Name" }
    return $profile
}

function Get-HicResourceProfileServices {
    param([string]$Profile, [string]$Task)
    $definition = Get-HicResourceProfile $Profile
    $services = @(Get-HicResourceProperty $definition.tasks $Task)
    if ($services.Count -eq 0) { throw "Unknown task '$Task' for resource profile '$Profile'." }
    return $services
}

function Get-HicResourceServiceClosure {
    param([string[]]$RequestedServices)
    $definitions = Get-HicResourceDefinitions
    $ordered = [System.Collections.Generic.List[string]]::new()
    $visiting = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $visited = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)

    $visit = {
        param([string]$Service)
        if ($visited.Contains($Service)) { return }
        if (-not $visiting.Add($Service)) { throw "Resource dependency cycle detected at '$Service'." }
        $definition = Get-HicResourceProperty $definitions.services $Service
        if ($null -eq $definition) { throw "Unknown HIC resource service: $Service" }
        foreach ($dependency in @(Get-HicResourceProperty $definition 'dependsOn')) {
            & $visit ([string]$dependency)
        }
        $visiting.Remove($Service) | Out-Null
        $visited.Add($Service) | Out-Null
        $ordered.Add($Service)
    }

    foreach ($service in $RequestedServices) { & $visit $service }
    return $ordered.ToArray()
}

function Get-HicResourceManagedOwnership {
    param([object[]]$Containers, [string]$PodmanMachineName)
    $result = [ordered]@{
        state = 'unverified'
        recordPresent = $false
        credentialsPresent = $null
        ownedContainerNames = @()
    }
    if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) { return $result }
    $managedDirectory = Join-Path $env:LOCALAPPDATA 'HumanInControl\managed'
    $manifestPath = Join-Path $managedDirectory 'manifest.json'
    $credentialsPath = Join-Path $managedDirectory 'runtime.env'
    $result.credentialsPresent = Test-Path -LiteralPath $credentialsPath -PathType Leaf
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        $result.state = if ($result.credentialsPresent) { 'partial-or-stale' } else { 'absent' }
        return $result
    }
    $result.recordPresent = $true
    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        if ($manifest.version -ne 3 -or $manifest.project -cne 'hic-managed' -or
            [string]::IsNullOrWhiteSpace([string]$manifest.machineName) -or
            $manifest.machineName -cne $PodmanMachineName -or
            -not $result.credentialsPresent) {
            $result.state = 'stale-or-invalid'
            return $result
        }
        $credentialDigest = (Get-FileHash -Algorithm SHA256 -LiteralPath $credentialsPath).Hash.ToLowerInvariant()
        if ($manifest.credentialsSha256 -cne $credentialDigest -or $credentialDigest -cnotmatch '^[0-9a-f]{64}$') {
            $result.state = 'stale-or-invalid'
            return $result
        }
        $owned = [System.Collections.Generic.List[string]]::new()
        $expectedServices = @{
            'hic-managed-postgres' = 'postgres'
            'hic-managed-redis' = 'redis'
            'hic-managed-control-plane' = 'control-plane'
        }
        foreach ($container in $Containers) {
            $stored = Get-HicResourceProperty $manifest.containers $container.name
            if ($null -eq $stored) { continue }
            $expectedService = $expectedServices[$container.name]
            if ($container.id -match '^[0-9a-f]{64}$' -and
                $container.id -ceq [string]$stored.id -and
                $container.project -ceq 'hic-managed' -and
                $null -ne $expectedService -and $container.service -ceq $expectedService) {
                $owned.Add($container.name)
            }
        }
        $result.ownedContainerNames = $owned.ToArray()
        if ($owned.Count -eq 3) { $result.state = 'verified' }
        else { $result.state = 'partial-or-stale' }
        return $result
    }
    catch {
        $result.state = 'stale-or-invalid'
        return $result
    }
}

function Get-HicResourceHostMemory {
    $result = [ordered]@{
        state = 'unknown'
        source = 'Win32_OperatingSystem'
        scope = 'Windows host total includes WSL/Podman and other processes; do not add component working sets to it.'
        totalGiB = $null
        availableGiB = $null
        usedGiB = $null
    }
    try {
        $operatingSystem = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop
        $total = [double]$operatingSystem.TotalVisibleMemorySize * 1024
        $available = [double]$operatingSystem.FreePhysicalMemory * 1024
        if ($total -le 0 -or $available -lt 0 -or $available -gt $total) {
            $result.state = 'invalid-measurement'
            return $result
        }
        $result.state = 'measured'
        $result.totalGiB = [math]::Round($total / 1GB, 2)
        $result.availableGiB = [math]::Round($available / 1GB, 2)
        $result.usedGiB = [math]::Round(($total - $available) / 1GB, 2)
        return $result
    }
    catch {
        $result.state = 'unavailable'
        return $result
    }
}

function Get-HicResourceTopProcesses {
    $result = [ordered]@{
        state = 'unknown'
        scope = 'Per-process working set; not additive to host or container measurements.'
        processes = @()
        vmProcesses = @()
    }
    try {
        $allProcesses = @(Get-Process -ErrorAction Stop)
        $result.processes = @(
            $allProcesses | Sort-Object -Property WorkingSet64 -Descending |
                Select-Object -First 12 `
                    @{ Name = 'name'; Expression = { $_.ProcessName } },
                    @{ Name = 'pid'; Expression = { $_.Id } },
                    @{ Name = 'workingSetMiB'; Expression = { [math]::Round($_.WorkingSet64 / 1MB, 1) } }
        )
        $result.vmProcesses = @(
            $allProcesses | Where-Object { $_.ProcessName -match '^vmmem(?:WSL)?$' } |
                Select-Object `
                    @{ Name = 'name'; Expression = { $_.ProcessName } },
                    @{ Name = 'pid'; Expression = { $_.Id } },
                    @{ Name = 'workingSetMiB'; Expression = { [math]::Round($_.WorkingSet64 / 1MB, 1) } }
        )
        $result.state = 'measured'
        return $result
    }
    catch {
        $result.state = 'unavailable'
        return $result
    }
}

function Get-HicResourceContainerInventory {
    param([string]$PodmanMachine, [string]$PodmanMachineName)
    $result = [ordered]@{
        state = 'unknown'
        containers = @()
        volumes = @()
        memory = [ordered]@{ state = 'unknown'; containers = @(); note = 'Container memory is reported separately from host/process working sets.' }
    }
    if ($PodmanMachine -ne 'running') {
        $result.state = if ($PodmanMachine -eq 'not-detected') { 'not-detected' } else { 'unavailable' }
        return $result
    }

    $format = '{{.ID}}|{{.Names}}|{{.State}}|{{.Labels}}'
    $containersProbe = Invoke-HicBoundedPodman -Arguments @('ps', '--all', '--no-trunc', '--format', $format) -TimeoutMs 5000
    if (-not $containersProbe.Success) {
        $result.state = if ($containersProbe.TimedOut) { 'unresponsive' } else { 'unavailable' }
        return $result
    }
    $containers = [System.Collections.Generic.List[object]]::new()
    foreach ($line in $containersProbe.Lines) {
        $fields = $line -split '\|', 4
        if ($fields.Count -ne 4 -or $fields[0] -notmatch '^[0-9a-fA-F]{12,64}$' -or
            $fields[1] -notmatch '^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$') {
            continue
        }
        $containers.Add([pscustomobject]@{
            id = $fields[0].ToLowerInvariant()
            name = $fields[1]
            state = $fields[2].Trim().ToLowerInvariant()
            project = Get-HicResourceLabelValue -Labels $fields[3] -Name 'com.docker.compose.project'
            service = Get-HicResourceLabelValue -Labels $fields[3] -Name 'com.docker.compose.service'
            ownership = 'external-or-unknown'
            memory = 'unknown'
        })
    }
    $ownership = Get-HicResourceManagedOwnership -Containers $containers.ToArray() -PodmanMachineName $PodmanMachineName
    foreach ($container in $containers) {
        if ($container.name -cin $ownership.ownedContainerNames) { $container.ownership = 'hic-managed-verified' }
    }

    $statsFormat = '{{.Name}}|{{.MemUsage}}'
    $statsProbe = Invoke-HicBoundedPodman -Arguments @('stats', '--no-stream', '--format', $statsFormat) -TimeoutMs 8000
    if ($statsProbe.Success) {
        $memoryRows = [System.Collections.Generic.List[object]]::new()
        foreach ($line in $statsProbe.Lines) {
            $fields = $line -split '\|', 2
            if ($fields.Count -ne 2) { continue }
            $memoryState = 'reported'
            $memoryValue = $fields[1].Trim()
            if ($memoryValue -match '^(0(?:\.0+)?\s*B)\s*/') {
                $memoryState = 'unavailable'
                $memoryValue = 'unknown'
            }
            $container = @($containers | Where-Object { $_.name -ceq $fields[0] }) | Select-Object -First 1
            $memoryRows.Add([pscustomobject]@{
                name = $fields[0]
                service = if ($null -ne $container -and $container.service) { $container.service } else { 'unknown' }
                ownership = if ($null -ne $container) { $container.ownership } else { 'external-or-unknown' }
                state = $memoryState
                value = $memoryValue
            })
            if ($null -ne $container) { $container.memory = $memoryValue }
        }
        $result.memory.containers = $memoryRows.ToArray()
        if ($memoryRows.Count -gt 0 -and @($memoryRows | Where-Object { $_.state -eq 'reported' }).Count -eq $memoryRows.Count) {
            $result.memory.state = 'measured'
        }
        elseif ($memoryRows.Count -gt 0) {
            $result.memory.state = 'partial-or-unavailable'
            $result.memory.note = 'At least one runtime memory sample was zero or unavailable; do not aggregate these values.'
        }
    }
    else {
        $result.memory.state = if ($statsProbe.TimedOut) { 'unresponsive' } else { 'unavailable' }
        $result.memory.note = 'Podman did not provide a complete bounded memory sample.'
    }

    $volumeProbe = Invoke-HicBoundedPodman -Arguments @('volume', 'ls', '--format', '{{.Name}}') -TimeoutMs 5000
    if ($volumeProbe.Success) {
        $result.volumes = @($volumeProbe.Lines | Where-Object { $_ -match '^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$' } |
            ForEach-Object {
                $isOwnedVolume = ($_ -eq 'hic-managed-postgres-data' -and 'hic-managed-postgres' -cin $ownership.ownedContainerNames) -or
                    ($_ -eq 'hic-managed-redis-data' -and 'hic-managed-redis' -cin $ownership.ownedContainerNames)
                [pscustomobject]@{
                    name = $_
                    ownership = if ($isOwnedVolume) { 'hic-managed-verified' } else { 'protected-external-or-unknown' }
                }
            })
    }
    else {
        $result.containers = $containers.ToArray()
        $result.state = 'partial'
        return $result
    }
    $result.containers = $containers.ToArray()
    $result.state = 'verified'
    return $result
}

function Get-HicResourceListenerInventory {
    param([object]$Ports, [int[]]$ProtectedPreviewPorts, [int[]]$ProtectedServicePorts)
    $result = [ordered]@{
        state = 'unknown'
        reservedPreviewPorts = @($ProtectedPreviewPorts)
        reservedServicePorts = @($ProtectedServicePorts)
        protectedListeners = @()
    }
    try {
        $allPorts = @($Ports.ControlPlane, $Ports.NativeHost, $Ports.WebUI, 8080, 5248, 5180) +
            $ProtectedPreviewPorts + $ProtectedServicePorts
        $listeners = @(Get-NetTCPConnection -State Listen -ErrorAction Stop |
            Where-Object { $_.LocalPort -in $allPorts } |
            Sort-Object -Property LocalPort, OwningProcess -Unique)
        $result.protectedListeners = @($listeners | ForEach-Object {
            $processName = 'unknown'
            try { $processName = (Get-Process -Id $_.OwningProcess -ErrorAction Stop).ProcessName } catch { }
            [pscustomobject]@{
                port = [int]$_.LocalPort
                process = $processName
                ownership = 'protected'
            }
        })
        $result.state = 'verified'
        return $result
    }
    catch {
        $result.state = 'unavailable'
        return $result
    }
}

function Get-HicResourceWorkerState {
    param([object[]]$Containers, [string]$PodmanMachine)
    if ($PodmanMachine -ne 'running') { return 'unknown' }
    $workers = @($Containers | Where-Object { $_.service -ceq 'agent-worker' -or $_.name -match '(^|-)worker($|-)'} )
    if (@($workers | Where-Object { $_.state -eq 'running' }).Count -gt 0) { return 'running' }
    if ($workers.Count -eq 0) { return 'not-detected' }
    if (@($workers | Where-Object { $_.state -notin @('exited', 'stopped', 'created', 'configured') }).Count -gt 0) {
        return 'unknown'
    }
    return 'stopped'
}

function Get-HicResourcePreviewState {
    param([object]$Ports)
    $nativeReady = Test-HttpReady "http://127.0.0.1:$($Ports.NativeHost)/v1/status" 'status' 'ready' 'hic-native-host'
    $webReady = Test-HicWebUI $Ports.WebUI
    $legacyNativeReady = Test-HttpReady 'http://127.0.0.1:5248/v1/status' 'status' 'ready' 'hic-native-host'
    $legacyWebReady = Test-HicWebUI 5180
    return [ordered]@{
        nativeHost = if ($nativeReady) { 'ready' } else { 'unavailable' }
        webUI = if ($webReady) { 'ready' } else { 'unavailable' }
        nativePortState = Get-LoopbackPortState $Ports.NativeHost
        webUIPortState = Get-LoopbackPortState $Ports.WebUI
        legacyNative = $legacyNativeReady
        legacyWebUI = $legacyWebReady
    }
}

function Get-HicResourceInventory {
    param([object]$Ports)
    $machine = 'not-detected'
    $machineName = ''
    if (Get-Command podman.exe -ErrorAction SilentlyContinue) {
        $machineProbe = Invoke-HicBoundedPodman -Arguments @('machine', 'list', '--format', 'json') -TimeoutMs 5000
        if ($machineProbe.TimedOut) { $machine = 'unresponsive' }
        elseif ($machineProbe.Success) {
            try {
                $machines = @(($machineProbe.Lines -join [Environment]::NewLine) | ConvertFrom-Json)
                if ($machines.Count -gt 1) {
                    $machine = 'ambiguous'
                }
                elseif ($machines.Count -eq 1 -and $machines[0].Running -eq $true) {
                    $machineName = [string]$machines[0].Name
                    if ($machineName -match '^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$') { $machine = 'running' }
                    else { $machineName = ''; $machine = 'unknown' }
                }
                else {
                    $machine = 'stopped'
                }
            }
            catch { $machine = 'unknown' }
        }
        else { $machine = 'unknown' }
    }
    $containers = Get-HicResourceContainerInventory -PodmanMachine $machine -PodmanMachineName $machineName
    $definitions = Get-HicResourceDefinitions
    $listeners = Get-HicResourceListenerInventory -Ports $Ports `
        -ProtectedPreviewPorts @($definitions.observation.protectedPreviewPorts) `
        -ProtectedServicePorts @($definitions.observation.protectedServicePorts)
    $protectedPreviews = @($listeners.protectedListeners | Where-Object { $_.port -in @($definitions.observation.protectedPreviewPorts) })
    if ($containers.state -eq 'verified') {
        $ownership = Get-HicResourceManagedOwnership -Containers @($containers.containers) -PodmanMachineName $machineName
    }
    else {
        $ownership = [ordered]@{
            state = 'unverified'
            recordPresent = $false
            credentialsPresent = $null
            ownedContainerNames = @()
        }
    }
    $podmanStats = Get-HicResourceTopProcesses
    $vmProcess = @($podmanStats.vmProcesses | Select-Object -First 1)

    return [ordered]@{
        checkedAtUtc = [DateTime]::UtcNow.ToString('o')
        readOnly = $true
        podmanMachine = $machine
        podmanMachineName = if ($machine -eq 'running') { $machineName } else { $null }
        configuredPorts = [ordered]@{
            controlPlane = $Ports.ControlPlane
            nativeHost = $Ports.NativeHost
            webUI = $Ports.WebUI
        }
        hostMemory = Get-HicResourceHostMemory
        topProcesses = $podmanStats
        podmanVmWorkingSet = if ($vmProcess.Count -gt 0) {
            [ordered]@{ state = 'measured'; process = $vmProcess[0] }
        } else {
            [ordered]@{ state = 'unknown'; reason = 'No Podman/WSL VM process was identified by a stable process name.' }
        }
        containers = $containers
        managedOwnership = $ownership
        listeners = $listeners
        protectedPreviews = $protectedPreviews
        preview = Get-HicResourcePreviewState $Ports
        worker = Get-HicResourceWorkerState -Containers @($containers.containers) -PodmanMachine $machine
        modelService = [ordered]@{
            state = 'unknown'
            reason = 'No explicit model-service identity was supplied; provider/process/container names are not guessed.'
        }
        memoryAccounting = 'Host, per-process, Podman-VM and container figures are separate scopes. Never sum them.'
    }
}

function Get-HicResourceServiceCurrentState {
    param([string]$Service, [System.Collections.IDictionary]$Inventory)
    switch ($Service) {
        'native-host' {
            if ($Inventory.preview.nativeHost -eq 'ready' -or $Inventory.preview.legacyNative) { return 'ready' }
            return $Inventory.preview.nativeHost
        }
        'web-ui' {
            if ($Inventory.preview.webUI -eq 'ready' -or $Inventory.preview.legacyWebUI) { return 'ready' }
            return $Inventory.preview.webUI
        }
        'selected-model' { return $Inventory.modelService.state }
        'agent-worker' { return $Inventory.worker }
        default {
            $definition = Get-HicResourceProperty (Get-HicResourceDefinitions).services $Service
            $matches = @($Inventory.containers.containers | Where-Object {
                $_.service -ceq $definition.composeService -or $_.name -ceq $definition.containerName
            })
            if ($matches.Count -eq 0) { return 'not-detected' }
            $running = @($matches | Where-Object { $_.state -eq 'running' })
            if ($running.Count -gt 0) { return 'running' }
            if (@($matches | Where-Object { $_.state -notin @('exited', 'stopped', 'created', 'configured') }).Count -gt 0) {
                return 'unknown'
            }
            return 'stopped'
        }
    }
}

function New-HicResourcePlan {
    param(
        [string]$Profile,
        [string]$Task,
        [System.Collections.IDictionary]$Inventory,
        [string]$ModelService,
        [int]$MemoryBudgetGiB = 0,
        [int]$TestParallelism = 0,
        [int]$InferenceParallelism = 0
    )
    $profileDefinition = Get-HicResourceProfile $Profile
    $requested = @(Get-HicResourceProfileServices -Profile $Profile -Task $Task)
    $services = @(Get-HicResourceServiceClosure -RequestedServices $requested)
    $blockers = [System.Collections.Generic.List[string]]::new()
    $warnings = [System.Collections.Generic.List[string]]::new()
    $managedStackAction = 'not-required'
    $previewAction = 'not-required'
    if (-not $profileDefinition.available) {
        $blockers.Add('FullAgent is unavailable. A profile name does not authorize Worker or untrusted-code execution; security gate #63 is still required.')
    }
    if ('selected-model' -cin $services) {
        if ([string]::IsNullOrWhiteSpace($ModelService)) {
            $blockers.Add('Inference requires an explicit local model-service identity. No provider, endpoint or paid API is selected by default.')
        }
        elseif ($ModelService -cnotmatch '^[A-Za-z0-9][A-Za-z0-9_.-]{0,62}$') {
            $blockers.Add('Model-service identity must be a short local service name, not a URL or credential.')
        }
        else {
            $matchingModel = @($Inventory.containers.containers | Where-Object { $_.name -ceq $ModelService -or $_.service -ceq $ModelService })
            if ($matchingModel.Count -gt 0) {
                $Inventory.modelService = [ordered]@{
                    state = if (@($matchingModel | Where-Object { $_.state -eq 'running' }).Count -gt 0) { 'external-running' } else { 'external-not-running' }
                    identity = $ModelService
                    ownership = 'external-or-unknown'
                }
            }
            else {
                $Inventory.modelService = [ordered]@{
                    state = 'not-detected'
                    identity = $ModelService
                    ownership = 'external-or-unknown'
                }
            }
            $blockers.Add('Local model lifecycle is deferred: no model is started, stopped or unloaded by Phase 1.')
        }
    }
    if ($Task -eq 'tests') {
        $blockers.Add('Tests require an isolated disposable database. Phase 1 never creates or reuses a persistent project database as a test fixture.')
    }
    if (@($services | Where-Object { $_ -in @('native-host', 'web-ui') }).Count -gt 0) {
        if ($Inventory.preview.nativeHost -eq 'ready' -and $Inventory.preview.webUI -eq 'ready') {
            $previewAction = 'reuse'
        }
        elseif ($Inventory.preview.legacyNative -and $Inventory.preview.legacyWebUI) {
            $previewAction = 'reuse-legacy'
        }
        elseif ($Inventory.preview.legacyNative -or $Inventory.preview.legacyWebUI) {
            $previewAction = 'blocked'
            $blockers.Add('Partial legacy HIC preview detected; no process will be started or replaced.')
        }
        else {
            $previewDecision = Get-HicPreviewDecision -State @{
                nativeReady = ($Inventory.preview.nativeHost -eq 'ready')
                webReady = ($Inventory.preview.webUI -eq 'ready')
                nativePortState = $Inventory.preview.nativePortState
                webPortState = $Inventory.preview.webUIPortState
            }
            $previewAction = $previewDecision.action
            if ($previewAction -eq 'blocked') { $blockers.Add($previewDecision.reason) }
        }
    }
    $workerStateNeedsReview = $Inventory.worker -eq 'running' -or
        ($Inventory.worker -eq 'unknown' -and $Inventory.podmanMachine -eq 'running')
    if ($workerStateNeedsReview -and @($services | Where-Object { $_ -in @('control-plane', 'agent-worker', 'executor') }).Count -gt 0) {
        $blockers.Add("Worker state is $($Inventory.worker). Infrastructure activation is blocked; no Worker action will be taken.")
    }
    if (@($services | Where-Object { $_ -in @('control-plane', 'postgres', 'redis') }).Count -gt 0) {
        if ($Inventory.podmanMachine -ne 'running') {
            $blockers.Add('Podman machine is not verified running; no machine start or WSL change is permitted.')
        }
        $externalPostgres = @($Inventory.containers.containers | Where-Object {
            $_.service -ceq 'postgres' -and $_.state -eq 'running' -and $_.ownership -ne 'hic-managed-verified'
        })
        if ($externalPostgres.Count -gt 0) {
            $blockers.Add('A running external PostgreSQL service is present. It is protected; explicit reviewed reuse is not configured, so no second database will be started.')
        }
        $hostDatabaseListener = @($Inventory.listeners.protectedListeners | Where-Object { $_.port -eq 5432 })
        if ($hostDatabaseListener.Count -gt 0) {
            $blockers.Add('A host listener uses the PostgreSQL port 5432. Its ownership/health is unverified; no second database will be started.')
        }
        $hostRedisListener = @($Inventory.listeners.protectedListeners | Where-Object { $_.port -eq 6379 })
        if ($hostRedisListener.Count -gt 0) {
            $blockers.Add('A host listener uses the Redis port 6379. It is protected and will not be adopted or replaced.')
        }
        $hostControlPlaneListener = @($Inventory.listeners.protectedListeners |
            Where-Object { $_.port -eq $Inventory.configuredPorts.controlPlane })
        if ($hostControlPlaneListener.Count -gt 0 -and $Inventory.managedOwnership.state -ne 'verified') {
            $blockers.Add('The configured Control Plane port is occupied by a resource without verified HIC ownership.')
        }
        $externalRedis = @($Inventory.containers.containers | Where-Object {
            $_.service -ceq 'redis' -and $_.state -eq 'running' -and $_.ownership -ne 'hic-managed-verified'
        })
        if ($externalRedis.Count -gt 0) {
            $blockers.Add('A running external Redis service is present. It is protected and will not be adopted or replaced.')
        }
        if ($Inventory.managedOwnership.state -eq 'verified') {
            $managedStackAction = 'resume-owned'
        }
        elseif ($Inventory.managedOwnership.state -eq 'absent' -and
            $Inventory.containers.state -eq 'verified' -and
            $Inventory.containers.containers.Count -eq 0 -and
            $Inventory.containers.volumes.Count -eq 0 -and
            -not $Inventory.managedOwnership.recordPresent -and
            -not $Inventory.managedOwnership.credentialsPresent -and
            $hostDatabaseListener.Count -eq 0) {
            $managedStackAction = 'bootstrap'
        }
        else {
            $managedStackAction = 'blocked'
            $blockers.Add('Existing resources or incomplete inventory prevent safe HIC-managed bootstrap/resume; external services and volumes are never adopted.')
        }
    }
    if ($Inventory.worker -in @('running', 'unknown') -and @($services | Where-Object { $_ -in @('control-plane', 'postgres', 'redis') }).Count -gt 0) {
        $warnings.Add("Worker state is $($Inventory.worker); it remains disabled and is never part of a Phase 1 start.")
    }
    if ($blockers.Count -gt 0 -and $managedStackAction -ne 'not-required') {
        $managedStackAction = 'blocked'
    }

    $serviceRows = @($services | ForEach-Object {
        $definition = Get-HicResourceProperty (Get-HicResourceDefinitions).services $_
        [pscustomobject]@{
            name = $_
            dependsOn = @($definition.dependsOn)
            kind = $definition.kind
            state = Get-HicResourceServiceCurrentState -Service $_ -Inventory $Inventory
            ownership = if ($definition.managed -and $_ -in @($Inventory.managedOwnership.ownedContainerNames |
                ForEach-Object {
                    if ($_ -eq 'hic-managed-postgres') { 'postgres' }
                    if ($_ -eq 'hic-managed-redis') { 'redis' }
                    if ($_ -eq 'hic-managed-control-plane') { 'control-plane' }
                })) { 'hic-managed-verified' } else { 'external-or-unowned' }
        }
    })
    $budget = [ordered]@{
        minimum = [int]$profileDefinition.memoryBudgetGiB.minimum
        maximum = [int]$profileDefinition.memoryBudgetGiB.maximum
        selectedGiB = if ($MemoryBudgetGiB -gt 0) { $MemoryBudgetGiB } else { $null }
        guaranteed = $false
        enforcement = 'advisory-only'
    }
    $protectedPorts = @($Inventory.listeners.reservedPreviewPorts)
    $protectedServicePorts = @($Inventory.listeners.reservedServicePorts)
    $activeProtectedPorts = @($Inventory.protectedPreviews | ForEach-Object { $_.port } | Sort-Object -Unique)
    $resourceChanges = [ordered]@{
        startsHostProcesses = @()
        startsExistingContainers = @()
        createsContainers = @()
        createsVolumes = @()
        runsOneShotServices = @()
        schemaInitialization = 'none'
    }
    if ($managedStackAction -eq 'bootstrap') {
        $resourceChanges.createsContainers = @('hic-managed-postgres', 'hic-managed-redis', 'hic-managed-control-plane')
        $resourceChanges.createsVolumes = @('hic-managed-postgres-data', 'hic-managed-redis-data')
        $resourceChanges.runsOneShotServices = @('schema-init')
        $resourceChanges.schemaInitialization = 'guarded v2 bootstrap on the new empty HIC-managed database only'
    }
    elseif ($managedStackAction -eq 'resume-owned') {
        $resourceChanges.startsExistingContainers = @($serviceRows | Where-Object {
            $_.name -in @('postgres', 'redis', 'control-plane') -and $_.state -ne 'running'
        } | ForEach-Object {
            if ($_.name -eq 'postgres') { 'hic-managed-postgres' }
            if ($_.name -eq 'redis') { 'hic-managed-redis' }
            if ($_.name -eq 'control-plane') { 'hic-managed-control-plane' }
        })
    }
    if ($previewAction -eq 'launch') {
        $resourceChanges.startsHostProcesses = @('hic-native-host', 'vite-web-ui')
    }
    $fingerprint = [ordered]@{
        profile = $Profile
        task = $Task
        modelService = $ModelService
        services = $services
        previewAction = $previewAction
        configuredMemoryBudgetGiB = $MemoryBudgetGiB
        configuredTestParallelism = $TestParallelism
        configuredInferenceParallelism = $InferenceParallelism
        profileMemoryBudget = $profileDefinition.memoryBudgetGiB
        profileTestParallelism = $profileDefinition.testParallelism
        profileInferenceParallelism = $profileDefinition.inferenceParallelism
        resourceChanges = $resourceChanges
        worker = $Inventory.worker
        podmanMachine = $Inventory.podmanMachine
        podmanMachineName = $Inventory.podmanMachineName
        ownership = $Inventory.managedOwnership.state
        ownershipRecordPresent = $Inventory.managedOwnership.recordPresent
        ownershipCredentialsPresent = $Inventory.managedOwnership.credentialsPresent
        managedStackAction = $managedStackAction
        containers = @($Inventory.containers.containers | Sort-Object name | ForEach-Object {
            "$($_.id)|$($_.name)|$($_.state)|$($_.project)|$($_.service)|$($_.ownership)"
        })
        volumes = @($Inventory.containers.volumes | Sort-Object name | ForEach-Object {
            "$($_.name)|$($_.ownership)"
        })
        protectedListeners = @($Inventory.listeners.protectedListeners | Sort-Object port, process | ForEach-Object {
            "$($_.port)|$($_.process)|$($_.ownership)"
        })
        protectedPorts = $protectedPorts
        protectedServicePorts = $protectedServicePorts
        activeProtectedPorts = $activeProtectedPorts
        preview = $Inventory.preview
    }
    $bytes = [System.Text.Encoding]::UTF8.GetBytes(($fingerprint | ConvertTo-Json -Compress -Depth 8))
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { $planId = 'HIC76-' + ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').Substring(0, 16).ToLowerInvariant() }
    finally { $sha.Dispose() }

    return [ordered]@{
        operation = 'profile-plan'
        planId = $planId
        profile = $Profile
        task = $Task
        defaultProfile = 'Light'
        available = [bool]$profileDefinition.available
        requestedServices = $requested
        services = $serviceRows
        managedStackAction = $managedStackAction
        previewAction = $previewAction
        podmanMachineName = $Inventory.podmanMachineName
        resourceChanges = $resourceChanges
        protectedPreviews = $Inventory.protectedPreviews
        protectedPreviewPorts = $protectedPorts
        protectedServicePorts = $protectedServicePorts
        memoryBudgetGiB = $budget
        budgetIsGuarantee = $false
        testParallelism = if ($TestParallelism -gt 0) { $TestParallelism } else { [int]$profileDefinition.testParallelism }
        inferenceParallelism = if ($InferenceParallelism -gt 0) { $InferenceParallelism } else { [int]$profileDefinition.inferenceParallelism }
        measuredMemory = [ordered]@{
            host = $Inventory.hostMemory
            podmanContainers = $Inventory.containers.memory
            accounting = $Inventory.memoryAccounting
        }
        worker = $Inventory.worker
        mutationsAuthorized = $false
        blockers = $blockers.ToArray()
        warnings = $warnings.ToArray()
    }
}

function Get-HicResourceStartDecision {
    param([System.Collections.IDictionary]$Plan, [System.Collections.IDictionary]$Inventory, [string]$ApprovedPlanId)
    if ($Plan.available -ne $true) {
        return [pscustomobject]@{ action = 'blocked'; reason = 'Requested profile is unavailable.' }
    }
    if ($Plan.blockers.Count -gt 0) {
        return [pscustomobject]@{ action = 'blocked'; reason = [string]$Plan.blockers[0] }
    }
    if ([string]::IsNullOrWhiteSpace($ApprovedPlanId)) {
        return [pscustomobject]@{ action = 'blocked'; reason = 'Run profile plan, review its protected resources, then pass the exact -PlanId with -Apply.' }
    }
    if ($ApprovedPlanId -cne $Plan.planId) {
        return [pscustomobject]@{ action = 'blocked'; reason = 'Inventory or selection changed since planning. Review a new profile plan before activation.' }
    }
    if ($Inventory.worker -eq 'running' -or
        ($Inventory.worker -eq 'unknown' -and $Inventory.podmanMachine -eq 'running')) {
        return [pscustomobject]@{ action = 'blocked'; reason = "Worker state is $($Inventory.worker); no new development services will start." }
    }
    if ($Plan.task -eq 'tests' -or $Plan.task -eq 'inference') {
        return [pscustomobject]@{ action = 'blocked'; reason = 'Test-database and local-model lifecycle are not activated in Phase 1.' }
    }
    $hasInfrastructure = @($Plan.services | Where-Object { $_.name -in @('control-plane', 'postgres', 'redis') }).Count -gt 0
    $hasPreview = @($Plan.services | Where-Object { $_.name -in @('native-host', 'web-ui') }).Count -gt 0
    $preview = $null
    if ($hasPreview) {
        if ($Inventory.preview.nativeHost -eq 'ready' -and $Inventory.preview.webUI -eq 'ready') {
            $preview = [pscustomobject]@{ action = 'reuse'; reason = 'Configured HIC preview is already healthy.' }
        }
        elseif ($Inventory.preview.legacyNative -and $Inventory.preview.legacyWebUI) {
            $preview = [pscustomobject]@{ action = 'reuse-legacy'; reason = 'Verified legacy HIC preview is reused; no second Native Host/UI is started.' }
        }
        elseif ($Inventory.preview.legacyNative -or $Inventory.preview.legacyWebUI) {
            return [pscustomobject]@{ action = 'blocked'; reason = 'Partial legacy HIC preview detected; no process will be started or replaced.' }
        }
        else {
            $preview = Get-HicPreviewDecision -State @{
                nativeReady = ($Inventory.preview.nativeHost -eq 'ready')
                webReady = ($Inventory.preview.webUI -eq 'ready')
                nativePortState = $Inventory.preview.nativePortState
                webPortState = $Inventory.preview.webUIPortState
            }
        }
        if ($preview.action -eq 'blocked') {
            return [pscustomobject]@{ action = 'blocked'; reason = $preview.reason }
        }
    }
    if (-not $hasInfrastructure) {
        if ($null -eq $preview) { return [pscustomobject]@{ action = 'blocked'; reason = 'Plan contains no Phase 1 lifecycle service.' } }
        return [pscustomobject]@{ action = $preview.action; reason = $preview.reason; previewAction = $preview.action }
    }
    if ($Plan.managedStackAction -eq 'bootstrap') {
        return [pscustomobject]@{ action = 'bootstrap-clean'; reason = 'No containers, volumes or database listeners were found; an isolated new managed stack may be created after explicit confirmation.' }
    }
    if ($Plan.managedStackAction -ne 'resume-owned') {
        return [pscustomobject]@{ action = 'blocked'; reason = 'Existing HIC-managed ownership is not verified and the host is not a clean bootstrap target.' }
    }
    return [pscustomobject]@{
        action = 'resume-owned'
        reason = 'Only exact, previously registered HIC-managed services may be resumed.'
        previewAction = if ($null -ne $preview) { $preview.action } else { 'not-requested' }
    }
}

function Get-HicResourceDoctorFindings {
    param([System.Collections.IDictionary]$Inventory)
    $findings = [System.Collections.Generic.List[string]]::new()
    if ($Inventory.hostMemory.state -ne 'measured') { $findings.Add('Host memory measurement is unavailable; no estimate is substituted.') }
    if ($Inventory.containers.memory.state -ne 'measured') { $findings.Add('Podman container memory is unknown or incomplete; container and host memory are not combined.') }
    if ($Inventory.podmanMachine -eq 'unresponsive') { $findings.Add('Podman inventory exceeded a bounded timeout; no WSL/VM reset or automatic retry is authorized.') }
    if ($Inventory.managedOwnership.state -in @('stale-or-invalid', 'partial-or-stale')) { $findings.Add('HIC ownership record is stale or incomplete; all matching resources remain protected.') }
    if ($Inventory.worker -eq 'running') { $findings.Add('Worker is running unexpectedly; this profile tool does not stop or otherwise touch it.') }
    if ($Inventory.worker -eq 'unknown') { $findings.Add('Worker state is unknown; no Worker lifecycle change is allowed.') }
    if ($Inventory.protectedPreviews.Count -gt 0) {
        $ports = @($Inventory.protectedPreviews | ForEach-Object { $_.port } | Sort-Object -Unique) -join ', '
        $findings.Add("Protected preview listeners are present on port(s) $ports; they will not be reused, stopped or replaced.")
    }
    $findings.Add('No model provider/service identity is inferred; local model memory remains unknown unless explicitly selected.')
    return $findings.ToArray()
}

function Write-HicResourceReport {
    param([System.Collections.IDictionary]$Report, [switch]$Doctor)
    Write-Host ('HIC {0} - read-only resource inventory' -f $Report.operation)
    Write-Host ('Profile: {0}; task: {1}; plan: {2}' -f $Report.profile, $Report.task, $Report.planId)
    Write-Host ('Podman machine: {0}; Worker: {1}' -f $Report.inventory.podmanMachine, $Report.inventory.worker)
    Write-Host ('Host memory: {0} (host scope includes WSL/Podman)' -f $Report.inventory.hostMemory.state)
    if ($Report.inventory.hostMemory.state -eq 'measured') {
        Write-Host ('Host GiB total/used/available: {0} / {1} / {2}' -f
            $Report.inventory.hostMemory.totalGiB, $Report.inventory.hostMemory.usedGiB, $Report.inventory.hostMemory.availableGiB)
    }
    Write-Host ('Podman container memory: {0}; model memory: unknown unless explicitly identified' -f $Report.inventory.containers.memory.state)
    $containerSummary = @($Report.inventory.containers.containers | ForEach-Object {
        '{0}={1} [{2}]' -f $_.name, $_.state, $_.ownership
    }) -join ', '
    Write-Host ('Containers: {0}; {1}' -f $Report.inventory.containers.state, $containerSummary)
    $volumeSummary = @($Report.inventory.containers.volumes | ForEach-Object {
        '{0} [{1}]' -f $_.name, $_.ownership
    }) -join ', '
    Write-Host ('Volumes: {0}' -f $volumeSummary)
    $processSummary = @($Report.inventory.topProcesses.processes | Select-Object -First 6 | ForEach-Object {
        '{0}={1}MiB' -f $_.name, $_.workingSetMiB
    }) -join ', '
    Write-Host ('Top process working sets: {0}' -f $processSummary)
    if ($Report.inventory.podmanVmWorkingSet.state -eq 'measured') {
        Write-Host ('Podman/WSL VM working set: {0}MiB' -f $Report.inventory.podmanVmWorkingSet.process.workingSetMiB)
    }
    $protectedPortSummary = @($Report.inventory.protectedPreviews | ForEach-Object { $_.port }) -join ', '
    Write-Host ('Protected preview listeners: {0}' -f $protectedPortSummary)
    Write-Host ('Reserved protected ports: {0}' -f (@($Report.plan.protectedPreviewPorts) -join ', '))
    if ($Report.plan) {
        $serviceSummary = @($Report.plan.services | ForEach-Object {
            '{0}={1} [{2}]' -f $_.name, $_.state, $_.ownership
        }) -join ', '
        Write-Host ('Services: {0}' -f $serviceSummary)
        Write-Host ('Advisory budget GiB: {0}-{1}; not a guarantee' -f $Report.plan.memoryBudgetGiB.minimum, $Report.plan.memoryBudgetGiB.maximum)
        Write-Host ('Stack lifecycle plan: {0}' -f $Report.plan.managedStackAction)
        if ($Report.plan.resourceChanges.createsContainers.Count -gt 0) {
            Write-Host ('Will create containers: {0}' -f ($Report.plan.resourceChanges.createsContainers -join ', '))
            Write-Host ('Will create volumes: {0}' -f ($Report.plan.resourceChanges.createsVolumes -join ', '))
            Write-Host ('One-shot services: {0}' -f ($Report.plan.resourceChanges.runsOneShotServices -join ', '))
        }
        if ($Report.plan.resourceChanges.startsExistingContainers.Count -gt 0) {
            Write-Host ('Will resume owned containers: {0}' -f ($Report.plan.resourceChanges.startsExistingContainers -join ', '))
        }
        if ($Report.plan.resourceChanges.startsHostProcesses.Count -gt 0) {
            Write-Host ('Will start host processes: {0}' -f ($Report.plan.resourceChanges.startsHostProcesses -join ', '))
        }
        Write-Host ('Suggested parallelism tests/inference: {0}/{1}' -f $Report.plan.testParallelism, $Report.plan.inferenceParallelism)
        foreach ($blocker in $Report.plan.blockers) { Write-Warning $blocker }
    }
    if ($Doctor) {
        foreach ($finding in $Report.findings) { Write-Warning $finding }
    }
    Write-Host 'No lifecycle change was performed.'
}

function Invoke-HicResourceCommand {
    param(
        [string]$Action,
        [string]$SubAction,
        [string]$Profile,
        [string]$Task,
        [string]$ModelService,
        [string]$PlanId,
        [int]$MemoryBudgetGiB = 0,
        [int]$TestParallelism = 0,
        [int]$InferenceParallelism = 0,
        [switch]$Apply,
        [switch]$Json,
        [object]$Ports,
        [string]$Root
    )
    if ($Action -eq 'profile' -and $SubAction -ne 'plan') { throw 'Supported profile command: profile plan.' }
    if ($Action -eq 'resource' -and $SubAction -notin @('status', 'doctor', 'start', 'stop')) {
        throw 'Supported resource commands: resource status, resource doctor, resource start, resource stop.'
    }
    if ($Action -eq 'resource' -and $SubAction -eq 'stop') {
        $report = [ordered]@{
            operation = 'resource-stop'
            action = 'blocked'
            changesMade = $false
            reason = 'Phase 1 does not stop persistent services or models; safe in-flight coordination and model unload are deferred to Phase 2.'
        }
        if (-not $Json) { Write-Host $report.reason }
        return $report
    }
    if ($Action -eq 'profile' -and $Apply) { throw 'profile plan is read-only; use resource start with a reviewed plan ID to activate.' }

    $inventory = Get-HicResourceInventory -Ports $Ports
    $plan = New-HicResourcePlan -Profile $Profile -Task $Task -Inventory $inventory -ModelService $ModelService `
        -MemoryBudgetGiB $MemoryBudgetGiB -TestParallelism $TestParallelism -InferenceParallelism $InferenceParallelism
    if ($Action -eq 'profile') {
        if (-not $Json) {
            Write-HicResourceReport -Report ([ordered]@{
                operation = 'profile-plan'
                profile = $Profile
                task = $Task
                planId = $plan.planId
                inventory = $inventory
                plan = $plan
            })
        }
        return $plan
    }
    if ($SubAction -in @('status', 'doctor')) {
        $report = [ordered]@{
            operation = "resource-$SubAction"
            profile = $Profile
            task = $Task
            planId = $plan.planId
            inventory = $inventory
            plan = $plan
            findings = if ($SubAction -eq 'doctor') { @(Get-HicResourceDoctorFindings $inventory) } else { @() }
        }
        if (-not $Json) { Write-HicResourceReport -Report $report -Doctor:($SubAction -eq 'doctor') }
        return $report
    }
    if ($SubAction -eq 'start' -and -not $Apply) {
        $decision = [ordered]@{
            operation = 'resource-start'
            action = 'plan-only'
            changesMade = $false
            plan = $plan
            reason = 'No action without -Apply and the exact reviewed -PlanId.'
        }
        if (-not $Json) { Write-HicResourceReport -Report ([ordered]@{ operation='resource-start'; profile=$Profile; task=$Task; planId=$plan.planId; inventory=$inventory; plan=$plan }) }
        return $decision
    }
    if ($SubAction -eq 'start') {
        if ($Apply -and $Json) { throw 'resource start with -Apply does not support -Json because it may launch an interactive preview.' }
        $decision = Get-HicResourceStartDecision -Plan $plan -Inventory $inventory -ApprovedPlanId $PlanId
        if ($decision.action -eq 'blocked') { throw $decision.reason }
        if ($decision.action -eq 'launch') {
            $nativeLauncher = Join-Path $PSScriptRoot 'hic-native.ps1'
            if (-not (Test-Path -LiteralPath $nativeLauncher)) { throw 'Native preview launcher is missing.' }
            & $nativeLauncher
            if (-not $?) { throw 'Native preview launch failed.' }
            return [ordered]@{ operation='resource-start'; action='started'; changesMade=$true; planId=$plan.planId; services=$plan.services }
        }
        if ($decision.action -in @('reuse', 'reuse-legacy')) {
            Write-Host $decision.reason
            return [ordered]@{ operation='resource-start'; action='reused'; changesMade=$false; planId=$plan.planId; services=$plan.services }
        }
        if ($decision.action -eq 'resume-owned') {
            $managedResult = Start-HicManagedInfrastructure -Status @{
                podmanMachine = $inventory.podmanMachine
            } -ControlPlanePort $Ports.ControlPlane -ExistingOnly
            if ($managedResult.outcome -ne 'ready') { throw $managedResult.reason }
            if ($decision.previewAction -eq 'launch') {
                $nativeLauncher = Join-Path $PSScriptRoot 'hic-native.ps1'
                if (-not (Test-Path -LiteralPath $nativeLauncher)) { throw 'Native preview launcher is missing after owned infrastructure resume.' }
                & $nativeLauncher
                if (-not $?) { throw 'Native preview launch failed after managed services were resumed; those services were left intact.' }
            }
            Write-Host $managedResult.reason
            return [ordered]@{ operation='resource-start'; action='resumed-owned'; changesMade=$true; planId=$plan.planId; services=$plan.services }
        }
        if ($decision.action -eq 'bootstrap-clean') {
            $managedResult = Start-HicManagedInfrastructure -Status @{
                podmanMachine = $inventory.podmanMachine
            } -ControlPlanePort $Ports.ControlPlane
            if ($managedResult.outcome -ne 'ready') { throw $managedResult.reason }
            if ($decision.previewAction -eq 'launch') {
                $nativeLauncher = Join-Path $PSScriptRoot 'hic-native.ps1'
                if (-not (Test-Path -LiteralPath $nativeLauncher)) { throw 'Native preview launcher is missing after clean managed bootstrap.' }
                & $nativeLauncher
                if (-not $?) { throw 'Native preview launch failed after managed services were created; those services were left intact.' }
            }
            Write-Host $managedResult.reason
            return [ordered]@{ operation='resource-start'; action='bootstrapped-clean'; changesMade=$true; planId=$plan.planId; services=$plan.services }
        }
    }
    throw 'Resource lifecycle decision was not recognized; no action was taken.'
}
