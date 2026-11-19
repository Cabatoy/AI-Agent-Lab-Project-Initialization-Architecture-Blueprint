import { spawn } from 'node:child_process';
import { accessSync, constants, lstatSync, realpathSync } from 'node:fs';
import { isAbsolute, resolve } from 'node:path';
import { sameWorkspaceDirectoryIdentity, verifyWorkspaceTarget } from './workspace-target.mjs';

// Proof of concept for issue #172: launch Copilot CLI (documented `-p` programmatic mode)
// with an explicit verified cwd. Read-only analysis only; GitHub Issue text is never part of the prompt.
export const COPILOT_PROMPT='Read-only analysis. Summarize the repository structure in at most 100 words. Do not modify files or run commands.';
const MAX_OUTPUT=262144, DEFAULT_TIMEOUT_MS=120000;
const ENV_ALLOW=['PATH','SystemRoot','USERPROFILE','HOME','APPDATA','LOCALAPPDATA','TEMP','TMP'];
const fail=code=>{throw new Error(code);};
const samePath=(a,b)=>process.platform==='win32'?a.toLowerCase()===b.toLowerCase():a===b;

// Verified against Copilot CLI 1.0.114: --available-tools takes concrete tool names (not permission kinds like `read`)
// and, like --deny-tool, must be repeated per value (comma-joined values are rejected).
export const READ_ONLY_TOOLS=Object.freeze(['view','glob','grep']);
export const SUPPORTED_COPILOT_VERSION='1.0.114';
const DENIED_PERMISSIONS=Object.freeze(['shell','write']);

export function buildCopilotArgs(cwd) {
  if(typeof cwd!=='string'||!isAbsolute(cwd)) fail('invalid-copilot-cwd');
  return ['-C',cwd,'-p',COPILOT_PROMPT,'--output-format=json','--no-ask-user','--no-auto-update',
    '--no-custom-instructions','--disable-builtin-mcps','--disallow-temp-dir',
    ...READ_ONLY_TOOLS.map(t=>'--available-tools='+t),
    '--allow-tool=read',
    ...DENIED_PERMISSIONS.map(t=>'--deny-tool='+t),
    '--deny-url=*'];
}

export function scrubbedEnv(env=process.env) {
  const out={};
  for(const k of ENV_ALLOW) if(typeof env[k]==='string') out[k]=env[k];
  return out;
}

export function verifyCopilotExecutable(copilotPath) {
  if(typeof copilotPath!=='string'||!isAbsolute(copilotPath)||/[\0\r\n]/.test(copilotPath)) fail('invalid-copilot-path');
  let stat,canonical;
  try {
    stat=lstatSync(copilotPath);
    canonical=realpathSync.native(copilotPath);
  } catch {fail('copilot-executable-unavailable')}
  if(!stat.isFile()||stat.isSymbolicLink()||!samePath(resolve(canonical),resolve(copilotPath))) fail('unsafe-copilot-executable');
  if(process.platform==='win32') {
    if(!/\.exe$/i.test(copilotPath)) fail('unsupported-copilot-executable');
  } else {
    try {accessSync(copilotPath,constants.X_OK)} catch {fail('copilot-executable-not-runnable')}
  }
  return canonical;
}

export function planCopilotLaunch({copilotPath,workspacePath,expectedBranch,repository}={}) {
  const command=verifyCopilotExecutable(copilotPath);
  const cwd=verifyWorkspaceTarget({workspacePath,expectedBranch,repository});
  return {command,args:buildCopilotArgs(cwd),cwd,env:scrubbedEnv()};
}

export function runCopilotReadOnly(options={}, {spawnFn=spawn,timeoutMs=DEFAULT_TIMEOUT_MS}={}) {
  const plan=planCopilotLaunch(options);
  if(!Number.isFinite(timeoutMs)||timeoutMs<=0) fail('invalid-copilot-timeout');
  verifyCopilotExecutable(plan.command);
  const verifiedCwd=verifyWorkspaceTarget({
    workspacePath:plan.cwd,expectedBranch:options.expectedBranch,repository:options.repository
  });
  if(!sameWorkspaceDirectoryIdentity(verifiedCwd,plan.cwd)) fail('workspace-git-root-mismatch');
  return new Promise((resolve,reject)=>{
    let child;
    try {child=spawnFn(plan.command,plan.args,{cwd:plan.cwd,env:plan.env,shell:false,windowsHide:true,stdio:['ignore','pipe','pipe']});}
    catch {reject(new Error('copilot-spawn-failed'));return;}
    const stdout=[];
    let stderrTail='',warning=false,size=0,stderrSize=0,settled=false;
    const done=(fn,v)=>{if(settled)return;settled=true;clearTimeout(timer);fn(v);};
    const stop=reason=>{if(settled)return;done(reject,new Error(reason));child.kill();};
    const timer=setTimeout(()=>stop('copilot-timeout'),timeoutMs);
    child.stdout.on('data',d=>{
      if(settled)return;
      size+=d.length;
      if(size>MAX_OUTPUT)return stop('copilot-output-too-large');
      stdout.push(d);
    });
    child.stderr.on('data',d=>{
      if(settled)return;
      stderrSize+=d.length;
      if(stderrSize>MAX_OUTPUT)return stop('copilot-output-too-large');
      const text=stderrTail+String(d);
      if(/unknown tool|invalid --/i.test(text)) warning=true;
      stderrTail=text.slice(-512);
    });
    child.stdout.on('error',()=>stop('copilot-output-stream-error'));
    child.stderr.on('error',()=>stop('copilot-output-stream-error'));
    child.on('error',()=>done(reject,new Error('copilot-spawn-failed')));
    child.on('close',code=>{
      if(settled)return;
      if(code!==0) {
        const error=new Error('copilot-nonzero-exit');
        error.exitCode=Number.isInteger(code)?code:null;
        return done(reject,error);
      }
      const rejectOutput=(reason,diagnosticCategory,evidence)=>{
        const error=new Error(reason);
        error.exitCode=code;
        if(diagnosticCategory) error.diagnosticCategory=diagnosticCategory;
        if(evidence) Object.assign(error,evidence);
        done(reject,error);
      };
      if(warning) return rejectOutput('copilot-tool-config-warning');
      const events=[];
      let out;
      try {out=new TextDecoder('utf-8',{fatal:true,ignoreBOM:true}).decode(Buffer.concat(stdout,size));}
      catch {return rejectOutput('copilot-invalid-output','stdout-invalid-utf8');}
      for(const line of out.split(/\r?\n/).filter(Boolean)) {
        let event;
        try {event=JSON.parse(line);}
        catch {return rejectOutput('copilot-invalid-output','stdout-invalid-jsonl');}
        if(!event||typeof event.type!=='string'||!event.type) {
          return rejectOutput('copilot-invalid-output','stdout-invalid-event');
        }
        events.push(event);
      }
      const {reason,diagnosticCategory,...evidence}=checkEvents(events,plan.cwd);
      if(reason) return rejectOutput(reason,diagnosticCategory,evidence);
      done(resolve,{
        copilotVersion:SUPPORTED_COPILOT_VERSION,
        processExitCode:0,
        sessionCwdMatches:true,
        eventCounts:events.reduce((counts,event)=>{
          const type=typeof event?.type==='string'?event.type:'unknown';
          counts[type]=(counts[type]??0)+1;
          return counts;
        },Object.create(null)),
        ...evidence,
        outputBytes:size
      });
    });
  });
}

function sessionCwdProblem(starts,cwd) {
  if(starts.length===0) return 'session-start-missing';
  if(starts.length!==1) return 'session-start-duplicate';
  const {data}=starts[0];
  if(!data||typeof data!=='object'||Array.isArray(data)) return 'session-start-invalid';
  const {context}=data;
  if(context===undefined) return 'session-cwd-missing';
  if(!context||typeof context!=='object'||Array.isArray(context)) return 'session-cwd-invalid';
  if(!Object.hasOwn(context,'cwd')) return 'session-cwd-missing';
  const {cwd:reported}=context;
  if(typeof reported!=='string'||!reported.trim()||/[\0\r\n]/.test(reported)) return 'session-cwd-invalid';
  try {
    if(!sameWorkspaceDirectoryIdentity(reported,cwd)) return 'filesystem-identity-mismatch';
  } catch(error) {
    if(error?.message==='workspace-identity-unavailable') return 'filesystem-identity-unavailable';
    if(error?.message==='workspace-path-unavailable') return 'unsafe-or-unverifiable-path';
    return 'session-cwd-invalid';
  }
  return null;
}

function checkEvents(events,cwd) {
  const starts=events.filter(e=>e.type==='session.start');
  const assistantResponseSeen=events.some(e=>e.type==='assistant.message'&&
    typeof e.data?.content==='string'&&e.data.content.trim().length>0);
  const resultEventSeen=events.some(e=>e.type==='result');
  const evidence={assistantResponseSeen,resultEventSeen};
  let toolRequestCount=0;
  for(const e of events) {
    if(/warning/i.test(e.type)&&/unknown tool|invalid --/i.test(JSON.stringify(e.data??''))) {
      return {reason:'copilot-tool-config-warning',...evidence};
    }
    if(e.type==='session.error') return {reason:'copilot-cli-session-error',...evidence};
    if(e.type==='tool.execution_start'&&!READ_ONLY_TOOLS.includes(e.data?.toolName)) {
      return {reason:'copilot-unexpected-tool',...evidence};
    }
    if(e.type==='assistant.message'&&e.data?.toolRequests!==undefined) {
      const requests=e.data.toolRequests;
      if(!Array.isArray(requests)||requests.some(r=>!READ_ONLY_TOOLS.includes(r?.name))) {
        return {reason:'copilot-unexpected-tool',...evidence};
      }
      toolRequestCount+=requests.length;
    }
  }
  evidence.toolRequestCount=toolRequestCount;
  const diagnosticCategory=sessionCwdProblem(starts,cwd);
  if(diagnosticCategory) return {reason:'copilot-session-cwd-mismatch',diagnosticCategory,...evidence};
  if(starts[0].data.copilotVersion!==SUPPORTED_COPILOT_VERSION) {
    return {reason:'copilot-cli-version-unsupported',...evidence};
  }
  if(!assistantResponseSeen||!resultEventSeen) return {reason:'copilot-session-incomplete',...evidence};
  return evidence;
}
