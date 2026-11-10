#Requires -Version 5.1
<#
Developer-only HIC relay bootstrap. Never runs automatically.
Modes:
 Plan (default): no mutations
 SetupAck: one-time DPAPI secret, typed locally, never log or commit it
 ReadOnlyOnce, ReadOnlyWatch: public GET only
 AckOnce, AckWatch: opt-in public GET + POST synthetic receipt to Issue #97
 InstallAutostart: explicit per-user logon task, no administrator required
#>
[CmdletBinding()]
param(
  [ValidateSet('Plan','SetupAck','ReadOnlyOnce','ReadOnlyWatch','AckOnce','AckWatch','InstallAutostart')]
  [string]$Mode='Plan'
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if($env:OS -ne 'Windows_NT') { throw 'Windows only: per-user DPAPI and Task Scheduler required.' }
$toolRoot=Split-Path -Parent $PSScriptRoot
$nodeExe=(Get-Command node.exe -ErrorAction Stop).Source
$stateDir=Join-Path $env:LOCALAPPDATA 'HICDevRelay'
$secretFile=Join-Path $stateDir 'ack-token.dpapi'
$taskName='HIC-Developer-Relay-Ack-OptIn'
switch ($Mode) {
 'Plan' {
   Write-Host 'ReadOnlyOnce/Watch: anonymous GitHub GET; no execution.'
   Write-Host 'SetupAck: asks for a fine-grained PAT with Issues read/write ONLY on the single personal repository, DPAPI per user.'
   Write-Host 'AckOnce/Watch: explicitly publishes only synthetic seen-untrusted receipts to Issue #97.'
   Write-Host 'InstallAutostart: requires explicit local confirmation and existing DPAPI secret; at-logon only.'
   break
 }
 'SetupAck' {
   New-Item -ItemType Directory -Force -Path $stateDir | Out-Null
   if(Test-Path -LiteralPath $secretFile) {throw 'Secret already exists. Stop and review; no overwrite or token rotation performed.'}
   Write-Host 'Create a fine-grained GitHub PAT: only this repository, Issues=Read/Write, metadata=read. No Contents, Actions, PR, admin permissions.'
   Write-Host 'Token will be encrypted with Windows DPAPI CurrentUser, not displayed or checked in.'
   $secret=Read-Host 'Paste PAT locally (hidden)' -AsSecureString
   $encrypted=ConvertFrom-SecureString -SecureString $secret
   [System.IO.File]::WriteAllText($secretFile,$encrypted,[System.Text.Encoding]::UTF8)
   Write-Host 'User-protected credential file created. No network call.'
   break
 }
 'ReadOnlyOnce' {
   & $nodeExe (Join-Path $toolRoot 'src/cli.mjs') '--enable-read-only' '--once'
   if($LASTEXITCODE -ne 0){throw 'Read-only poll failed.'}
   break
 }
 'ReadOnlyWatch' {
   & $nodeExe (Join-Path $toolRoot 'src/cli.mjs') '--enable-read-only' '--watch'
   break
 }
 {$_ -in 'AckOnce','AckWatch'} {
   if(!(Test-Path -LiteralPath $secretFile -PathType Leaf)){throw 'Ack token not configured. Run SetupAck locally first.'}
   $encrypted=[System.IO.File]::ReadAllText($secretFile,[System.Text.Encoding]::UTF8).Trim()
   $secure=ConvertTo-SecureString $encrypted
   $bstr=[Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
   try{
     $env:HIC_DEV_ACK_TOKEN=[Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
     $modeArg=if($Mode -eq 'AckOnce'){'--once'}else{'--watch'}
     & $nodeExe (Join-Path $toolRoot 'src/ack-cli.mjs') '--enable-read-only' '--enable-ack-publish' $modeArg
     if($Mode -eq 'AckOnce' -and $LASTEXITCODE -ne 0){throw 'Ack run did not complete.'}
   } finally {
     Remove-Item Env:\HIC_DEV_ACK_TOKEN -ErrorAction SilentlyContinue
     [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
   }
   break
 }
 'InstallAutostart' {
   if(!(Test-Path -LiteralPath $secretFile -PathType Leaf)){throw 'SetupAck must be done first.'}
   if(Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue){throw 'Task already exists; no overwrite.'}
   $confirm=Read-Host 'Type ENABLE-HIC-ACK for local user-only logon autostart'
   if($confirm -cne 'ENABLE-HIC-ACK'){throw 'Not authorized.'}
   $user=[System.Security.Principal.WindowsIdentity]::GetCurrent().Name
   $scriptPath=$MyInvocation.MyCommand.Path
   $arguments='-NoProfile -NonInteractive -File "'+$scriptPath+'" -Mode AckWatch'
   $action=New-ScheduledTaskAction -Execute (Join-Path $PSHOME 'powershell.exe') -Argument $arguments
   $trigger=New-ScheduledTaskTrigger -AtLogOn -User $user
   $principal=New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
   Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Description 'Developer-only HIC synthetic GitHub ack relay; never executes work orders' | Out-Null
   Write-Host 'Task registered for user logon; not started in this process.'
   break
 }
}
