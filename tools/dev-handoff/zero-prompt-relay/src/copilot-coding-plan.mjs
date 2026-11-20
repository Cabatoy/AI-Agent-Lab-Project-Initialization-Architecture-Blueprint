import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { closeSync, fstatSync, lstatSync, openSync, readSync } from 'node:fs';
import { isAbsolute } from 'node:path';
import { PERSONAL_REPOSITORY } from './copilot-readonly.mjs';
import { verifyWorkspaceTarget } from './workspace-target.mjs';

export const CODING_PLAN_VERSION='hic.dev-copilot-coding-plan/1';
const fail=code=>{throw new Error(code);};
const exactKeys=(value,keys)=>value!==null&&typeof value==='object'&&!Array.isArray(value)&&
  Object.keys(value).length===keys.length&&keys.every(key=>Object.hasOwn(value,key));
const digest=value=>createHash('sha256').update(JSON.stringify(value),'utf8').digest('hex');
const SAFE_ERRORS=new Set([
  'coding-config-unavailable','unsafe-coding-config','invalid-coding-config','coding-disabled',
  'coding-repository-mismatch','invalid-coding-task','unsafe-coding-permissions',
  'coding-task-approval-required','coding-permission-approval-required','coding-head-mismatch',
  'workspace-unc-not-allowed','workspace-path-not-absolute','invalid-workspace-path',
  'invalid-workspace-branch','invalid-workspace-repository','workspace-path-unavailable',
  'workspace-path-not-canonical','workspace-path-link-not-allowed','workspace-path-not-directory',
  'workspace-git-preflight-failed','workspace-git-root-mismatch','workspace-repository-mismatch',
  'workspace-branch-mismatch','workspace-not-clean'
]);

export function loadCodingPlanConfig(path) {
  if(typeof path!=='string'||!isAbsolute(path)||/[\0\r\n]/.test(path)) fail('invalid-coding-config');
  let stat;
  try {stat=lstatSync(path);} catch {fail('coding-config-unavailable');}
  if(!stat.isFile()||stat.isSymbolicLink()||stat.size>16384) fail('unsafe-coding-config');
  let descriptor;
  try {
    descriptor=openSync(path,'r');
    const opened=fstatSync(descriptor);
    if(!opened.isFile()||opened.size>16384) fail('unsafe-coding-config');
    const buffer=Buffer.alloc(16385);
    let length=0,bytes;
    do {
      bytes=readSync(descriptor,buffer,length,buffer.length-length,null);
      length+=bytes;
    } while(bytes>0&&length<buffer.length);
    if(length>16384) fail('unsafe-coding-config');
    return JSON.parse(new TextDecoder('utf-8',{fatal:true}).decode(buffer.subarray(0,length)).replace(/^\uFEFF/,''));
  } catch(error) {
    if(error?.message==='unsafe-coding-config') throw error;
    fail('invalid-coding-config');
  } finally {
    if(descriptor!==undefined) closeSync(descriptor);
  }
}

function validatePermissions(permissions) {
  if(!exactKeys(permissions,['writePaths','testCommands'])||
    !Array.isArray(permissions.writePaths)||permissions.writePaths.length<1||permissions.writePaths.length>20||
    !Array.isArray(permissions.testCommands)||permissions.testCommands.length<1||permissions.testCommands.length>8) {
    fail('unsafe-coding-permissions');
  }
  const paths=permissions.writePaths;
  if(paths.some(path=>typeof path!=='string'||path.length>240||
    !/^[A-Za-z0-9_-][A-Za-z0-9_.-]*(?:\/[A-Za-z0-9_-][A-Za-z0-9_.-]*)*$/.test(path)||
    path.split('/').some(part=>part.endsWith('.')||part.toLowerCase()==='.git'))||
    new Set(paths.map(path=>path.toLowerCase())).size!==paths.length) fail('unsafe-coding-permissions');
  const commands=permissions.testCommands;
  if(commands.some(command=>!Array.isArray(command)||
    !((command.length===3&&command[0]==='npm'&&command[1]==='run'&&command[2]==='check')||
      (command.length===2&&command[0]==='dotnet'&&command[1]==='test')))||
    new Set(commands.map(command=>JSON.stringify(command))).size!==commands.length) fail('unsafe-coding-permissions');
}

function scopeFromConfig(config) {
  if(!exactKeys(config,['version','enabled','repository','workspacePath','expectedBranch','expectedHead','task','permissions','approvals'])||
    config.version!==CODING_PLAN_VERSION) fail('invalid-coding-config');
  if(config.enabled!==true) fail('coding-disabled');
  if(config.repository!==PERSONAL_REPOSITORY) fail('coding-repository-mismatch');
  if(typeof config.workspacePath!=='string'||typeof config.expectedBranch!=='string'||
    typeof config.expectedHead!=='string'||!/^[0-9a-f]{40}$/.test(config.expectedHead)) fail('invalid-coding-config');
  if(!exactKeys(config.task,['id','data'])||typeof config.task.id!=='string'||
    !/^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$/.test(config.task.id)||
    typeof config.task.data!=='string'||config.task.data.trim().length===0||
    Buffer.byteLength(config.task.data,'utf8')>8192||config.task.data.includes('\0')) fail('invalid-coding-task');
  validatePermissions(config.permissions);
  if(!exactKeys(config.approvals,['task','permissions'])||
    ![config.approvals.task,config.approvals.permissions].every(value=>value===null||
      (typeof value==='string'&&/^[0-9a-f]{64}$/.test(value)))) fail('invalid-coding-config');
  return {
    version:config.version,repository:config.repository,workspacePath:config.workspacePath,
    expectedBranch:config.expectedBranch,expectedHead:config.expectedHead,
    task:{id:config.task.id,data:config.task.data},
    permissions:{writePaths:config.permissions.writePaths,testCommands:config.permissions.testCommands}
  };
}

export function planCodingDryRun(config,{review=false}={}) {
  try {
    const scope=scopeFromConfig(config);
    const workspace=verifyWorkspaceTarget(scope);
    let head;
    try {
      head=execFileSync('git',['-C',workspace,'rev-parse','--verify','HEAD'],{
        encoding:'utf8',stdio:['ignore','pipe','ignore'],windowsHide:true
      }).trim();
    } catch {fail('workspace-git-preflight-failed');}
    if(head!==scope.expectedHead) fail('coding-head-mismatch');
    const scopeSha256=digest(scope);
    if(!review&&config.approvals.task!==scopeSha256) fail('coding-task-approval-required');
    if(!review&&config.approvals.permissions!==scopeSha256) fail('coding-permission-approval-required');
    return {
      status:'PASS',phase:review?'REVIEW':'DRY_RUN',agentExecutionAvailable:false,
      execution:{status:'BLOCKED',reason:'agent-execution-unavailable'},scopeSha256,
      intendedAction:{type:'single-shot-coding-agent',writePaths:scope.permissions.writePaths,
        testCommands:scope.permissions.testCommands}
    };
  } catch(error) {
    return {status:'BLOCKED',reason:SAFE_ERRORS.has(error?.message)?error.message:'coding-plan-failed',agentExecutionAvailable:false};
  }
}

export function blockedCodingConfig(error) {
  return {status:'BLOCKED',reason:SAFE_ERRORS.has(error?.message)?error.message:'coding-plan-failed',agentExecutionAvailable:false};
}
