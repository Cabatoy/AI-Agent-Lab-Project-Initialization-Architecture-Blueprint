param(
    [Parameter(Mandatory=$true)][int]$BrowserProcessId,
    [Parameter(Mandatory=$true)][string]$OperatorTokenPath,
    [Parameter(Mandatory=$true)][string]$UiOrigin,
    [Parameter(Mandatory=$true)][string]$ApiOrigin,
    [Parameter(Mandatory=$true)][string]$ExpectedRevision
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
foreach ($origin in @($UiOrigin, $ApiOrigin)) {
    $uri = [uri]$origin
    if ($uri.Scheme -ne 'http' -or $uri.Host -ne '127.0.0.1' -or
        $uri.AbsolutePath -ne '/' -or $uri.Query -or $uri.Fragment -or $uri.UserInfo) {
        throw 'Operator access requires exact credential-free loopback UI/API origins.'
    }
}
$UiOrigin = $UiOrigin.TrimEnd('/')
$ApiOrigin = $ApiOrigin.TrimEnd('/')
$process = Get-Process -Id $BrowserProcessId
if ($process.ProcessName -notin @('msedge', 'chrome') -or $process.MainWindowHandle -eq 0) {
    throw 'A visible explicitly selected operator browser window is required.'
}
$root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
function Find-Element([string]$name, [System.Windows.Automation.ControlType]$type) {
    $condition = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)),
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $type)))
    $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
$address = Find-Element 'Address and search bar' ([System.Windows.Automation.ControlType]::Edit)
if ($null -eq $address) { throw 'Operator browser address bar could not be verified.' }
$addressValue = $address.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
if ($addressValue -notin @("$UiOrigin/#operator-access", "$($UiOrigin.Substring(7))/#operator-access")) {
    throw 'Operator browser is not on the exact authorized HIC URL.'
}
$input = Find-Element 'Local operator bootstrap token' ([System.Windows.Automation.ControlType]::Edit)
$login = Find-Element 'Open conversation workspace' ([System.Windows.Automation.ControlType]::Button)
if ($null -eq $input -or $null -eq $login -or !$input.Current.IsPassword -or !$input.Current.IsEnabled) {
    throw 'Existing HIC password login is not ready. No authentication or approval was bypassed.'
}
$bytes = [Security.Cryptography.ProtectedData]::Unprotect(
    [IO.File]::ReadAllBytes($OperatorTokenPath), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
try {
    $payload = @{ bootstrapToken = [Convert]::ToBase64String($bytes) } | ConvertTo-Json -Compress
    $session = Invoke-RestMethod -Method Post -Uri "$ApiOrigin/local-runner/session" `
        -ContentType 'application/json' -Body $payload -MaximumRedirection 0 -TimeoutSec 10
    $authorization = @{ Authorization = "Bearer $($session.accessToken)" }
    $direct = Invoke-RestMethod -Uri "$ApiOrigin/local-runner/github/status" `
        -Headers $authorization -MaximumRedirection 0 -TimeoutSec 10
    $authorization['Origin'] = $UiOrigin
    $authorization['X-HIC-Request'] = 'local-runner'
    $proxied = Invoke-RestMethod -Uri "$UiOrigin/api/local-runner/github/status" `
        -Headers $authorization -MaximumRedirection 0 -TimeoutSec 10
    foreach ($status in @($direct, $proxied)) {
        if ($status.runtimeRevision -ne $ExpectedRevision -or $status.repository -ne 'Cabatoy/hic-agent-sandbox' -or
            $status.appId -ne '5671141' -or $status.installationId -ne '179947607' -or !$status.configured) {
            throw 'Operator UI/API does not match the pinned sandbox publisher.'
        }
    }
    $input.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue([Convert]::ToBase64String($bytes))
} finally {
    if ($null -ne $session -and $null -ne $authorization) {
        try {
            $null = Invoke-RestMethod -Method Post -Uri "$ApiOrigin/local-runner/session/revoke" `
                -Headers $authorization -MaximumRedirection 0 -TimeoutSec 10
        } catch { Write-Warning 'Temporary runtime verification session could not be revoked; it expires within 15 minutes.' }
    }
    [Array]::Clear($bytes, 0, $bytes.Length)
    $payload = $null
    $session = $null
    $authorization = $null
}
$deadline = [DateTime]::UtcNow.AddSeconds(10)
while (!$login.Current.IsEnabled -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
if (!$login.Current.IsEnabled) { throw 'Existing HIC login did not accept the protected input.' }
$login.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Write-Output 'Existing operator login invoked in the visible human browser. No Approve, Reject or Execute control was invoked.'
