import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { planCopilotLaunch } from '../src/copilot-cli-launch.mjs';
import {
  checkCopilotConfig, copilotConfigPath, COPILOT_CONFIG_VERSION, loadCopilotConfig,
  PERSONAL_REPOSITORY, runCopilotOnce
} from '../src/copilot-readonly.mjs';

const branch='task/172-test';

function fixture(t) {
  const root=mkdtempSync(join(tmpdir(),'hic-copilot-readonly-'));
  t.after(()=>rmSync(root,{recursive:true,force:true}));
  const workspacePath=join(root,'repo');
  mkdirSync(workspacePath);
  const copilotPath=join(root,'copilot.exe');
  writeFileSync(copilotPath,'offline executable fixture');
  const git=args=>execFileSync('git',['-C',workspacePath,...args],{stdio:'ignore',windowsHide:true});
  git(['init','--quiet']);
  git(['checkout','--quiet','-b',branch]);
  writeFileSync(join(workspacePath,'README.md'),'offline fixture\n');
  git(['add','README.md']);
  git(['-c','user.name=HIC Offline Test','-c','user.email=hic-offline@example.invalid','commit','--quiet','-m','fixture']);
  git(['remote','add','origin','https://github.com/'+PERSONAL_REPOSITORY+'.git']);
  return {
    root,workspacePath,copilotPath,
    config:{version:COPILOT_CONFIG_VERSION,enabled:true,repository:PERSONAL_REPOSITORY,copilotPath,workspacePath,expectedBranch:branch}
  };
}

test('configuration is exact, explicit, personal-repository-only and disabled by default',t=>{
  const f=fixture(t);
  assert.throws(()=>checkCopilotConfig({...f.config,enabled:false}),/copilot-read-only-disabled/);
  assert.throws(()=>checkCopilotConfig({...f.config,repository:'other/private'}),/invalid-copilot-repository/);
  assert.throws(()=>checkCopilotConfig({...f.config,issueBody:'untrusted'}),/invalid-copilot-config/);
  assert.throws(()=>checkCopilotConfig({...f.config,copilotPath:'copilot'}),/invalid-copilot-config/);
  const path=copilotConfigPath(join(f.root,'state'));
  assert.match(path,/copilot-read-only\.json$/);
});

test('operator invocation with no config fails closed and never starts a CLI',t=>{
  const f=fixture(t);
  const configPath=join(f.root,'missing.json');
  const cli=fileURLToPath(new URL('../src/copilot-readonly-cli.mjs',import.meta.url));
  const result=spawnSync(process.execPath,[cli,'--enable-copilot-read-only','--once','--config',configPath,'--state-dir',join(f.root,'state')],{encoding:'utf8',windowsHide:true});
  assert.equal(result.status,1);
  assert.deepEqual(JSON.parse(result.stderr),{status:'blocked',reason:'copilot-read-only-not-configured'});
});

test('execution result record is structured and excludes raw output, paths and secrets',async t=>{
  const f=fixture(t);
  const stateDir=join(f.root,'state');
  const result=await runCopilotOnce({
    config:f.config,stateDir,clock:(()=>{let n=0;return()=>1700000000000+n++*1000})(),
    runner:async()=>({
      sessionCwdMatches:true,eventCounts:{'session.start':1,'assistant.message':1,'SECRET_TOKEN=do-not-log':99},
      toolRequestCount:0,outputBytes:1234,
      rawOutput:'PRIVATE_ACCESS_TOKEN=must-not-be-recorded'
    })
  });
  assert.equal(result.status,'completed');
  assert.equal(result.record.sessionCwdMatches,true);
  assert.deepEqual(result.record.eventCounts,{'session.start':1,other:99,'assistant.message':1});
  const stored=readFileSync(join(stateDir,'copilot-read-only','records',result.runId+'.json'),'utf8');
  assert.ok(!stored.includes('SECRET_TOKEN'));
  assert.ok(!stored.includes('PRIVATE_ACCESS_TOKEN'));
  assert.ok(!stored.includes(f.workspacePath));
  assert.equal(JSON.parse(stored).repository,PERSONAL_REPOSITORY);
});

test('timeouts are recorded as a safe blocked result without raw errors',async t=>{
  const f=fixture(t);
  const result=await runCopilotOnce({
    config:f.config,stateDir:join(f.root,'state'),
    runner:async()=>{throw new Error('copilot-timeout');}
  });
  assert.equal(result.status,'blocked');
  assert.equal(result.reason,'copilot-timeout');
  assert.equal(JSON.stringify(result.record).includes(f.workspacePath),false);
});

test('cwd mismatch records only an allow-listed diagnostic category',async t=>{
  const f=fixture(t);
  const result=await runCopilotOnce({
    config:f.config,stateDir:join(f.root,'state'),
    runner:async()=>{
      const error=new Error('copilot-session-cwd-mismatch');
      error.diagnosticCategory='filesystem-identity-mismatch';
      error.toolRequestCount=1;
      error.exitCode=0;
      error.assistantResponseSeen=false;
      error.resultEventSeen=true;
      throw error;
    }
  });
  assert.equal(result.reason,'copilot-session-cwd-mismatch');
  assert.equal(result.record.diagnosticCategory,'filesystem-identity-mismatch');
  assert.equal(result.record.toolRequestCount,1);
  assert.equal(result.record.processExitCode,0);
  assert.equal(result.record.assistantResponseSeen,false);
  assert.equal(result.record.resultEventSeen,true);
  assert.equal(JSON.stringify(result.record).includes(f.workspacePath),false);
  const unsafe=await runCopilotOnce({
    config:f.config,stateDir:join(f.root,'state'),
    runner:async()=>{throw Object.assign(new Error('copilot-session-cwd-mismatch'),{
      diagnosticCategory:f.workspacePath,assistantResponseSeen:f.workspacePath
    });}
  });
  assert.equal('diagnosticCategory' in unsafe.record,false);
  assert.equal('assistantResponseSeen' in unsafe.record,false);
});

test('invalid config and path mismatch prevent CLI spawn',async t=>{
  const f=fixture(t);
  let called=false;
  assert.throws(()=>loadCopilotConfig(join(f.root,'not-found.json')),/copilot-read-only-not-configured/);
  const result=await runCopilotOnce({
    config:{...f.config,workspacePath:join(f.root,'outside')},stateDir:join(f.root,'state'),
    runner:async options=>{planCopilotLaunch(options);called=true;return {sessionCwdMatches:true};}
  });
  assert.equal(result.status,'blocked');
  assert.equal(result.reason,'workspace-path-unavailable');
  assert.equal(called,false);
});
