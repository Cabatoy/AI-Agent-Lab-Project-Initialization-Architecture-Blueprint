import { randomUUID } from 'node:crypto';
import { closeSync, lstatSync, mkdirSync, openSync, readFileSync, realpathSync, writeFileSync } from 'node:fs';
import { isAbsolute, join, parse, resolve, sep } from 'node:path';
import { defaultStateDir } from './chat-forward.mjs';
import { runCopilotReadOnly, SUPPORTED_COPILOT_VERSION } from './copilot-cli-launch.mjs';
import { validateWorkspaceBinding } from './workspace-target.mjs';

export const COPILOT_CONFIG_VERSION='hic.dev-copilot-read-only/1';
export const COPILOT_RECORD_VERSION='hic.dev-copilot-read-only-record/1';
export const PERSONAL_REPOSITORY='Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint';
const fail=code=>{throw new Error(code);};
const exactKeys=(x,keys)=>!!x&&typeof x==='object'&&!Array.isArray(x)&&
  Object.keys(x).length===keys.length&&keys.every(key=>Object.hasOwn(x,key));
const CONFIG_KEYS=['version','enabled','repository','copilotPath','workspacePath','expectedBranch'];
const RECORDED_EVENT_TYPES=new Set([
  'session.start','session.model_change','session.extensions_loaded','session.tools_updated',
  'session.info','session.mcp_servers_loaded','user.message','assistant.turn_start',
  'assistant.message','assistant.turn_end','tool.execution_start','tool.execution_complete',
  'result','session.result','session.error'
]);
const SAFE_ERRORS=new Set([
  'invalid-copilot-config','copilot-read-only-disabled','invalid-copilot-repository',
  'invalid-copilot-path','copilot-executable-unavailable','unsafe-copilot-executable',
  'unsupported-copilot-executable','copilot-executable-not-runnable','invalid-workspace-path',
  'workspace-unc-not-allowed','workspace-path-not-absolute','invalid-workspace-branch',
  'invalid-workspace-repository','workspace-path-unavailable','workspace-path-not-canonical',
  'workspace-path-link-not-allowed','workspace-path-not-directory','workspace-git-preflight-failed',
  'workspace-git-root-mismatch','workspace-repository-mismatch','workspace-branch-mismatch',
  'workspace-not-clean','copilot-spawn-failed','copilot-timeout','copilot-output-too-large',
  'copilot-tool-config-warning','copilot-invalid-output','copilot-session-cwd-mismatch',
  'copilot-unexpected-tool','copilot-execution-blocked','unsafe-copilot-state-dir',
  'copilot-cli-session-error','copilot-cli-version-unsupported','invalid-copilot-timeout','copilot-read-only-not-configured',
  'unsafe-copilot-config','copilot-result-record-failed','copilot-output-stream-error',
  'copilot-session-incomplete','copilot-nonzero-exit'
]);
const SAFE_DIAGNOSTIC_CATEGORIES=new Map([
  ['copilot-session-cwd-mismatch',new Set([
    'session-start-missing','session-start-invalid','session-start-duplicate','session-cwd-missing',
    'session-cwd-invalid','filesystem-identity-mismatch','unsafe-or-unverifiable-path',
    'filesystem-identity-unavailable'
  ])],
  ['copilot-invalid-output',new Set(['stdout-invalid-utf8','stdout-invalid-jsonl','stdout-invalid-event'])]
]);

function responseEvidence(source) {
  const evidence={};
  for(const key of ['assistantResponseSeen','resultEventSeen']) {
    if(typeof source?.[key]==='boolean') evidence[key]=source[key];
  }
  return evidence;
}

export const copilotConfigPath=(stateDir=defaultStateDir())=>join(stateDir,'copilot-read-only.json');

export function checkCopilotConfig(x) {
  if(!exactKeys(x,CONFIG_KEYS)||x.version!==COPILOT_CONFIG_VERSION) fail('invalid-copilot-config');
  if(x.enabled!==true) fail('copilot-read-only-disabled');
  if(x.repository!==PERSONAL_REPOSITORY) fail('invalid-copilot-repository');
  if(typeof x.copilotPath!=='string'||!isAbsolute(x.copilotPath)||/[\0\r\n]/.test(x.copilotPath)) fail('invalid-copilot-config');
  if(typeof x.workspacePath!=='string'||typeof x.expectedBranch!=='string') fail('invalid-copilot-config');
  validateWorkspaceBinding({workspacePath:x.workspacePath,expectedBranch:x.expectedBranch});
  return x;
}

function assertSafeDirectory(path) {
  const absolute=resolve(path);
  const root=parse(absolute).root;
  const parts=absolute.slice(root.length).split(sep).filter(Boolean);
  if(parts.length===0) fail('unsafe-copilot-state-dir');
  let current=root;
  for(const part of parts) {
    current=join(current,part);
    let stat;
    try {stat=lstatSync(current)} catch {fail('unsafe-copilot-state-dir')}
    if(stat.isSymbolicLink()||!stat.isDirectory()) fail('unsafe-copilot-state-dir');
  }
  let canonical;
  try {canonical=realpathSync.native(absolute)} catch {fail('unsafe-copilot-state-dir')}
  const same=process.platform==='win32'?resolve(canonical).toLowerCase()===absolute.toLowerCase():resolve(canonical)===absolute;
  if(!same) fail('unsafe-copilot-state-dir');
  return canonical;
}

function ensureDirectory(path) {
  const absolute=resolve(path),root=parse(absolute).root;
  const parts=absolute.slice(root.length).split(sep).filter(Boolean);
  if(parts.length===0) fail('unsafe-copilot-state-dir');
  let current=root;
  for(const part of parts) {
    current=join(current,part);
    let stat;
    try {stat=lstatSync(current)} catch(error) {
      if(error?.code!=='ENOENT') fail('unsafe-copilot-state-dir');
      try {mkdirSync(current,{mode:0o700})} catch {fail('unsafe-copilot-state-dir')}
      try {stat=lstatSync(current)} catch {fail('unsafe-copilot-state-dir')}
    }
    if(stat.isSymbolicLink()||!stat.isDirectory()) fail('unsafe-copilot-state-dir');
  }
  return assertSafeDirectory(path);
}

export function loadCopilotConfig(path) {
  let stat;
  try {stat=lstatSync(path)} catch {fail('copilot-read-only-not-configured')}
  if(!stat.isFile()||stat.isSymbolicLink()||stat.size>4096) fail('unsafe-copilot-config');
  let value;
  try {value=JSON.parse(readFileSync(path,'utf8').replace(/^\uFEFF/,''))} catch {fail('invalid-copilot-config')}
  return checkCopilotConfig(value);
}

function writeRecord(directory,record) {
  const path=join(directory,record.runId+'.json');
  let descriptor;
  try {
    descriptor=openSync(path,'wx',0o600);
    writeFileSync(descriptor,JSON.stringify(record)+'\n');
  } catch {fail('copilot-result-record-failed')}
  finally {if(descriptor!==undefined) closeSync(descriptor)}
}

export async function runCopilotOnce({config,stateDir=defaultStateDir(),runner=runCopilotReadOnly,clock=Date.now}={}) {
  const c=checkCopilotConfig(config);
  const base=ensureDirectory(join(ensureDirectory(stateDir),'copilot-read-only'));
  const records=ensureDirectory(join(base,'records'));
  const runId=randomUUID();
  const start=clock();
  let status='blocked',reason='copilot-execution-blocked',processExitCode=null,summary={};
  let diagnosticCategory=null;
  try {
    const result=await runner(c);
    if(result.sessionCwdMatches!==true) fail('copilot-session-cwd-mismatch');
    status='completed';
    reason=null;
    processExitCode=result.processExitCode===0?0:null;
    const eventCounts={};
    for(const [type,count] of Object.entries(result.eventCounts??{})) {
      const safeType=RECORDED_EVENT_TYPES.has(type)?type:'other';
      if(Number.isSafeInteger(count)&&count>=0) eventCounts[safeType]=(eventCounts[safeType]??0)+count;
    }
    summary={
      copilotVersion:SUPPORTED_COPILOT_VERSION,
      sessionCwdMatches:result.sessionCwdMatches===true,
      ...responseEvidence(result),
      eventCounts,
      toolRequestCount:Number.isSafeInteger(result.toolRequestCount)&&result.toolRequestCount>=0?result.toolRequestCount:0,
      outputBytes:Number.isSafeInteger(result.outputBytes)&&result.outputBytes>=0?result.outputBytes:0
    };
  } catch(error) {
    const code=typeof error?.message==='string'&&SAFE_ERRORS.has(error.message)?error.message:'copilot-execution-blocked';
    reason=code;
    processExitCode=Number.isInteger(error?.exitCode)?error.exitCode:null;
    summary=responseEvidence(error);
    if(Number.isSafeInteger(error?.toolRequestCount)&&error.toolRequestCount>=0) {
      summary.toolRequestCount=error.toolRequestCount;
    }
    if(SAFE_DIAGNOSTIC_CATEGORIES.get(code)?.has(error?.diagnosticCategory)) {
      diagnosticCategory=error.diagnosticCategory;
    }
  }
  const end=clock();
  const record={
    version:COPILOT_RECORD_VERSION,runId,repository:c.repository,branch:c.expectedBranch,
    status,reason,processExitCode,startedAt:new Date(start).toISOString(),finishedAt:new Date(end).toISOString(),
    durationMs:Math.max(0,end-start),...(diagnosticCategory?{diagnosticCategory}:{}),...summary
  };
  writeRecord(records,record);
  return {status,reason,runId,repository:c.repository,branch:c.expectedBranch,record};
}
