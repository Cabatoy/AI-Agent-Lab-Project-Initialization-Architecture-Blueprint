# Bounded Windows CLI execution. Prevent frozen WSL child processes from hanging HIC.
# Designed for fixed, trusted HIC-owned diagnostic arguments only.
function Invoke-HicBoundedProcess {
    param(
        [Parameter(Mandatory)][string]$FileName,
        [string[]]$Arguments = @(),
        [ValidateRange(250, 30000)][int]$TimeoutMs = 5000
    )
    $proc = New-Object System.Diagnostics.Process
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FileName
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    # Fixed internal arguments. Quote spaces, quote marks and Go template symbols.
    $escaped = foreach ($arg in $Arguments) {
        if ($arg -match '^[A-Za-z0-9_./:\-]+$') { $arg }
        else { '"' + ($arg.Replace('\', '\\').Replace('"', '\"')) + '"' }
    }
    $psi.Arguments = ($escaped -join ' ')
    $proc.StartInfo = $psi
    try {
        if (-not $proc.Start()) { throw 'Could not create bounded subprocess.' }
        $outTask = $proc.StandardOutput.ReadToEndAsync()
        $errTask = $proc.StandardError.ReadToEndAsync()
        if (-not $proc.WaitForExit($TimeoutMs)) {
            # Only terminate the exact child tree created by this invocation.
            try {
                $kill = Start-Process -FilePath 'taskkill.exe' -ArgumentList @('/T','/F','/PID',[string]$proc.Id) -PassThru -WindowStyle Hidden -ErrorAction Stop
                try {
                    if (-not $kill.WaitForExit(1500)) { $kill.Kill() }
                }
                finally { $kill.Dispose() }
            }
            catch { }
            try {
                if (-not $proc.HasExited) { $proc.Kill() }
            }
            catch { }
            return [pscustomobject]@{ Success = $false; TimedOut = $true; ExitCode = -1; Lines = @() }
        }
        if (-not $outTask.Wait(1000) -or -not $errTask.Wait(1000)) {
            return [pscustomobject]@{ Success = $false; TimedOut = $true; ExitCode = -1; Lines = @() }
        }
        $lines = @($outTask.Result -split '\r?\n' | Where-Object { $_.Length -gt 0 })
        return [pscustomobject]@{
            Success = ($proc.ExitCode -eq 0)
            TimedOut = $false
            ExitCode = $proc.ExitCode
            Lines = $lines
        }
    }
    finally {
        $proc.Dispose()
    }
}

function Invoke-HicBoundedPodman {
    param([string[]]$Arguments, [int]$TimeoutMs = 5000)
    $binary = Get-Command podman.exe -ErrorAction SilentlyContinue
    if ($null -eq $binary -or -not $binary.Source) {
        return [pscustomobject]@{ Success = $false; TimedOut = $false; ExitCode = -1; Lines = @() }
    }
    return Invoke-HicBoundedProcess -FileName $binary.Source -Arguments $Arguments -TimeoutMs $TimeoutMs
}
