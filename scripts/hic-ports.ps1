# Host-only HIC port profile; container-internal ports remain unchanged.
# Each machine can override HIC_CONTROL_PLANE_PORT, HIC_NATIVE_PORT, HIC_WEB_PORT.
function Get-HicPorts {
    $defaults = [ordered]@{
        ControlPlane = @{ Env = 'HIC_CONTROL_PLANE_PORT'; Default = 19480 }
        NativeHost   = @{ Env = 'HIC_NATIVE_PORT'; Default = 19481 }
        WebUI        = @{ Env = 'HIC_WEB_PORT'; Default = 19482 }
    }
    $result = [ordered]@{}
    foreach ($entry in $defaults.GetEnumerator()) {
        $raw = [Environment]::GetEnvironmentVariable($entry.Value.Env)
        $value = $entry.Value.Default
        if (-not [string]::IsNullOrEmpty($raw)) {
            if ($raw -cnotmatch '^[0-9]{4,5}$') {
                throw "$($entry.Value.Env) must be a decimal port in range 1024..65535."
            }
            $value = [int]$raw
        }
        if ($value -lt 1024 -or $value -gt 65535) {
            throw "$($entry.Value.Env) must be a decimal port in range 1024..65535."
        }
        $result[$entry.Key] = $value
    }
    if ((@($result.Values | Select-Object -Unique)).Count -ne 3) {
        throw 'HIC_CONTROL_PLANE_PORT, HIC_NATIVE_PORT and HIC_WEB_PORT must be distinct.'
    }
    return [pscustomobject]$result
}
