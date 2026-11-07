# Pure startup decision — no commands, file writes or process starts.
# Used by hic.ps1 up and by Windows CI with synthetic snapshots.
function Get-HicStartupDecision {
    param([System.Collections.IDictionary]$State)

    $newNative = $State.nativeHost -eq 'ready'
    $newUI = $State.webUI -eq 'ready'
    $oldNative = [bool]$State.legacyNative
    $oldUI = [bool]$State.legacyWebUI

    if ($newNative -and $newUI) {
        return [pscustomobject]@{ action = 'reuse'; reason = 'Configured HIC session already healthy.' }
    }
    if ($newNative -or $newUI) {
        return [pscustomobject]@{ action = 'blocked'; reason = 'Partial configured Native Host/UI session. Refusing to replace or terminate processes.' }
    }
    if ($oldNative -xor $oldUI) {
        return [pscustomobject]@{ action = 'blocked'; reason = 'Partial legacy Native Host/UI session. Refusing to start a second session.' }
    }
    if ($oldNative -and $oldUI) {
        return [pscustomobject]@{ action = 'reuse-legacy'; reason = 'Legacy HIC preview is healthy.' }
    }
    if ($State.nativePortState -ne 'free' -or $State.webUIPortState -ne 'free') {
        return [pscustomobject]@{ action = 'blocked'; reason = 'Configured Native Host/UI port is occupied or cannot be inspected. Choose available ports; no process is stopped.' }
    }

    # Control Plane and Worker state intentionally do not gate the read-only preview.
    # This function never grants permission for task execution or infrastructure recovery.
    return [pscustomobject]@{ action = 'launch-native'; reason = 'Ready to start read-only Native Host/UI (degraded mode is supported).' }
}
