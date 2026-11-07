# One-time local launcher for the safe HIC Native Host preview.
# No database migrations, Podman operations, Worker starts or git checkout changes.
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$ui = Join-Path $root 'agent-lab\hic-web-ui'
$project = Join-Path $root 'agent-lab\hic-native-host\Hic.NativeHost.csproj'
. (Join-Path $PSScriptRoot 'hic-ports.ps1')
$ports = Get-HicPorts

if (-not (Test-Path (Join-Path $root 'HumanInControl.sln'))) { throw 'HumanInControl.sln not found. Use the checked-out HIC develop branch.' }
if (-not (Test-Path $project)) { throw 'HIC Native Host project not found.' }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET 10 SDK is required.' }
if (-not (Get-Command npm.cmd -ErrorAction SilentlyContinue)) { throw 'Node.js/npm is required.' }
foreach ($port in @($ports.NativeHost, $ports.WebUI)) {
  $tcp = New-Object System.Net.Sockets.TcpClient
  try {
    $tcp.Connect('127.0.0.1', $port)
    throw "Port $port is already in use. Existing processes will NOT be stopped."
  }
  catch [System.Net.Sockets.SocketException] { }
  finally { $tcp.Dispose() }
}

& dotnet build $project -c Release
if ($LASTEXITCODE -ne 0) { throw 'Native Host build failed. No service started.' }
Push-Location $ui
try {
  if (-not (Test-Path (Join-Path $ui 'node_modules'))) {
    & npm.cmd ci
    if ($LASTEXITCODE -ne 0) { throw 'npm ci failed. No service started.' }
  }

  $bytes = New-Object byte[] 32
  $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
  try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
  $env:HIC_NATIVE_TOKEN = [Convert]::ToBase64String($bytes)
  [Array]::Clear($bytes, 0, $bytes.Length)
  $env:HIC_WORKSPACE_ROOT = $root
  $env:HIC_NATIVE_PORT = [string]$ports.NativeHost
  $env:HIC_WEB_PORT = [string]$ports.WebUI
  $env:HIC_NATIVE_URL = "http://127.0.0.1:$($ports.NativeHost)"
  $env:HIC_API_URL = "http://127.0.0.1:$($ports.ControlPlane)"

  $native = Start-Process -FilePath 'dotnet' -ArgumentList @(
    'run', '--no-build', '--no-launch-profile', '-c', 'Release', '--project', ('"' + $project + '"')
  ) -WorkingDirectory $root -PassThru -NoNewWindow
  try {
    Start-Sleep -Seconds 2
    if ($native.HasExited) { throw 'Native Host failed to start.' }
    Write-Host ''
    Write-Host "HIC Native preview: http://127.0.0.1:$($ports.WebUI)"
    Write-Host 'Existing UI and Control Plane are untouched; only the Native Host and a new UI are launched.'
    Write-Host 'Use Command Center -> Inspect local workspace.'
    Write-Host 'Press Ctrl+C to stop this preview and its Native Host.'
    & npm.cmd run dev -- --port $ports.WebUI --strictPort
  }
  finally {
    if ($null -ne $native -and -not $native.HasExited) { Stop-Process -Id $native.Id -Force -ErrorAction SilentlyContinue }
  }
}
finally {
  Remove-Item Env:HIC_NATIVE_TOKEN -ErrorAction SilentlyContinue
  Pop-Location
}
