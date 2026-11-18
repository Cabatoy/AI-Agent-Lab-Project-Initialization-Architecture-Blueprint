import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { validateWorkspaceBinding, verifyWorkspaceTarget } from '../src/workspace-target.mjs';

const repository='Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint';
const branch='task/171-test';

function createWorkspace(t) {
  const parent=mkdtempSync(join(tmpdir(),'hic-workspace-target-'));
  t.after(()=>rmSync(parent,{recursive:true,force:true}));
  const workspacePath=join(parent,'repo');
  mkdirSync(workspacePath);
  const git=args=>execFileSync('git',['-C',workspacePath,...args],{stdio:'ignore',windowsHide:true});
  git(['init','--quiet']);
  git(['checkout','--quiet','-b',branch]);
  git(['config','user.name','HIC Offline Test']);
  git(['config','user.email','hic-offline@example.invalid']);
  writeFileSync(join(workspacePath,'README.md'),'offline fixture\n');
  git(['add','README.md']);
  git(['-c','user.name=HIC Offline Test','-c','user.email=hic-offline@example.invalid','commit','--quiet','-m','offline fixture']);
  git(['remote','add','origin','https://github.com/'+repository+'.git']);
  return {parent,workspacePath};
}

test('Windows workspace binding rejects UNC and device paths without accessing them',()=>{
  for(const workspacePath of [
    '\\\\server\\share\\repo',
    '\\\\?\\UNC\\server\\share\\repo',
    '\\\\.\\C:\\repo',
    '//server/share/repo'
  ]) {
    assert.throws(()=>validateWorkspaceBinding({workspacePath,expectedBranch:branch},'win32'),/workspace-unc-not-allowed/);
  }
});

test('workspace preflight accepts only the configured clean GitHub worktree',(t)=>{
  const {workspacePath}=createWorkspace(t);
  assert.equal(verifyWorkspaceTarget({workspacePath,expectedBranch:branch,repository}),workspacePath);
});

test('workspace preflight rejects a different origin, branch, or dirty tree',(t)=>{
  const {workspacePath}=createWorkspace(t);
  const git=args=>execFileSync('git',['-C',workspacePath,...args],{stdio:'ignore',windowsHide:true});
  assert.throws(()=>verifyWorkspaceTarget({workspacePath,expectedBranch:branch,repository:'Cabatoy/other-repo'}),/workspace-repository-mismatch/);
  assert.throws(()=>verifyWorkspaceTarget({workspacePath,expectedBranch:'task/other',repository}),/workspace-branch-mismatch/);
  writeFileSync(join(workspacePath,'untracked.txt'),'untracked\n');
  assert.throws(()=>verifyWorkspaceTarget({workspacePath,expectedBranch:branch,repository}),/workspace-not-clean/);
  rmSync(join(workspacePath,'untracked.txt'));
  git(['remote','set-url','origin','https://github.com/Cabatoy/other-repo.git']);
  assert.throws(()=>verifyWorkspaceTarget({workspacePath,expectedBranch:branch,repository}),/workspace-repository-mismatch/);
});

test('workspace preflight rejects junction aliases and workspace files',(t)=>{
  const {parent,workspacePath}=createWorkspace(t);
  const junction=join(parent,'junction');
  symlinkSync(workspacePath,junction,'junction');
  assert.throws(()=>verifyWorkspaceTarget({workspacePath:junction,expectedBranch:branch,repository}),/workspace-path-(?:not-canonical|link-not-allowed)/);

  const multiRoot=join(parent,'multi-root.code-workspace');
  writeFileSync(multiRoot,JSON.stringify({folders:[{path:workspacePath},{path:parent}]}));
  assert.throws(()=>verifyWorkspaceTarget({workspacePath:multiRoot,expectedBranch:branch,repository}),/workspace-path-not-directory/);
});

test('workspace binding requires an absolute local path and a valid branch',()=>{
  assert.throws(()=>validateWorkspaceBinding({workspacePath:'relative\\repo',expectedBranch:branch},'win32'),/workspace-path-not-absolute/);
  assert.throws(()=>validateWorkspaceBinding({workspacePath:'C:\\repo',expectedBranch:'bad..branch'},'win32'),/invalid-workspace-branch/);
});
