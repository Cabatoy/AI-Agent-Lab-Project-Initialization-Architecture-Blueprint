# Pure offline-testable decision for an independent updated native preview.
function Get-HicPreviewDecision {
    param([System.Collections.IDictionary]$State)
    if ($State.nativeReady -and $State.webReady) {
        return [pscustomobject]@{ action='reuse'; reason='Both configured native preview endpoints identify as HIC.' }
    }
    if ($State.nativeReady -or $State.webReady) {
        return [pscustomobject]@{ action='blocked'; reason='Partial configured Native Host/UI session. No process will be replaced.' }
    }
    if ($State.nativePortState -ne 'free' -or $State.webPortState -ne 'free') {
        return [pscustomobject]@{ action='blocked'; reason='Configured native preview port occupied or unverified; refusing to replace listeners.' }
    }
    return [pscustomobject]@{ action='launch'; reason='New loopback-only Native Host/UI can run beside existing legacy preview.' }
}
