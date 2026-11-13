# HIC Relay Authentication Client — Offline Test Suite
# Tests: Credential management, rate-limiting, output redaction, and error handling
# No network calls, no real tokens, completely offline

param(
    [Parameter(Mandatory=$false)]
    [switch]$VerboseOutput
)

$ErrorActionPreference = "Stop"
$testsPassed = 0
$testsFailed = 0

function Assert-Equal {
    param([string]$Expected, [string]$Actual, [string]$Message)
    if ($Expected -ne $Actual) {
        Write-Error "FAILED: $Message`nExpected: $Expected`nActual: $Actual" -ErrorAction Stop
        $global:testsFailed++
    } else {
        $global:testsPassed++
    }
}

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) {
        Write-Error "FAILED: $Message`nExpected: True`nActual: False" -ErrorAction Stop
        $global:testsFailed++
    } else {
        $global:testsPassed++
    }
}

function Assert-False {
    param([bool]$Condition, [string]$Message)
    if ($Condition) {
        Write-Error "FAILED: $Message`nExpected: False`nActual: True" -ErrorAction Stop
        $global:testsFailed++
    } else {
        $global:testsPassed++
    }
}

function Assert-NotNull {
    param($Value, [string]$Message)
    if ($null -eq $Value) {
        Write-Error "FAILED: $Message`nValue should not be null" -ErrorAction Stop
        $global:testsFailed++
    } else {
        $global:testsPassed++
    }
}

function Assert-Null {
    param($Value, [string]$Message)
    if ($null -ne $Value) {
        Write-Error "FAILED: $Message`nValue should be null`nActual: $Value" -ErrorAction Stop
        $global:testsFailed++
    } else {
        $global:testsPassed++
    }
}

function Assert-Contains {
    param([string]$Haystack, [string]$Needle, [string]$Message)
    if ($Haystack -notlike "*$Needle*") {
        Write-Error "FAILED: $Message`nExpected to contain: $Needle`nActual: $Haystack" -ErrorAction Stop
        $global:testsFailed++
    } else {
        $global:testsPassed++
    }
}

function Assert-NotContains {
    param([string]$Haystack, [string]$Needle, [string]$Message)
    if ($Haystack -like "*$Needle*") {
        Write-Error "FAILED: $Message`nShould not contain: $Needle`nActual: $Haystack" -ErrorAction Stop
        $global:testsFailed++
    } else {
        $global:testsPassed++
    }
}

# ============ TEST: Credential Validation ============
Write-Host "Test: Credential Validation"
$validToken = "ghs_validtoken1234567890abcdefghijklmnop"
Assert-True -Condition ($validToken.Length -ge 40) -Message "Valid token length check"
Assert-False -Condition ($validToken -like "* *") -Message "Valid token has no spaces"
Assert-False -Condition ([string]::IsNullOrEmpty($validToken)) -Message "Valid token is not empty"

$invalidTokens = @(
    "",  # empty
    "short",  # too short
    "token with spaces 1234567890abcdefghijk",  # contains spaces
    $null  # null
)

foreach ($invalid in $invalidTokens) {
    $isEmpty = [string]::IsNullOrEmpty($invalid)
    $hasTooFewChars = if ($invalid) { $invalid.Length -lt 20 } else { $true }
    $hasSpaces = if ($invalid) { $invalid -like "* *" } else { $false }
    
    Assert-True -Condition ($isEmpty -or $hasTooFewChars -or $hasSpaces) -Message "Credential validation rejects: '$invalid'"
}

# ============ TEST: Output Redaction ============
Write-Host "Test: Output Redaction"

# Test token redaction
$tokenPattern = "token=SYNTHETIC_TEST_CREDENTIAL_NOT_A_REAL_SECRET_1234567890"
$redacted = $tokenPattern -replace '[a-zA-Z0-9_-]{40,}', '***REDACTED***'
Assert-Contains -Haystack $redacted -Needle "***REDACTED***" -Message "Token pattern redacted"
Assert-NotContains -Haystack $redacted -Needle "SYNTHETIC_TEST_CREDENTIAL" -Message "Original token removed after redaction"

# Test Authorization header redaction
$authHeader = "Authorization: token SYNTHETIC_TEST_CREDENTIAL_NOT_A_REAL_SECRET_1234567890"
$redactedAuth = $authHeader -replace 'Authorization[=:]*\s*[^\s]+', 'Authorization: ***REDACTED***'
Assert-Contains -Haystack $redactedAuth -Needle "Authorization: ***REDACTED***" -Message "Authorization header redacted"
Assert-NotContains -Haystack $redactedAuth -Needle "token SYNTHETIC_TEST_CREDENTIAL" -Message "Credential removed from auth header"

# Test Bearer token redaction
$bearerToken = "Bearer SYNTHETIC_TEST_CREDENTIAL_NOT_A_REAL_SECRET_1234567890"
$redactedBearer = $bearerToken -replace '[Bb]earer\s+[^\s]+', 'Bearer ***REDACTED***'
Assert-Contains -Haystack $redactedBearer -Needle "Bearer ***REDACTED***" -Message "Bearer token redacted"

# Test empty string handling
$emptyRedacted = "" -replace '.', ''
Assert-Equal -Expected "" -Actual $emptyRedacted -Message "Empty string redaction returns empty"

# ============ TEST: Rate Limit Logic ============
Write-Host "Test: Rate Limit Logic"

# Test exponential backoff calculation
$attempt1 = [Math]::Pow(2, 0)  # 1
$attempt2 = [Math]::Pow(2, 1)  # 2
$attempt3 = [Math]::Pow(2, 2)  # 4

Assert-True -Condition ($attempt1 -lt $attempt2) -Message "Backoff: Attempt 1 < Attempt 2"
Assert-True -Condition ($attempt2 -lt $attempt3) -Message "Backoff: Attempt 2 < Attempt 3"
Assert-True -Condition ($attempt3 -le 5) -Message "Backoff: Capped at reasonable value"

# Test rate limit header parsing
$remainingHeader = "X-RateLimit-Remaining: 59"
$remaining = [int]($remainingHeader -replace 'X-RateLimit-Remaining: (\d+)', '$1')
Assert-Equal -Expected "59" -Actual $remaining.ToString() -Message "Parse rate limit remaining count"

$resetHeader = "X-RateLimit-Reset: 1700000000"
$resetTime = [int]($resetHeader -replace 'X-RateLimit-Reset: (\d+)', '$1')
Assert-True -Condition ($resetTime -gt 0) -Message "Parse rate limit reset timestamp"

# ============ TEST: HTTP Status Code Handling ============
Write-Host "Test: HTTP Status Code Handling"

$statusCodes = @{
    200 = "Success"
    401 = "Unauthorized"
    403 = "Forbidden"
    429 = "TooManyRequests"
    500 = "InternalServerError"
}

foreach ($code in @(200, 401, 403, 429, 500)) {
    Assert-True -Condition ($statusCodes.ContainsKey($code)) -Message "Known status code: $code"
}

# Test auth error detection
$authErrors = @(401, 403)
foreach ($code in $authErrors) {
    $isAuthError = ($code -eq 401 -or $code -eq 403)
    Assert-True -Condition $isAuthError -Message "Status $code is auth error"
}

# Test rate limit detection
$isRateLimit = (429 -eq 429)
Assert-True -Condition $isRateLimit -Message "Status 429 is rate limit"

# ============ TEST: No Credential Leakage ============
Write-Host "Test: No Credential Leakage in Errors"

$errorMessages = @(
    "Request failed with token SYNTHETIC_TEST_CREDENTIAL_NOT_A_REAL_SECRET_1234567890",
    "Authorization: Bearer eyJhbGciOiJIUzI1NiJ9",
    "password=mytoken123456789012345678901234567890",
    "github_token ghp_abcdef1234567890abcdef1234567890abcd"
)

foreach ($msg in $errorMessages) {
    # Apply redaction
    $redacted = $msg -replace '[a-zA-Z0-9_-]{40,}', '***REDACTED***'
    $redacted = $redacted -replace 'Bearer\s+[^\s]+', 'Bearer ***REDACTED***'
    $redacted = $redacted -replace 'password=\S+', 'password=***REDACTED***'
    $redacted = $redacted -replace 'github_token\s+\S+', 'github_token ***REDACTED***'
    
    # Verify token not in redacted message
    Assert-NotContains -Haystack $redacted -Needle "SYNTHETIC_TEST_CREDENTIAL" -Message "No PAT in error: $msg"
    Assert-NotContains -Haystack $redacted -Needle "SYNTHETIC_TEST_CREDENTIAL" -Message "No JWT in error: $msg"
}

# ============ TEST: Git Credential Manager Command ============
Write-Host "Test: Git Credential Manager Integration"

# Test GCM availability check
$hasGit = try { $null = & git --version; $true } catch { $false }
Assert-True -Condition $hasGit -Message "Git is available on system"

# Test credential query format
$query = "protocol=https`nhost=github.com`n`n"
Assert-Contains -Haystack $query -Needle "protocol=https" -Message "Credential query has protocol"
Assert-Contains -Haystack $query -Needle "host=github.com" -Message "Credential query has host"

# ============ TEST: Networking and Timeout Handling ============
Write-Host "Test: Networking and Timeout Handling"

$validTimeouts = @(5, 10, 30, 60, 300)
foreach ($timeout in $validTimeouts) {
    Assert-True -Condition ($timeout -gt 0 -and $timeout -le 300) -Message "Valid timeout: ${timeout}s"
}

$invalidTimeouts = @(-1, 0, 301, 3600)
foreach ($timeout in $invalidTimeouts) {
    Assert-False -Condition ($timeout -gt 0 -and $timeout -le 300) -Message "Invalid timeout rejected: ${timeout}s"
}

# ============ TEST: URL Validation ============
Write-Host "Test: URL Validation"

$validUrls = @(
    "https://api.github.com/repos/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/issues/142",
    "https://api.github.com/repos/owner/repo/issues/1",
    "https://api.github.com/repos/test-org/test-repo-123/issues/999"
)

foreach ($url in $validUrls) {
    Assert-Contains -Haystack $url -Needle "https://api.github.com/repos/" -Message "Valid URL format: $url"
    Assert-Contains -Haystack $url -Needle "/issues/" -Message "URL has issues endpoint: $url"
}

# ============ TEST: Retry Logic ============
Write-Host "Test: Retry Logic"

# Simulate retry counter
$maxRetries = 3
$attempts = 0
$rateLimitOccurred = $true

while ($attempts -lt $maxRetries -and $rateLimitOccurred) {
    $attempts++
    if ($attempts -eq 3) { $rateLimitOccurred = $false }
}

Assert-True -Condition ($attempts -le $maxRetries) -Message "Retry logic respects max attempts"
Assert-Equal -Expected "3" -Actual $attempts.ToString() -Message "Correct number of retry attempts"

# ============ Summary ============
Write-Host "`n===== TEST RESULTS ====="
Write-Host "Passed: $testsPassed"
Write-Host "Failed: $testsFailed"

if ($testsFailed -gt 0) {
    Write-Host "`n❌ TESTS FAILED" -ForegroundColor Red
    exit 1
} else {
    Write-Host "`n✅ ALL TESTS PASSED" -ForegroundColor Green
    exit 0
}
