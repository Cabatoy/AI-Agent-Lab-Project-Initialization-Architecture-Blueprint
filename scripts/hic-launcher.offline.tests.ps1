# Offline contract tests for HIC Launcher. Never invokes "up" or any mutating command.
$ErrorActionPreference = 'Stop'
$paths = @(
    (Join-Path $PSScriptRoot 'hic.ps1'),
    (Join-Path $PSScriptRoot 'hic-native.ps1'),
    (Join-Path $PSScriptRoot 'hic-ports.ps1')
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
    $env:HIC_WEB_PORT = '20581'
    $output = & (Join-Path $PSScriptRoot 'hic.ps1') status -Json 2>&1
    if ($LASTEXITCODE -eq 0) { throw 'Duplicate HIC host ports must be rejected.' }
    $env:HIC_WEB_PORT = '70000'
    $output = & (Join-Path $PSScriptRoot 'hic.ps1') status -Json 2>&1
    if ($LASTEXITCODE -eq 0) { throw 'Out-of-range HIC ports must be rejected.' }
}
finally {
    foreach ($key in $previous.Keys) {
        [Environment]::SetEnvironmentVariable($key, $previous[$key])
    }
}
Write-Host 'HIC port overrides / collision validation: PASS'
