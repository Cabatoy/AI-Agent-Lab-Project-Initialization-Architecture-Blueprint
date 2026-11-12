#Requires -Version 5.1
<#
Developer-only HIC relay bootstrap. Never runs automatically.
Modes:
 Plan (default): no mutations
 SetupAck: one-time DPAPI secret, typed locally, never log or commit it
 ReadOnlyOnce, ReadOnlyWatch: public GET only
 AckOnce, AckWatch: opt-in public GET + POST synthetic receipt to Issue #97
 InstallAutostart: explicit per-user logon task, no administrator required
 ChatForwardSetup: write local opt-in config naming exactly one GitHub issue for VS Code Chat
 ChatForwardOnce: forward that issue once to `code chat`; a local record prevents re-sending
 ChatForwardWatchSetup: write local opt-in config for polling issues with a label
 ChatForwardWatchOnce: poll once for issues with label; no re-send on restart
#>
[CmdletBinding()]
param(
  [ValidateSet('Plan','SetupAck','ReadOnlyOnce','ReadOnlyWatch','AckOnce','AckWatch','InstallAutostart','ChatForwardSetup','ChatForwardOnce','ChatForwardWatchSetup','ChatForwardWatchOnce')]
  [string]$Mode='Plan',
  [string]$Repository='',
  [int]$IssueNumber=0,
  [string]$TrustedAuthor='',
  [string]$RelayLabel='',
  [ValidateSet('agent','ask','edit')]
  [string]$ChatMode='agent',
  [switch]$ReplaceConfig
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if($env:OS -ne 'Windows_NT') { throw 'Windows only: per-user DPAPI and Task Scheduler required.' }
$toolRoot=Split-Path -Parent $PSScriptRoot
$nodeExe=(Get-Command node.exe -ErrorAction Stop).Source
$stateDir=Join-Path $env:LOCALAPPDATA 'HICDevRelay'
$secretFile=Join-Path $stateDir 'ack-token.dpapi'
$taskName='HIC-Developer-Relay-Ack-OptIn'
$chatConfigFile=Join-Path $stateDir 'chat-forward.json'
switch ($Mode) {
 'Plan' {
   Write-Host 'ReadOnlyOnce/Watch: anonymous GitHub GET; no execution.'
   Write-Host 'SetupAck: asks for a fine-grained PAT with Issues read/write ONLY on the single personal repository, DPAPI per user.'
   Write-Host 'AckOnce/Watch: explicitly publishes only synthetic seen-untrusted receipts to Issue #97.'
   Write-Host 'InstallAutostart: requires explicit local confirmation and existing DPAPI secret; at-logon only.'
   Write-Host 'ChatForwardSetup: writes a local config naming ONE repo/issue/trusted author; no network call.'
   Write-Host 'ChatForwardOnce: reads that issue anonymously and hands it to VS Code Chat via code chat once; exit code is not completion.'
   Write-Host 'ChatForwardWatchSetup: writes a local config for polling issues with a label on a repo.'
   Write-Host 'ChatForwardWatchOnce: poll once for open issues with label from trusted author; no re-send on restart.'
   break
 }
 'ChatForwardSetup' {
   if($Repository -notmatch '^[A-Za-z0-9-]{1,39}/[A-Za-z0-9._-]{1,100}$'){throw 'Pass -Repository owner/name.'}
   if($IssueNumber -lt 1){throw 'Pass -IssueNumber <n> explicitly.'}
   if($TrustedAuthor -notmatch '^[A-Za-z0-9-]{1,39}$'){throw 'Pass -TrustedAuthor <github-login> (issue author allowed to give tasks).'}
   if((Test-Path -LiteralPath $chatConfigFile) -and -not $ReplaceConfig){throw 'Chat forward config already exists; review it or pass -ReplaceConfig.'}
   $codeCommand=(Get-Command code.cmd -ErrorAction Stop).Source
   New-Item -ItemType Directory -Force -Path $stateDir | Out-Null
   $config=[ordered]@{version='hic.dev-chat-forward/1';enabled=$true;repository=$Repository;issueNumber=$IssueNumber;trustedAuthor=$TrustedAuthor;codeCommand=$codeCommand;chatMode=$ChatMode}
   [System.IO.File]::WriteAllText($chatConfigFile,($config|ConvertTo-Json),(New-Object System.Text.UTF8Encoding($false)))
   Write-Host "Chat forward config written for $Repository#$IssueNumber. Nothing was sent."
   break
 }
 'ChatForwardOnce' {
   & $nodeExe (Join-Path $toolRoot 'src/chat-cli.mjs') '--enable-chat-forward' '--once' '--config' $chatConfigFile '--state-dir' $stateDir
   if($LASTEXITCODE -ne 0){throw 'Chat forward did not hand off the issue.'}
   break
 }
 'ChatForwardWatchSetup' {
   if($Repository -notmatch '^[A-Za-z0-9-]{1,39}/[A-Za-z0-9._-]{1,100}$'){throw 'Pass -Repository owner/name.'}
   if($TrustedAuthor -notmatch '^[A-Za-z0-9-]{1,39}$'){throw 'Pass -TrustedAuthor <github-login>.'}
   if($RelayLabel -notmatch '^[a-z-]{1,50}$'){throw 'Pass -RelayLabel <label> (lowercase, hyphens allowed).'}
   $watchConfigFile=Join-Path $stateDir 'watch-forward.json'
   if((Test-Path -LiteralPath $watchConfigFile) -and -not $ReplaceConfig){throw 'Watch forward config already exists; review it or pass -ReplaceConfig.'}
   New-Item -ItemType Directory -Force -Path $stateDir | Out-Null
   $config=[ordered]@{version='hic.dev-watch/1';repository=$Repository;trustedAuthor=$TrustedAuthor;relayLabel=$RelayLabel}
   [System.IO.File]::WriteAllText($watchConfigFile,($config|ConvertTo-Json),(New-Object System.Text.UTF8Encoding($false)))
   Write-Host "Watch forward config written for $Repository label=$RelayLabel author=$TrustedAuthor. Nothing was sent."
   break
 }
 'ChatForwardWatchOnce' {
   $watchConfigFile=Join-Path $stateDir 'watch-forward.json'
   & $nodeExe (Join-Path $toolRoot 'src/watch-cli.mjs') '--enable-watch' '--poll-once' '--config' $watchConfigFile '--state-dir' $stateDir
   if($LASTEXITCODE -ne 0){throw 'Watch poll failed.'}
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
