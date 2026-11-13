# HIC Relay Issue Fetch Wrapper
# Secure GitHub API client wrapper for Relay watcher
# Usage: .\relay-fetch-issue.ps1 -Owner "Cabatoy" -Repo "AI-Agent-Lab..." -IssueNumber 142

param(
    [Parameter(Mandatory=$true)]
    [string]$Owner,
    
    [Parameter(Mandatory=$true)]
    [string]$Repo,
    
    [Parameter(Mandatory=$true)]
    [int]$IssueNumber,
    
    [Parameter(Mandatory=$false)]
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = "Stop"

# Validate Git Credential Manager availability
function Test-GitCredentialManager {
    try {
        $output = & git credential-manager --version 2>&1
        return $LASTEXITCODE -eq 0
    }
    catch {
        return $false
    }
}

# Fetch credential using git credential-manager
function Get-GitHubCredential {
    param([string]$Host = "github.com")
    
    try {
        $process = [System.Diagnostics.Process]::new()
        $process.StartInfo.FileName = "git"
        $process.StartInfo.Arguments = "credential fill"
        $process.StartInfo.UseShellExecute = $false
        $process.StartInfo.RedirectStandardInput = $true
        $process.StartInfo.RedirectStandardOutput = $true
        $process.StartInfo.RedirectStandardError = $true
        $process.StartInfo.CreateNoWindow = $true
        
        $process.Start() | Out-Null
        
        # Send credential query
        $input = "protocol=https`nhost=$Host`n`n"
        $process.StandardInput.Write($input)
        $process.StandardInput.Close()
        
        $output = $process.StandardOutput.ReadToEnd()
        $process.WaitForExit(5000)
        
        if ($process.ExitCode -ne 0) {
            return $null
        }
        
        $credential = @{}
        foreach ($line in $output -split "`n") {
            if ($line -match "^password=(.+)$") {
                $credential["token"] = $matches[1]
            }
            elseif ($line -match "^username=(.+)$") {
                $credential["identity"] = $matches[1]
            }
        }
        
        return if ($credential["token"]) { $credential } else { $null }
    }
    catch {
        Write-Error "Credential fetch failed: $_" -ErrorAction SilentlyContinue
        return $null
    }
}

# Redact sensitive data from output
function Redact-SensitiveData {
    param([string]$Text)
    
    if ([string]::IsNullOrEmpty($Text)) {
        return $Text
    }
    
    # Redact token patterns
    $redacted = $Text -replace '[a-zA-Z0-9_-]{40,}', '***REDACTED***'
    $redacted = $redacted -replace 'Authorization[=:]*\s*[^\s]+', 'Authorization: ***REDACTED***'
    $redacted = $redacted -replace '[Bb]earer\s+[^\s]+', 'Bearer ***REDACTED***'
    
    return $redacted
}

# Main fetch logic
function Invoke-GitHubIssueFetch {
    param(
        [string]$Owner,
        [string]$Repo,
        [int]$IssueNumber,
        [int]$TimeoutSeconds
    )
    
    # Check GCM availability
    if (-not (Test-GitCredentialManager)) {
        Write-Warning "Git Credential Manager not available; proceeding without authentication"
        $credential = $null
    }
    else {
        $credential = Get-GitHubCredential
        if (-not $credential) {
            Write-Warning "No credentials found in Git Credential Manager"
        }
    }
    
    $url = "https://api.github.com/repos/$Owner/$Repo/issues/$IssueNumber"
    
    try {
        $headers = @{
            "User-Agent" = "HIC-Relay-Fetch/1.0"
            "Accept"     = "application/vnd.github.v3+json"
        }
        
        if ($credential -and $credential["token"]) {
            $headers["Authorization"] = "token $($credential['token'])"
        }
        
        $response = Invoke-WebRequest -Uri $url `
            -Headers $headers `
            -Method Get `
            -TimeoutSec $TimeoutSeconds `
            -ErrorAction Stop
        
        # Extract rate-limit info
        $remaining = $response.Headers["X-RateLimit-Remaining"]
        $reset = $response.Headers["X-RateLimit-Reset"]
        
        if ($remaining -and $remaining -eq "0") {
            Write-Warning "GitHub API rate limit approaching; Reset: $(if ($reset) { [DateTime]::UnixEpoch.AddSeconds($reset) })"
        }
        
        # Return issue data
        $content = $response.Content | ConvertFrom-Json
        return @{
            success = $true
            issue   = $content
            headers = @{
                remaining = $remaining
                reset     = $reset
            }
        }
    }
    catch [System.Net.Http.HttpRequestException] {
        $statusCode = if ($_.Exception.Response) { 
            [int]$_.Exception.Response.StatusCode 
        } else { 0 }
        
        $message = "GitHub API error (HTTP $statusCode)"
        
        # Distinguish auth errors from rate limits
        if ($statusCode -eq 401) {
            Write-Error "Authentication failed: Invalid or expired credentials" -ErrorAction Stop
        }
        elseif ($statusCode -eq 403) {
            Write-Error "Authorization failed: Insufficient scope or permission" -ErrorAction Stop
        }
        elseif ($statusCode -eq 429) {
            Write-Error "Rate limit exceeded. Retry after reset time." -ErrorAction Stop
        }
        else {
            $redacted = Redact-SensitiveData($_.Exception.Message)
            Write-Error "Request failed: $redacted" -ErrorAction Stop
        }
    }
    catch {
        $redacted = Redact-SensitiveData($_.Exception.Message)
        Write-Error "Unexpected error: $redacted" -ErrorAction Stop
    }
}

# Execute
Invoke-GitHubIssueFetch -Owner $Owner -Repo $Repo -IssueNumber $IssueNumber -TimeoutSeconds $TimeoutSeconds | ConvertTo-Json -Depth 3
