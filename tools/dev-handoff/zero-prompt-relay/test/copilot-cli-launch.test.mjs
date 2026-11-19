import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { EventEmitter } from 'node:events';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { buildCopilotArgs, planCopilotLaunch, runCopilotReadOnly, scrubbedEnv, SUPPORTED_COPILOT_VERSION } from '../src/copilot-cli-launch.mjs';

const repository='Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint';
const branch='task/172-test';
function workspace(t) {
  const parent=mkdtempSync(join(tmpdir(),'hic-copilot-launch-'));
  t.after(()=>rmSync(parent,{recursive:true,force:true}));
  const copilotPath=join(parent,'copilot-fake.exe');
  writeFileSync(copilotPath,'fake executable fixture');
  const workspacePath=join(parent,'repo');
  mkdirSync(workspacePath);
  const git=args=>execFileSync('git',['-C',workspacePath,...args],{stdio:'ignore',windowsHide:true});
  git(['init','--quiet']);
  git(['checkout','--quiet','-b',branch]);
  writeFileSync(join(workspacePath,'README.md'),'fixture\n');
  git(['add','README.md']);
  git(['-c','user.name=HIC Offline Test','-c','user.email=hic-offline@example.invalid','commit','--quiet','-m','fixture']);
  git(['remote','add','origin','https://github.com/'+repository+'.git']);
  return {workspacePath,copilotPath,git};
}

const startEvent=(cwd,version=SUPPORTED_COPILOT_VERSION)=>JSON.stringify({type:'session.start',data:{copilotVersion:version,context:{cwd}}})+'\n';
const response='{"type":"assistant.message","data":{"content":"Résumé fixture"}}\n';
const resultEvent='{"type":"result"}\n';
const completion=response+resultEvent;

function fakeSpawn({stdout='',stderr='',stdoutChunks=[stdout],stderrChunks=[stderr],code=0,calls}) {
  return (command,args,opts)=>{
    calls.push({command,args,opts});
    const child=new EventEmitter();
    child.stdout=new EventEmitter();
    child.stderr=new EventEmitter();
    child.kill=()=>{};
    setImmediate(()=>{
      child.emit('exit',code);
      for(const chunk of stderrChunks) if(chunk.length) child.stderr.emit('data',Buffer.from(chunk));
      for(const chunk of stdoutChunks) if(chunk.length) child.stdout.emit('data',Buffer.from(chunk));
      child.emit('close',code);
    });
    return child;
  };
}

test('arguments are read-only and never grant broad permissions or trust',()=>{
  const cwd=resolve(tmpdir(),'ws');
  const list=buildCopilotArgs(cwd);
  const args=list.join(' ');
  assert.deepEqual(list.filter(a=>a.startsWith('--available-tools=')),['--available-tools=view','--available-tools=glob','--available-tools=grep']);
  assert.ok(!list.includes('--available-tools=read'),'permission kind must not be used as a tool name');
  assert.ok(list.every(a=>!/^--(available|excluded)-tools=.*,/.test(a)&&!/^--deny-tool=.*,/.test(a)),'no comma-joined values');
  assert.deepEqual(list.slice(0,2),['-C',cwd]);
  for(const bad of ['--allow-all','--yolo','--allow-all-tools','--allow-all-paths','--allow-all-urls','--trust-folder','--add-dir','--autopilot']) {
    assert.ok(!args.includes(bad),bad);
  }
  assert.match(args,/--deny-tool=shell --deny-tool=write/);
  assert.match(args,/--no-ask-user/);
});

test('environment is allow-listed and drops tokens',()=>{
  const env=scrubbedEnv({PATH:'p',GITHUB_TOKEN:'x',COPILOT_GITHUB_TOKEN:'y',AWS_SECRET_ACCESS_KEY:'z'});
  assert.deepEqual(env,{PATH:'p'});
});

test('launch plan fails closed on bad executable, repository, branch and dirty tree',(t)=>{
  const {workspacePath,copilotPath}=workspace(t);
  assert.throws(()=>planCopilotLaunch({copilotPath:'copilot',workspacePath,expectedBranch:branch,repository}),/invalid-copilot-path/);
  assert.throws(()=>planCopilotLaunch({copilotPath:join(workspacePath,'missing.exe'),workspacePath,expectedBranch:branch,repository}),/copilot-executable-unavailable/);
  if(process.platform==='win32') {
    assert.throws(()=>planCopilotLaunch({copilotPath:copilotPath.replace(/\.exe$/i,'.cmd'),workspacePath,expectedBranch:branch,repository}),/copilot-executable-unavailable/);
  }
  assert.throws(()=>planCopilotLaunch({copilotPath,workspacePath,expectedBranch:branch,repository:'Other/repo'}),/workspace-repository-mismatch/);
  assert.throws(()=>planCopilotLaunch({copilotPath,workspacePath,expectedBranch:'main',repository}),/workspace-branch-mismatch/);
  writeFileSync(join(workspacePath,'dirty.txt'),'x');
  assert.throws(()=>planCopilotLaunch({copilotPath,workspacePath,expectedBranch:branch,repository}),/workspace-not-clean/);
});

test('verified workspace is used as explicit cwd with no shell and parsed JSONL output',async(t)=>{
  const {workspacePath,copilotPath}=workspace(t);
  const calls=[];
  const stdout=Buffer.from((startEvent(workspacePath)+completion).replace(/\n/g,'\r\n').trimEnd());
  const split=stdout.indexOf(Buffer.from('é'))+1;
  const result=await runCopilotReadOnly({copilotPath,workspacePath,expectedBranch:branch,repository},
    {spawnFn:fakeSpawn({stdoutChunks:[stdout.subarray(0,split),stdout.subarray(split)],calls})});
  assert.equal(calls.length,1);
  assert.equal(calls[0].opts.shell,false);
  assert.equal(calls[0].opts.cwd,workspacePath);
  assert.equal(calls[0].args[calls[0].args.indexOf('-C')+1],workspacePath);
  assert.equal(result.sessionCwdMatches,true);
  assert.equal(result.eventCounts['session.start'],1);
  assert.equal(result.assistantResponseSeen,true);
  assert.equal(result.resultEventSeen,true);
  assert.equal(result.eventCounts.result,1);
  assert.equal(result.outputBytes>0,true);
  assert.equal('events' in result,false,'raw events are never returned to callers');
});

test('Windows session cwd accepts equivalent case and trailing-separator representations',async t=>{
  if(process.platform!=='win32') return t.skip('Windows filesystem identity semantics');
  const {workspacePath,copilotPath}=workspace(t);
  const reported=workspacePath.toUpperCase()+'\\';
  const calls=[];
  const result=await runCopilotReadOnly({copilotPath,workspacePath,expectedBranch:branch,repository},
    {spawnFn:fakeSpawn({stdout:startEvent(reported)+completion,calls})});
  assert.equal(result.sessionCwdMatches,true);
});

test('standalone Copilot 1.0.114 success event shape is accepted without exposing raw output',async t=>{
  const {workspacePath,copilotPath}=workspace(t);
  const fixture=readFileSync(new URL('./fixtures/copilot-1.0.114-success.jsonl',import.meta.url),'utf8');
  const stdout=fixture.replace(JSON.stringify('C:\\synthetic\\hic-fixture'),JSON.stringify(workspacePath));
  const calls=[];
  const result=await runCopilotReadOnly({copilotPath,workspacePath,expectedBranch:branch,repository},
    {spawnFn:fakeSpawn({stdout,code:0,calls})});
  assert.equal(calls.length,1);
  assert.equal(result.processExitCode,0);
  assert.equal(result.sessionCwdMatches,true);
  assert.equal(result.assistantResponseSeen,true);
  assert.equal(result.resultEventSeen,true);
  assert.equal(result.toolRequestCount,0);
  assert.deepEqual({...result.eventCounts},{'session.start':1,'assistant.message':1,result:1});
  assert.doesNotMatch(JSON.stringify(result),/HIC_CLI_SMOKE_OK|example\/hic-fixture|fixture\/smoke|0123456789abcdef/);
});

test('invalid tool allowlist, wrong session cwd and unexpected tools fail closed',async(t)=>{
  const {workspacePath,copilotPath}=workspace(t);
  const opts={copilotPath,workspacePath,expectedBranch:branch,repository};
  const run=o=>runCopilotReadOnly(opts,{spawnFn:fakeSpawn({calls:[],...o})});
  await assert.rejects(run({
    stdout:startEvent(workspacePath)+completion,
    stderrChunks:['Warning: unknown ','tool name: read\n'+'x'.repeat(600)]
  }),error=>error.message==='copilot-tool-config-warning'&&error.exitCode===0);
  await assert.rejects(run({stdout:startEvent(workspacePath)+JSON.stringify({type:'session.warning',data:{message:'Unknown tool name "read"'}})+'\n'}),/copilot-tool-config-warning/);
  await assert.rejects(
    run({stdout:startEvent(join(workspacePath,'..'))}),
    error=>error.message==='copilot-session-cwd-mismatch'&&error.diagnosticCategory==='filesystem-identity-mismatch'&&error.exitCode===0
  );
  const allowedRead=JSON.stringify({type:'assistant.message',data:{toolRequests:[{name:'view'}]}})+'\n';
  await assert.rejects(
    run({stdout:startEvent(join(workspacePath,'..'))+allowedRead}),
    error=>error.message==='copilot-session-cwd-mismatch'&&error.toolRequestCount===1&&error.exitCode===0
  );
  await assert.rejects(run({stdout:startEvent(workspacePath,'1.0.115')}),/copilot-cli-version-unsupported/);
  await assert.rejects(run({stdout:'{"type":"b"}\n'}),/copilot-session-cwd-mismatch/);
  const shellCall=JSON.stringify({type:'assistant.message',data:{toolRequests:[{name:'powershell'}]}})+'\n';
  await assert.rejects(run({stdout:startEvent(workspacePath)+shellCall}),/copilot-unexpected-tool/);
  const shellExecution=JSON.stringify({type:'tool.execution_start',data:{toolName:'powershell'}})+'\n';
  await assert.rejects(run({stdout:startEvent(workspacePath)+shellExecution}),/copilot-unexpected-tool/);
  await assert.rejects(run({stdout:shellCall}),/copilot-unexpected-tool/);
  await assert.rejects(run({stdout:startEvent(workspacePath)+'{"type":"assistant.message","data":{"toolRequests":{}}}\n'}),/copilot-unexpected-tool/);
});

test('zero exit distinguishes missing, malformed and ambiguous session context',async t=>{
  const {workspacePath,copilotPath}=workspace(t);
  const opts={copilotPath,workspacePath,expectedBranch:branch,repository};
  for(const [stdout,category] of [
    ['', 'session-start-missing'],
    ['{"type":"session.start","data":null}\n','session-start-invalid'],
    [startEvent(undefined),'session-cwd-missing'],
    [startEvent(172),'session-cwd-invalid'],
    [startEvent('relative-repo'),'session-cwd-invalid'],
    [startEvent(workspacePath)+startEvent(workspacePath),'session-start-duplicate']
  ]) {
    await assert.rejects(runCopilotReadOnly(opts,{spawnFn:fakeSpawn({stdout,calls:[]})}),
      error=>error.message==='copilot-session-cwd-mismatch'&&error.diagnosticCategory===category&&
        error.exitCode===0&&error.assistantResponseSeen===false&&error.resultEventSeen===false);
  }
});

test('zero exit does not pass on startup alone, result alone, or truncated JSONL',async t=>{
  const {workspacePath,copilotPath}=workspace(t);
  const opts={copilotPath,workspacePath,expectedBranch:branch,repository};
  const run=suffix=>runCopilotReadOnly(opts,{spawnFn:fakeSpawn({stdout:startEvent(workspacePath)+suffix,calls:[]})});
  for(const [suffix,assistantResponseSeen,resultEventSeen] of [
    ['',false,false],[resultEvent,false,true],[response,true,false]
  ]) {
    await assert.rejects(run(suffix),error=>error.message==='copilot-session-incomplete'&&
      error.exitCode===0&&error.assistantResponseSeen===assistantResponseSeen&&error.resultEventSeen===resultEventSeen);
  }
  await assert.rejects(run(response+'{"type":"result"'),error=>error.message==='copilot-invalid-output'&&
    error.diagnosticCategory==='stdout-invalid-jsonl'&&error.exitCode===0);
  await assert.rejects(run('null\n'),error=>error.message==='copilot-invalid-output'&&
    error.diagnosticCategory==='stdout-invalid-event');
  await assert.rejects(runCopilotReadOnly(opts,{
    spawnFn:fakeSpawn({stdoutChunks:[startEvent(workspacePath),Buffer.from([0xc3])],calls:[]})
  }),error=>error.message==='copilot-invalid-output'&&error.diagnosticCategory==='stdout-invalid-utf8');
});

test('nonzero exit and malformed output are rejected; no spawn when preflight fails',async(t)=>{
  const {workspacePath,copilotPath}=workspace(t);
  const opts={copilotPath,workspacePath,expectedBranch:branch,repository};
  await assert.rejects(runCopilotReadOnly(opts,{spawnFn:fakeSpawn({code:1,calls:[]})}),/copilot-nonzero-exit/);
  await assert.rejects(runCopilotReadOnly(opts,{spawnFn:fakeSpawn({stdout:startEvent(workspacePath)+'not json',calls:[]})}),/copilot-invalid-output/);
  const calls=[];
  await assert.rejects(async()=>runCopilotReadOnly({...opts,expectedBranch:'main'},{spawnFn:fakeSpawn({calls})}),/workspace-branch-mismatch/);
  assert.equal(calls.length,0);
});

test('timeout terminates the child and rejects without exposing process output',async t=>{
  const {workspacePath,copilotPath}=workspace(t);
  let killed=false;
  const spawnFn=()=> {
    const child=new EventEmitter();
    child.stdout=new EventEmitter();
    child.stderr=new EventEmitter();
    child.kill=()=>{killed=true;};
    return child;
  };
  await assert.rejects(runCopilotReadOnly({copilotPath,workspacePath,expectedBranch:branch,repository},
    {spawnFn,timeoutMs:10}),/copilot-timeout/);
  assert.equal(killed,true);
});
