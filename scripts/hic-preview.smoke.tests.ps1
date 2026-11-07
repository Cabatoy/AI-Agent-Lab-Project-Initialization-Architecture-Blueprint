# Isolated Windows CI smoke: start ONLY test-owned Native Host + Vite, with CP offline.
# No WSL, Podman, Worker, database, local user machine or task creation.
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$ui = Join-Path $root 'agent-lab\hic-web-ui'
$project = Join-Path $root 'agent-lab\hic-native-host\Hic.NativeHost.csproj'
$dll = Join-Path $root 'agent-lab\hic-native-host\bin\Release\net10.0\Hic.NativeHost.dll'
$vite = Join-Path $ui 'node_modules\vite\bin\vite.js'
$outDir = $env:RUNNER_TEMP
if (-not $outDir) { throw 'Only run this smoke test on an isolated CI runner.' }
$native = $null
$web = $null

function Test-Ready([string]$Url) {
  try {
    $r = Invoke-WebRequest -Uri $Url -TimeoutSec 2 -UseBasicParsing -ErrorAction Stop
    return [int]$r.StatusCode -eq 200
  } catch { return $false }
}
function Wait-Ready([string]$Url, [System.Diagnostics.Process]$Process) {
  for ($i = 0; $i -lt 45; $i++) {
    if (Test-Ready $Url) { return }
    if ($Process.HasExited) { throw "CI test child exited before responding: $Url" }
    Start-Sleep -Milliseconds 400
  }
  throw "CI test child was not ready: $Url"
}
function Stop-TestChild([System.Diagnostics.Process]$Process) {
  if ($null -eq $Process) { return }
  try {
    $Process.Refresh()
    if (-not $Process.HasExited) {
      & "$env:SystemRoot\System32\taskkill.exe" /T /F /PID $Process.Id 2>$null | Out-Null
    }
  } catch { } finally { $Process.Dispose() }
}

try {
  $env:HIC_CONTROL_PLANE_PORT = '21480'
  $env:HIC_NATIVE_PORT = '21481'
  $env:HIC_WEB_PORT = '21482'
  $env:HIC_NATIVE_URL = 'http://127.0.0.1:21481'
  $env:HIC_API_URL = 'http://127.0.0.1:21480'
  $env:HIC_WORKSPACE_ROOT = $root
  $bytes = New-Object byte[] 32
  $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
  try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
  $env:HIC_NATIVE_TOKEN = [Convert]::ToBase64String($bytes)
  [Array]::Clear($bytes, 0, $bytes.Length)

  & dotnet build $project -c Release -nologo
  if ($LASTEXITCODE -ne 0) { throw 'Native Host CI build failed.' }

  Push-Location $ui
  try {
    & npm.cmd ci --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw 'Locked UI dependency install failed.' }
  } finally { Pop-Location }

  $native = Start-Process -FilePath (Get-Command dotnet).Source -ArgumentList ('"' + $dll + '"') -WorkingDirectory $root -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $outDir 'hic-native.out') -RedirectStandardError (Join-Path $outDir 'hic-native.err')
  Wait-Ready 'http://127.0.0.1:21481/v1/status' $native

  $web = Start-Process -FilePath (Get-Command node).Source -ArgumentList @('"' + $vite + '"','--host','127.0.0.1','--port','21482','--strictPort') -WorkingDirectory $ui -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $outDir 'hic-vite.out') -RedirectStandardError (Join-Path $outDir 'hic-vite.err')
  Wait-Ready 'http://127.0.0.1:21482/' $web

  $nativeStatus = Invoke-RestMethod 'http://127.0.0.1:21481/v1/status' -TimeoutSec 3
  if ($nativeStatus.service -cne 'hic-native-host' -or $nativeStatus.taskExecutionEnabled -ne $false) { throw 'Native Host contract failed.' }

  $index = Invoke-WebRequest 'http://127.0.0.1:21482/' -TimeoutSec 3 -UseBasicParsing
  if ($index.Content -notlike '*<title>HIC | Control Dashboard</title>*') { throw 'Vite did not serve HIC.' }
  $module = Invoke-WebRequest 'http://127.0.0.1:21482/src/CommandCenter.tsx' -TimeoutSec 3 -UseBasicParsing
  if ($module.Content -notlike '*Unsent input stored locally*') { throw 'Updated offline-capable Command Center is not served.' }

  $headers = @{ Origin='http://127.0.0.1:21482'; 'Sec-Fetch-Site'='same-origin'; 'X-HIC-Request'='native-action' }
  $nativeAction = Invoke-RestMethod 'http://127.0.0.1:21482/api/native/actions' -Method Post -TimeoutSec 5 -Headers $headers -ContentType 'application/json' -Body '{"action":"workspace.inspect"}'
  if ($nativeAction.action -cne 'workspace.inspect' -or $nativeAction.workspaceAvailable -ne $true) { throw 'Read-only native proxy failed.' }

  $http = [System.Net.Http.HttpClient]::new()
  try {
    $http.Timeout = [TimeSpan]::FromSeconds(7)
    $response = $http.GetAsync('http://127.0.0.1:21482/api/tasks').GetAwaiter().GetResult()
    try {
      if ([int]$response.StatusCode -ne 502) { throw "Offline CP must not invent task data: got $([int]$response.StatusCode)." }
    } finally { $response.Dispose() }
  } finally { $http.Dispose() }
  Write-Host 'PASS: Windows Native Host + new Web UI + read-only native proxy + unavailable CP behavior; no Worker.'
} finally {
  Stop-TestChild $web
  Stop-TestChild $native
  foreach ($key in @('HIC_CONTROL_PLANE_PORT','HIC_NATIVE_PORT','HIC_WEB_PORT','HIC_NATIVE_URL','HIC_API_URL','HIC_WORKSPACE_ROOT','HIC_NATIVE_TOKEN')) {
    [Environment]::SetEnvironmentVariable($key,$null)
  }
}
