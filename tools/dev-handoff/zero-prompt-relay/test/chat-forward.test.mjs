import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { chmodSync, existsSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { CONFIG_VERSION, buildLaunch, checkConfig, checkIssue, fetchIssue, forwardOnce } from '../src/chat-forward.mjs';

const here=dirname(fileURLToPath(import.meta.url));
const REPO='Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint';
const N=4242;
const BODY='Kabul kriteri: "quotes" & %PATH% | <tag> ^ ! çalışmamalı';
const issue=(over={})=>({number:N,html_url:'https://github.com/'+REPO+'/issues/'+N,state:'open',user:{login:'Cabatoy'},title:'Synthetic relay task',body:BODY,updated_at:'2026-11-13T00:00:00Z',...over});
const config=(over={})=>({version:CONFIG_VERSION,enabled:true,repository:REPO,issueNumber:N,trustedAuthor:'Cabatoy',codeCommand:process.platform==='win32'?'C:\\fake\\code.cmd':'/fake/code',workspacePath:process.platform==='win32'?'C:\\hic\\test-worktree':'/tmp/hic-test-worktree',expectedBranch:'task/171-test',...over});
const tmp=async fn=>{const dir=mkdtempSync(join(tmpdir(),'hic-chat-t-'));try{return await fn(dir)}finally{rmSync(dir,{force:true,recursive:true})}};
const okFetch=(body=issue(),calls=[])=>async(url,opts)=>{calls.push({url:String(url),opts});return {status:200,text:async()=>JSON.stringify(body)}};
const countingLaunch=(calls,result={exitCode:0,error:null})=>async spec=>{calls.push(spec);return result};
const expectedBranch='task/171-test';

function createGitWorkspace(parent) {
  const workspacePath=join(parent,'workspace');
  mkdirSync(workspacePath);
  const git=args=>execFileSync('git',['-C',workspacePath,...args],{stdio:'ignore',windowsHide:true});
  git(['init','--quiet']);
  git(['checkout','--quiet','-b',expectedBranch]);
  git(['config','user.name','HIC Offline Test']);
  git(['config','user.email','hic-offline@example.invalid']);
  writeFileSync(join(workspacePath,'README.md'),'offline fixture\n');
  git(['add','README.md']);
  git(['-c','user.name=HIC Offline Test','-c','user.email=hic-offline@example.invalid','commit','--quiet','-m','offline fixture']);
  git(['remote','add','origin','https://github.com/'+REPO+'.git']);
  return workspacePath;
}

function fakeCodeCommand(dir) {
  const fake=join(here,'fixtures','fake-code.mjs');
  if(process.platform==='win32') {
    const path=join(dir,'fake-code.cmd');
    writeFileSync(path,'@echo off\r\n"'+process.execPath+'" "'+fake+'" %*\r\nexit /b %ERRORLEVEL%\r\n');
    return path;
  }
  const path=join(dir,'fake-code.sh');
  writeFileSync(path,'#!/bin/sh\nexec "'+process.execPath+'" "'+fake+'" "$@"\n');chmodSync(path,0o755);
  return path;
}

test('Windows relay process fails closed rather than launching an empty or last-active chat window',{skip:process.platform!=='win32'},()=>tmp(async dir=>{
  const state=join(dir,'state'),cfg=join(dir,'chat-forward.json'),issueFile=join(dir,'issue.json');
  const codeLog=join(dir,'code.log'),fetchLog=join(dir,'fetch.log');
  const workspacePath=createGitWorkspace(dir);
  writeFileSync(cfg,JSON.stringify(config({codeCommand:fakeCodeCommand(dir),workspacePath})));
  writeFileSync(issueFile,JSON.stringify(issue()));
  const result=spawnSync(process.execPath,[join(here,'fixtures','forward-process.mjs'),cfg,state,issueFile,fetchLog],{
    env:{...process.env,FAKE_CODE_LOG:codeLog},
    encoding:'utf8'
  });
  assert.equal(result.status,1,result.stderr);
  assert.deepEqual(JSON.parse(result.stdout.trim()),{status:'blocked',reason:'chat-workspace-binding-unavailable'});
  assert.equal(existsSync(codeLog),false,'no VS Code command is launched without an exact workspace-window binding');
  assert.equal(existsSync(fetchLog),false,'the issue is not read when the handoff cannot be bound');
  assert.equal(existsSync(state),false,'no handoff record is created');
}));

test('forwarding fails closed before network, state, or launch when no exact window binding is available',()=>tmp(async dir=>{
  const stateDir=join(dir,'state'),fetches=[],launches=[];
  await assert.rejects(forwardOnce({
    config:config(),
    stateDir,
    fetchImpl:okFetch(issue(),fetches),
    launch:countingLaunch(launches),
    verifyWorkspace:()=>{}
  }),/chat-workspace-binding-unavailable/);
  assert.equal(fetches.length,0);
  assert.equal(launches.length,0);
  assert.equal(existsSync(stateDir),false);
}));

test('workspace preflight blocks the issue read and launch when the target is not a Git worktree',()=>tmp(async dir=>{
  const fetches=[],launches=[];
  await assert.rejects(forwardOnce({
    config:config({workspacePath:dir}),
    stateDir:join(dir,'state'),
    fetchImpl:okFetch(issue(),fetches),
    launch:countingLaunch(launches)
  }),/workspace-git-preflight-failed/);
  assert.equal(fetches.length,0);
  assert.equal(launches.length,0);
}));

test('issue validation remains fail-closed independently of workspace launch support',()=>{
  const c=checkConfig(config());
  const cases=[
    [issue({pull_request:{}}),/issue-is-pull-request/],
    [issue({state:'closed'}),/issue-not-open/],
    [issue({user:{login:'someone-else'}}),/issue-author-untrusted/],
    [issue({number:N+1}),/invalid-issue/],
    [issue({html_url:'https://github.com/attacker/repo/issues/'+N}),/invalid-issue/],
    [issue({body:'x'.repeat(20001)}),/issue-body-too-large/]
  ];
  for(const [body,error] of cases) assert.throws(()=>checkIssue(body,c),error);
});

test('config is explicit opt-in with a fixed shape',()=>{
  assert.equal(checkConfig(config()).chatMode,'ask','missing chatMode fails closed to Ask');
  assert.throws(()=>checkConfig(config({chatMode:'agent'})),/chat-agent-disabled/,'Agent execution is disabled');
  assert.throws(()=>checkConfig(config({enabled:false})),/chat-forward-disabled/);
  assert.throws(()=>checkConfig({...config(),autoMerge:true}),/invalid-chat-config/);
  const {issueNumber,...missing}=config();assert.throws(()=>checkConfig(missing),/invalid-chat-config/);
  assert.throws(()=>checkConfig(config({repository:'../etc'})),/invalid-chat-repository/);
  assert.throws(()=>checkConfig(config({issueNumber:0})),/invalid-chat-issue/);
  assert.throws(()=>checkConfig(config({codeCommand:'code'})),/invalid-chat-command/);
  assert.throws(()=>checkConfig(config({codeCommand:(process.platform==='win32'?'C:\\x\\':'/x/')+'code&calc'})),/invalid-chat-command/);
  assert.throws(()=>checkConfig(config({chatMode:'yolo'})),/invalid-chat-mode/);
  const {workspacePath,...unbound}=config();
  assert.throws(()=>checkConfig(unbound),/invalid-chat-config/);
});

test('issue read is an anonymous GET to the configured issue without redirects',async()=>{
  const calls=[];
  const r=await fetchIssue(checkConfig(config()),{fetchImpl:okFetch(issue(),calls)});
  assert.equal(r.number,N);
  assert.equal(calls[0].url,'https://api.github.com/repos/'+REPO+'/issues/'+N);
  assert.equal(calls[0].opts.method,'GET');assert.equal(calls[0].opts.redirect,'manual');
  assert.equal(calls[0].opts.headers.Authorization,undefined);
  await assert.rejects(fetchIssue(checkConfig(config()),{fetchImpl:async()=>({status:302,text:async()=>''})}),/github-read-failed/);
});

test('Windows launch fails closed instead of targeting an empty or last-active window',()=>{
  const c={...config(),codeCommand:'C:\\Program Files\\Code\\bin\\code.cmd',workspacePath:'C:\\Users\\dev\\workspace',chatMode:'ask'};
  assert.throws(()=>buildLaunch(c,'C:\\Users\\dev\\AppData\\Local\\HICDevRelay\\chat-forward\\prompts\\x.md','win32'),/chat-workspace-binding-unavailable/);
  assert.throws(()=>buildLaunch(c,'C:\\Users\\a&b\\x.md','win32'),/unsafe-prompt-path/);
  assert.throws(()=>buildLaunch({...c,codeCommand:'code.cmd'},'C:\\x.md','win32'),/invalid-chat-command/);
});

test('CLI requires the opt-in flag and a local config before doing anything',()=>tmp(async dir=>{
  const cli=join(here,'..','src','chat-cli.mjs');
  assert.equal(spawnSync(process.execPath,[cli,'--once'],{encoding:'utf8'}).status,2);
  const r=spawnSync(process.execPath,[cli,'--enable-chat-forward','--once','--state-dir',dir],{encoding:'utf8'});
  assert.equal(r.status,1);
  assert.equal(JSON.parse(r.stderr.trim()).reason,'chat-forward-not-configured');
}));
