import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { PERSONAL_REPOSITORY, COPILOT_CONFIG_VERSION } from '../src/copilot-readonly.mjs';
import { detectRelayRuntime, relayStatus } from '../src/status.mjs';
import { WATCH_VERSION } from '../src/watch.mjs';

test('Windows runtime detects known loops but not a one-shot or incidental script argument',()=>{
  const entry=String.raw`"C:\Personal HIC\tools\dev-handoff\zero-prompt-relay\src\watch-cli.mjs"`;
  const node=String.raw`"C:\Program Files\nodejs\node.exe"`;
  assert.equal(detectRelayRuntime([`${node} ${entry} "--poll-loop"`]).status,'running');
  assert.equal(detectRelayRuntime([
    `${node} ${entry} --poll-check --config "--poll-loop"`,`${node} unrelated.mjs ${entry} --poll-loop`
  ]).status,'stopped');
});

test('Windows runtime distinguishes an empty snapshot from inaccessible or ambiguous data',()=>{
  assert.equal(detectRelayRuntime([]).status,'stopped');
  for(const snapshot of [null,[null],['node src\\watch-cli.mjs --poll-loop']]) {
    assert.equal(detectRelayRuntime(snapshot).status,'unknown');
  }
});

function fixture(t) {
  const root=mkdtempSync(join(tmpdir(),'hic-status-'));
  t.after(()=>rmSync(root,{recursive:true,force:true}));
  const stateDir=join(root,'state');
  const workspaceRoot=join(root,'workspace');
  const status=(probeProcesses=()=>[])=>relayStatus({stateDir,workspaceRoot,probeProcesses});
  return {stateDir,workspaceRoot,status};
}

test('missing status config blocks both backends without creating local state',t=>{
  const {stateDir,status}=fixture(t);
  const result=status();
  assert.deepEqual(result.config,{watch:'missing',copilotReadOnly:'missing'});
  assert.equal(result.githubPolling.blocker,'config-missing');
  assert.equal(result.cliBackend.blocker,'config-missing');
  assert.equal(result.codingAgent.available,false);
  assert.equal(existsSync(stateDir),false);
});

test('status distinguishes malformed and disabled config without exposing file contents',t=>{
  const {stateDir,workspaceRoot,status}=fixture(t);
  mkdirSync(stateDir);
  writeFileSync(join(stateDir,'watch-forward.json'),'synthetic-private-value');
  writeFileSync(join(stateDir,'copilot-read-only.json'),JSON.stringify({
    version:COPILOT_CONFIG_VERSION,enabled:false,repository:PERSONAL_REPOSITORY,
    workspacePath:workspaceRoot,expectedBranch:'task/status',copilotPath:join(workspaceRoot,'copilot.exe')
  }));
  const result=status();
  assert.deepEqual(result.config,{watch:'invalid',copilotReadOnly:'disabled'});
  assert.equal(result.cliBackend.blocker,'config-disabled');
  assert.doesNotMatch(JSON.stringify(result),/synthetic-private-value|copilot\.exe/);
});

test('valid watch config targeting a different checkout is not dereferenced or treated as live-ready',t=>{
  const {stateDir,workspaceRoot,status}=fixture(t);
  mkdirSync(stateDir);
  writeFileSync(join(stateDir,'watch-forward.json'),JSON.stringify({
    version:WATCH_VERSION,repository:PERSONAL_REPOSITORY,trustedAuthor:'Cabatoy',
    relayLabel:'hic-relay-ready',workspacePath:join(workspaceRoot,'other'),expectedBranch:'task/status',
    chatMode:'ask',githubAuth:'gcm',githubLogin:'FixtureLogin'
  }));
  const result=status(()=>null);
  assert.equal(result.runtime.status,'unknown');
  assert.equal(result.config.watch,'valid');
  assert.equal(result.githubPolling.blocker,'workspace-not-this-checkout');
  assert.equal(result.githubPolling.credentialsVerified,false);
  assert.equal(result.githubPolling.connectivityVerified,false);
  assert.doesNotMatch(JSON.stringify(result),/FixtureLogin/);
});

test('local workspace and executable preflight does not certify live readiness',t=>{
  const {stateDir,workspaceRoot,status}=fixture(t);
  mkdirSync(stateDir);
  mkdirSync(workspaceRoot);
  const git=(...args)=>execFileSync('git',['-C',workspaceRoot,...args],{stdio:'ignore',windowsHide:true});
  git('init','--initial-branch=task/status');
  git('remote','add','origin',`https://github.com/${PERSONAL_REPOSITORY}.git`);
  const binding={repository:PERSONAL_REPOSITORY,workspacePath:workspaceRoot,expectedBranch:'task/status'};
  writeFileSync(join(stateDir,'watch-forward.json'),JSON.stringify({
    ...binding,version:WATCH_VERSION,trustedAuthor:'Cabatoy',relayLabel:'hic-relay-ready',githubAuth:'none'
  }));
  const copilotPath=join(stateDir,'copilot.exe');
  writeFileSync(join(stateDir,'copilot-read-only.json'),JSON.stringify({
    ...binding,version:COPILOT_CONFIG_VERSION,enabled:true,copilotPath
  }));
  assert.equal(status().cliBackend.blocker,'local-executable-unverified');
  writeFileSync(copilotPath,'fixture: must not be executed',{mode:0o700});
  const result=status();
  assert.equal(result.githubPolling.status,'configured-not-probed');
  assert.equal(result.cliBackend.status,'local-preflight-passed');
  assert.equal(result.cliBackend.versionVerified,false);
  assert.equal(result.cliBackend.liveExecutionVerified,false);
});
