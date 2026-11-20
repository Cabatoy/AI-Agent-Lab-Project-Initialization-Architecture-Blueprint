import { execFileSync } from 'node:child_process';
import { isAbsolute, join, win32 } from 'node:path';
import { verifyCopilotExecutable } from './copilot-cli-launch.mjs';
import { copilotConfigPath, loadCopilotConfig, PERSONAL_REPOSITORY } from './copilot-readonly.mjs';
import { loadWatchConfig } from './watch.mjs';
import { verifyWorkspaceTarget } from './workspace-target.mjs';

const probeScript=`
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false)
$sessionId=(Get-Process -Id $PID).SessionId
$filter="SessionId = $sessionId AND Name = 'node.exe' AND (CommandLine IS NULL OR CommandLine LIKE '%cli.mjs%')"
$lines=@(Get-CimInstance -ClassName Win32_Process -Filter $filter -Property CommandLine |
  ForEach-Object {$_.CommandLine})
ConvertTo-Json -InputObject $lines -Compress
`;
const loopFlags=new Map([
  ['watch-cli.mjs','--poll-loop'],['cli.mjs','--watch'],['ack-cli.mjs','--watch']
]);

export function detectRelayRuntime(commandLines) {
  if(!Array.isArray(commandLines)) return {status:'unknown',reason:'process-probe-unavailable'};
  let ambiguous=false;
  for(const line of commandLines) {
    if(typeof line!=='string'||!line.trim()) {
      ambiguous=true;
      continue;
    }
    // Match the Node script operand, not paths mentioned in unrelated process arguments.
    const [,quoted,bare,rest]=line.match(/^\s*(?:"[^"]+"|\S+)\s+(?:"([^"]+)"|([^\s"]+))(.*)$/)||[];
    const script=quoted??bare;
    if(!script) continue;
    const entry=win32.basename(script).toLowerCase();
    const flag=loopFlags.get(entry);
    const args=(rest.match(/"[^"]*"|[^\s"]+/g)??[]).map(arg=>arg.replace(/^"|"$/g,''));
    if(!flag||(entry==='watch-cli.mjs'?args[0]!==flag:!args.includes(flag)||args.includes('--once'))) continue;
    if(/^[a-z]:[\\/](?:.*[\\/])?tools[\\/]dev-handoff[\\/]zero-prompt-relay[\\/]src[\\/][^\\/]+$/i.test(script)) {
      return {status:'running',reason:'relay-loop-process-observed'};
    }
    if(!win32.isAbsolute(script)) ambiguous=true;
  }
  return ambiguous?{status:'unknown',reason:'process-identity-unverified'}:
    {status:'stopped',reason:'no-relay-loop-process-observed'};
}

function probeWindowsProcesses() {
  if(process.platform!=='win32'||!/^[a-z]:[\\/]/i.test(process.env.SystemRoot??'')) return null;
  const powershell=join(process.env.SystemRoot,'System32','WindowsPowerShell','v1.0','powershell.exe');
  try {
    return JSON.parse(execFileSync(powershell,['-NoProfile','-NonInteractive','-Command',probeScript],{
      encoding:'utf8',stdio:['ignore','pipe','ignore'],timeout:5000,maxBuffer:256*1024,windowsHide:true
    }));
  } catch {
    return null;
  }
}

function configStatus(load,path,missing,disabled) {
  try {
    return {status:'valid',config:load(path)};
  } catch(error) {
    if(error?.message===missing) return {status:'missing'};
    if(error?.message==='watch-config-upgrade-required') return {status:'upgrade-required'};
    if(disabled&&error?.message===disabled) return {status:'disabled'};
    return {status:'invalid'};
  }
}

function workspaceBlocker(config,workspaceRoot) {
  if(config.repository!==PERSONAL_REPOSITORY) return 'non-personal-repository';
  const separators=path=>process.platform==='win32'?path.replaceAll('/','\\'):path;
  // Don't follow config paths into other checkouts, even if they name the personal repository.
  if(separators(config.workspacePath)!==separators(workspaceRoot)) return 'workspace-not-this-checkout';
  try {
    verifyWorkspaceTarget(config);
    return null;
  } catch(error) {
    return error?.message==='workspace-not-clean'?'workspace-not-clean':'workspace-preflight-failed';
  }
}

export function relayStatus({stateDir,workspaceRoot,probeProcesses=probeWindowsProcesses}) {
  const runtime=detectRelayRuntime(probeProcesses());
  const watch=configStatus(loadWatchConfig,join(stateDir,'watch-forward.json'),'watch-config-not-found');
  const cli=configStatus(loadCopilotConfig,copilotConfigPath(stateDir),'copilot-read-only-not-configured','copilot-read-only-disabled');
  const watchBlocker=watch.config?workspaceBlocker(watch.config,workspaceRoot):`config-${watch.status}`;
  let cliBlocker=cli.config?workspaceBlocker(cli.config,workspaceRoot):`config-${cli.status}`;
  if(!cliBlocker) {
    try {
      const path=cli.config.copilotPath;
      if(!isAbsolute(path)||(process.platform==='win32'&&!/^[a-z]:[\\/]/i.test(path))) throw new Error();
      verifyCopilotExecutable(path);
    } catch {
      cliBlocker='local-executable-unverified';
    }
  }
  return {
    runtime:{...runtime,scope:'known-node-loop-entry-points-in-current-windows-session'},
    config:{watch:watch.status,copilotReadOnly:cli.status},
    githubPolling:{status:watchBlocker?'blocked':'configured-not-probed',blocker:watchBlocker,
      authMode:watch.config?.githubAuth??null,connectivityVerified:false,credentialsVerified:false},
    cliBackend:{status:cliBlocker?'blocked':'local-preflight-passed',blocker:cliBlocker,
      versionVerified:false,liveExecutionVerified:false},
    codingAgent:{available:false,status:'blocked',blocker:'scoped-write-and-test-enforcement-unverified'}
  };
}
