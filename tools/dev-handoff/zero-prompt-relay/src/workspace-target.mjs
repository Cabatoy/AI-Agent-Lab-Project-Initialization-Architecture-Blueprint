import { execFileSync } from 'node:child_process';
import { lstatSync, realpathSync } from 'node:fs';
import { posix, win32 } from 'node:path';

const fail=code=>{throw new Error(code);};
const REPO=/^[A-Za-z0-9-]{1,39}\/[A-Za-z0-9._-]{1,100}$/;
const BRANCH=/^[A-Za-z0-9][A-Za-z0-9._/-]{0,254}$/;

function pathApi(platform) {
  return platform==='win32'?win32:posix;
}

function rejectUnc(path,platform) {
  if(platform==='win32'&&/^(?:\\\\|\/\/)/.test(path)) fail('workspace-unc-not-allowed');
}

export function validateWorkspaceBinding({workspacePath,expectedBranch}={},platform=process.platform) {
  if(typeof workspacePath!=='string'||workspacePath.length===0||workspacePath.includes('\0')) fail('invalid-workspace-path');
  rejectUnc(workspacePath,platform);
  if(!pathApi(platform).isAbsolute(workspacePath)) fail('workspace-path-not-absolute');
  if(typeof expectedBranch!=='string'||!BRANCH.test(expectedBranch)||expectedBranch.includes('..')||
    expectedBranch.includes('//')||expectedBranch.endsWith('/')||expectedBranch.endsWith('.')) fail('invalid-workspace-branch');
  return {workspacePath,expectedBranch};
}

function comparablePath(path,api,platform) {
  let value=api.resolve(path);
  if(platform==='win32') {
    value=value.replace(/^\\\\\?\\/, '');
    value=value.toLowerCase();
  }
  const root=api.parse(value).root;
  return value.length>root.length?value.replace(/[\\/]+$/,''):value;
}

function canonicalWorkspacePath(workspacePath,platform) {
  const api=pathApi(platform);
  const {workspacePath:configuredPath}=validateWorkspaceBinding({workspacePath,expectedBranch:'main'},platform);
  const resolved=api.resolve(configuredPath);
  let canonical;
  try {canonical=realpathSync.native(resolved);}
  catch {fail('workspace-path-unavailable')}
  if(comparablePath(canonical,api,platform)!==comparablePath(resolved,api,platform)) fail('workspace-path-not-canonical');

  const parsed=api.parse(resolved);
  const segments=resolved.slice(parsed.root.length).split(api.sep).filter(Boolean);
  let current=parsed.root;
  for(const segment of segments) {
    current=api.join(current,segment);
    let stat;
    try {stat=lstatSync(current);}
    catch {fail('workspace-path-unavailable')}
    if(stat.isSymbolicLink()) fail('workspace-path-link-not-allowed');
    if(!stat.isDirectory()) fail('workspace-path-not-directory');
  }
  if(segments.length===0) {
    let stat;
    try {stat=lstatSync(resolved);}
    catch {fail('workspace-path-unavailable')}
    if(stat.isSymbolicLink()) fail('workspace-path-link-not-allowed');
    if(!stat.isDirectory()) fail('workspace-path-not-directory');
  }
  return resolved;
}

function githubRepositoryFromRemote(remote) {
  if(typeof remote!=='string'||remote.includes('\n')||remote.includes('\r')) return null;
  const scp=/^git@github\.com:([^?#]+)$/i.exec(remote);
  if(scp) return scp[1].replace(/\.git$/i,'');
  let parsed;
  try {parsed=new URL(remote);}
  catch {return null;}
  if(parsed.hostname.toLowerCase()!=='github.com'||parsed.search||parsed.hash) return null;
  if(parsed.protocol==='https:') {
    if(parsed.username||parsed.password) return null;
  } else if(parsed.protocol==='ssh:') {
    if(parsed.username!=='git'||parsed.password) return null;
  } else return null;
  return parsed.pathname.replace(/^\/+/,'').replace(/\.git$/i,'');
}

function gitOutput(workspacePath,args) {
  try {
    return execFileSync('git',['-C',workspacePath,...args],{
      encoding:'utf8',
      stdio:['ignore','pipe','ignore'],
      windowsHide:true
    }).trimEnd();
  } catch {
    fail('workspace-git-preflight-failed');
  }
}

export function verifyWorkspaceTarget({workspacePath,expectedBranch,repository}={}) {
  validateWorkspaceBinding({workspacePath,expectedBranch});
  if(typeof repository!=='string'||!REPO.test(repository)||repository.includes('..')) fail('invalid-workspace-repository');
  const canonicalPath=canonicalWorkspacePath(workspacePath,process.platform);
  const root=gitOutput(canonicalPath,['rev-parse','--show-toplevel']);
  if(comparablePath(root,pathApi(process.platform),process.platform)!==comparablePath(canonicalPath,pathApi(process.platform),process.platform)) {
    fail('workspace-git-root-mismatch');
  }
  const remote=gitOutput(canonicalPath,['remote','get-url','origin']);
  if(githubRepositoryFromRemote(remote)?.toLowerCase()!==repository.toLowerCase()) fail('workspace-repository-mismatch');
  try {
    execFileSync('git',['-C',canonicalPath,'check-ref-format','--branch',expectedBranch],{
      stdio:'ignore',
      windowsHide:true
    });
  } catch {
    fail('invalid-workspace-branch');
  }
  if(gitOutput(canonicalPath,['branch','--show-current'])!==expectedBranch) fail('workspace-branch-mismatch');
  if(gitOutput(canonicalPath,['status','--porcelain','--untracked-files=all'])!=='') fail('workspace-not-clean');
  return canonicalPath;
}

// `code chat -n` opens an empty window; `-r` targets whichever window was last active.
// Neither CLI mode can bind Ask chat to a verified workspace window.
export function assertChatWorkspaceSelectionAvailable() {
  fail('chat-workspace-binding-unavailable');
}
