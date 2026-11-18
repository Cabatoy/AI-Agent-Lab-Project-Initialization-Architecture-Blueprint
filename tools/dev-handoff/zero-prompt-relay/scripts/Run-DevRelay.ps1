#Requires -Version 5.1
<#
Developer-only HIC relay bootstrap. Never runs automatically.
Modes:
 Plan (default): no mutations
 SetupAck: one-time DPAPI secret, typed locally, never log or commit it
 ReadOnlyOnce, ReadOnlyWatch: public GET only
 AckOnce, AckWatch: opt-in public GET + POST synthetic receipt to Issue #97
 InstallAutostart: explicit per-user logon task, no administrator required
 ChatForwardSetup: write local opt-in config naming one issue and a pinned local Git worktree/branch
 ChatForwardOnce: verify the pinned worktree, then fail closed because the VS Code CLI cannot bind Ask Chat to that exact window
 ChatForwardWatchSetup: write local opt-in config for polling issues and a pinned local Git worktree/branch
 ChatForwardWatchCheck: polling-only verification; never forwards or opens Chat
 ChatForwardWatchOnce: poll once for issues with label; Chat delivery is blocked until exact window binding is supported
 ChatForwardWatchLoop: poll in a loop every 60 seconds; gracefully shut down with Ctrl+C
#>
[CmdletBinding()]
param(
  [ValidateSet('Plan','SetupAck','ReadOnlyOnce','ReadOnlyWatch','AckOnce','AckWatch','InstallAutostart','ChatForwardSetup','ChatForwardOnce','ChatForwardWatchSetup','ChatForwardWatchCheck','ChatForwardWatchOnce','ChatForwardWatchLoop')]
  [string]$Mode='Plan',
  [string]$Repository='',
  [int]$IssueNumber=0,
  [string]$TrustedAuthor='',
  [string]$WorkspacePath='',
  [string]$ExpectedBranch='',
  [string]$RelayLabel='',
  [ValidateSet('ask')]
  [string]$ChatMode='ask',
  [ValidateSet('none','gcm','env')]
  [string]$GitHubAuth='none',
  [string]$GitHubLogin='',
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
if($Mode -notin @('ChatForwardSetup','ChatForwardWatchSetup') -and $PSBoundParameters.ContainsKey('ChatMode')) {
  throw '-ChatMode is only accepted during local setup. Select the mode in the local config; runtime promotion is blocked.'
}
function Get-WorkspaceBinding {
  if([string]::IsNullOrWhiteSpace($WorkspacePath)){throw 'Pass -WorkspacePath <absolute personal Git worktree path>.'}
  if([string]::IsNullOrWhiteSpace($ExpectedBranch)){throw 'Pass -ExpectedBranch <branch>.'}
  if($WorkspacePath.StartsWith('\\') -or $WorkspacePath.StartsWith('//')){throw 'UNC and device paths are not allowed.'}
  if($WorkspacePath -notmatch '^[A-Za-z]:[\\/]'){throw '-WorkspacePath must be an absolute local drive path.'}
  $resolved=[System.IO.Path]::GetFullPath($WorkspacePath)
  $item=Get-Item -LiteralPath $resolved -Force -ErrorAction Stop
  if($item.PSProvider.Name -ne 'FileSystem'){throw '-WorkspacePath must be a local filesystem directory.'}
  if(-not $item.PSIsContainer){throw '-WorkspacePath must be a single-folder Git worktree, not a file or .code-workspace.'}
  return $resolved
}
switch ($Mode) {
 'Plan' {
   Write-Host 'ReadOnlyOnce/Watch: anonymous GitHub GET; no execution.'
   Write-Host 'SetupAck: asks for a fine-grained PAT with Issues read/write ONLY on the single personal repository, DPAPI per user.'
   Write-Host 'AckOnce/Watch: explicitly publishes only synthetic seen-untrusted receipts to Issue #97.'
   Write-Host 'InstallAutostart: requires explicit local confirmation and existing DPAPI secret; at-logon only.'
   Write-Host 'ChatForwardSetup/ChatForwardWatchSetup: require an explicit local worktree path and expected branch; setup makes no network call.'
   Write-Host 'ChatForwardOnce: fails closed because the VS Code CLI cannot bind Ask Chat to a verified workspace window.'
   Write-Host 'ChatForwardWatchOnce: polls once; pending issues are blocked from Chat delivery until exact window binding is supported, then may be retried.'
   Write-Host 'Agent execution is disabled. Existing version-1 chat configs must be recreated.'
   Write-Host 'ChatForwardWatchCheck: polling-only verification; lists issues that would be forwarded; never forwards, opens Chat or writes the dedup DB.'
   Write-Host 'ChatForwardWatchLoop: poll every 60 seconds with bounded rate-limit backoff; stops on auth/permission errors; Ctrl+C to stop.'
   break
 }
 'ChatForwardSetup' {
   if($Repository -notmatch '^[A-Za-z0-9-]{1,39}/[A-Za-z0-9._-]{1,100}$'){throw 'Pass -Repository owner/name.'}
   if($IssueNumber -lt 1){throw 'Pass -IssueNumber <n> explicitly.'}
   if($TrustedAuthor -notmatch '^[A-Za-z0-9-]{1,39}$'){throw 'Pass -TrustedAuthor <github-login> (issue author allowed to give tasks).'}
   $resolvedWorkspacePath=Get-WorkspaceBinding
   if((Test-Path -LiteralPath $chatConfigFile) -and -not $ReplaceConfig){throw 'Chat forward config already exists; review it or pass -ReplaceConfig.'}
   $codeCommand=(Get-Command code.cmd -ErrorAction Stop).Source
   New-Item -ItemType Directory -Force -Path $stateDir | Out-Null
   $config=[ordered]@{version='hic.dev-chat-forward/2';enabled=$true;repository=$Repository;issueNumber=$IssueNumber;trustedAuthor=$TrustedAuthor;codeCommand=$codeCommand;workspacePath=$resolvedWorkspacePath;expectedBranch=$ExpectedBranch;chatMode='ask'}
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
   if($GitHubAuth -ne 'none' -and $GitHubLogin -notmatch '^[A-Za-z0-9-]{1,39}$'){throw 'Authenticated polling requires -GitHubLogin <login> (the account the credential must belong to).'}
   if($GitHubAuth -eq 'none' -and $GitHubLogin){throw '-GitHubLogin applies only with -GitHubAuth gcm|env.'}
   $resolvedWorkspacePath=Get-WorkspaceBinding
   $watchConfigFile=Join-Path $stateDir 'watch-forward.json'
   if((Test-Path -LiteralPath $watchConfigFile) -and -not $ReplaceConfig){throw 'Watch forward config already exists; review it or pass -ReplaceConfig.'}
   New-Item -ItemType Directory -Force -Path $stateDir | Out-Null
   $config=[ordered]@{version='hic.dev-watch/2';repository=$Repository;trustedAuthor=$TrustedAuthor;relayLabel=$RelayLabel;workspacePath=$resolvedWorkspacePath;expectedBranch=$ExpectedBranch;chatMode='ask';githubAuth=$GitHubAuth}
   if($GitHubAuth -ne 'none'){ $config.githubLogin=$GitHubLogin }
   [System.IO.File]::WriteAllText($watchConfigFile,($config|ConvertTo-Json),(New-Object System.Text.UTF8Encoding($false)))
   Write-Host "Watch forward config written: repository=$Repository label=$RelayLabel branch=$ExpectedBranch workspace=bound chatMode=Ask githubAuth=$GitHubAuth githubLogin=$GitHubLogin"
   break
 }
 'ChatForwardWatchCheck' {
   $watchConfigFile=Join-Path $stateDir 'watch-forward.json'
   & $nodeExe (Join-Path $toolRoot 'src/watch-cli.mjs') '--poll-check' '--config' $watchConfigFile '--state-dir' $stateDir
   if($LASTEXITCODE -ne 0){throw 'Watch check failed closed; see the blocked reason above.'}
   break
 }
 'ChatForwardWatchOnce' {
   $watchConfigFile=Join-Path $stateDir 'watch-forward.json'
   $codeCmd=(Get-Command code.cmd -ErrorAction Stop).Source
   & $nodeExe (Join-Path $toolRoot 'src/watch-cli.mjs') '--poll-once' '--code-command' $codeCmd '--config' $watchConfigFile '--state-dir' $stateDir
   if($LASTEXITCODE -ne 0){throw 'Watch poll failed.'}
   break
 }
 'ChatForwardWatchLoop' {
   $watchConfigFile=Join-Path $stateDir 'watch-forward.json'
   Write-Host 'Starting watch loop. Press Ctrl+C to stop.'
   $codeCmd=(Get-Command code.cmd -ErrorAction Stop).Source
   & $nodeExe (Join-Path $toolRoot 'src/watch-cli.mjs') '--poll-loop' '--interval' '60000' '--code-command' $codeCmd '--config' $watchConfigFile '--state-dir' $stateDir
   if($LASTEXITCODE -ne 0){throw 'Watch loop stopped fail-closed; see the blocked reason above.'}
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
