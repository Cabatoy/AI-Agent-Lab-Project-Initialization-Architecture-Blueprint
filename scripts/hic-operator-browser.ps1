param(
    [Parameter(Mandatory=$true)][string]$OperatorTokenPath,
    [Parameter(Mandatory=$true)][string]$PublicConfigurationPath,
    [Parameter(Mandatory=$true)][string]$UiOrigin,
    [Parameter(Mandatory=$true)][string]$ApiOrigin,
    [Parameter(Mandatory=$true)][string]$ExpectedRevision,
    [int]$Port = 19556,
    [int]$LifetimeMinutes = 60
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
foreach ($origin in @($UiOrigin, $ApiOrigin)) {
    $uri = [uri]$origin
    if ($uri.Scheme -ne 'http' -or $uri.Host -ne '127.0.0.1' -or
        $uri.AbsolutePath -ne '/' -or $uri.Query -or $uri.Fragment -or $uri.UserInfo) {
        throw 'Operator access requires exact credential-free loopback UI/API origins.'
    }
}
if ($Port -lt 1024 -or $Port -gt 65535 -or $LifetimeMinutes -lt 1 -or $LifetimeMinutes -gt 120) {
    throw 'Invalid temporary operator access port or lifetime.'
}
$UiOrigin = $UiOrigin.TrimEnd('/')
$ApiOrigin = $ApiOrigin.TrimEnd('/')
$operatorSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$expires = [DateTime]::UtcNow.AddMinutes($LifetimeMinutes)
$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add("http://localhost:$Port/")
$listener.AuthenticationSchemes = [System.Net.AuthenticationSchemes]::Negotiate -bor
    [System.Net.AuthenticationSchemes]::Anonymous
try {
    $listener.Start()
    $directory = Split-Path -Parent $PublicConfigurationPath
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    @{ sessionUrl = "http://localhost:$Port/session"; expiresAtUtc = $expires.ToString('o') } |
        ConvertTo-Json | Set-Content -LiteralPath $PublicConfigurationPath -Encoding UTF8
    Write-Output 'Temporary Windows operator access ready. No credential is present in its URL or public configuration.'
    while ($listener.IsListening) {
        $context = $listener.GetContext()
        $request = $context.Request
        $response = $context.Response
        $response.Headers['Cache-Control'] = 'no-store'
        $response.Headers['X-Content-Type-Options'] = 'nosniff'
        $response.ContentType = 'application/json; charset=utf-8'
        $body = '{"error":"operator-access-denied"}'
        try {
            if ($request.Url.Authority -ne "localhost:$Port" -or $request.Url.AbsolutePath -ne '/session' -or
                $request.Url.Query -or $request.Headers['Origin'] -ne $UiOrigin -or
                ($request.RemoteEndPoint.Address.ToString() -notin @('127.0.0.1', '::1'))) {
                $response.StatusCode = 403
            } elseif ([DateTime]::UtcNow -ge $expires) {
                $response.StatusCode = 410
                $body = '{"error":"temporary-operator-access-expired"}'
            } elseif ($request.HttpMethod -eq 'OPTIONS') {
                if ($request.Headers['Access-Control-Request-Method'] -ne 'POST' -or
                    $request.Headers['Access-Control-Request-Headers'].ToLowerInvariant() -ne 'x-hic-operator-access') {
                    $response.StatusCode = 403
                } else {
                    $response.StatusCode = 204
                    $response.Headers['Access-Control-Allow-Origin'] = $UiOrigin
                    $response.Headers['Access-Control-Allow-Credentials'] = 'true'
                    $response.Headers['Access-Control-Allow-Methods'] = 'POST'
                    $response.Headers['Access-Control-Allow-Headers'] = 'X-HIC-Operator-Access'
                    $body = ''
                }
            } elseif ($request.HttpMethod -ne 'POST' -or
                $request.Headers['X-HIC-Operator-Access'] -ne 'windows-session' -or $request.ContentLength64 -gt 0) {
                $response.StatusCode = 403
            } else {
                $response.Headers['Access-Control-Allow-Origin'] = $UiOrigin
                $response.Headers['Access-Control-Allow-Credentials'] = 'true'
                $identity = $context.User.Identity
                if ($null -eq $identity -or !$identity.IsAuthenticated) {
                    $response.StatusCode = 401
                    $response.Headers['WWW-Authenticate'] = 'Negotiate'
                } elseif ($identity -isnot [Security.Principal.WindowsIdentity] -or
                    $identity.User.Value -ne $operatorSid) {
                    $response.StatusCode = 403
                } else {
                    $bytes = [Security.Cryptography.ProtectedData]::Unprotect(
                        [IO.File]::ReadAllBytes($OperatorTokenPath), $null,
                        [Security.Cryptography.DataProtectionScope]::CurrentUser)
                    try {
                        $payload = @{ bootstrapToken = [Convert]::ToBase64String($bytes) } | ConvertTo-Json -Compress
                        $session = Invoke-RestMethod -Method Post -Uri "$ApiOrigin/local-runner/session" `
                            -ContentType 'application/json' -Body $payload -MaximumRedirection 0 -TimeoutSec 10
                        $status = Invoke-RestMethod -Uri "$ApiOrigin/local-runner/github/status" `
                            -Headers @{ Authorization = "Bearer $($session.accessToken)" } -MaximumRedirection 0 -TimeoutSec 10
                        if ($status.runtimeRevision -ne $ExpectedRevision -or
                            $status.repository -ne 'Cabatoy/hic-agent-sandbox' -or !$status.configured) {
                            throw [InvalidOperationException]::new('Operator target runtime does not match the expected isolated publisher.')
                        }
                        $body = @{ accessToken = $session.accessToken; expiresInSeconds = 900 } | ConvertTo-Json -Compress
                        $response.StatusCode = 200
                    } finally {
                        [Array]::Clear($bytes, 0, $bytes.Length)
                        $payload = $null
                        $session = $null
                    }
                }
            }
        } catch [Net.WebException], [Security.Cryptography.CryptographicException], [IO.IOException], [InvalidOperationException] {
            $response.StatusCode = 503
            $body = '{"error":"operator-access-unavailable"}'
            Write-Warning 'Operator browser access failed; credential-bearing details withheld. No approval was issued.'
        }
        try {
            $output = [Text.Encoding]::UTF8.GetBytes($body)
            $response.ContentLength64 = $output.Length
            $response.OutputStream.Write($output, 0, $output.Length)
        } finally {
            $response.Close()
            $body = $null
        }
    }
} finally { $listener.Close() }
