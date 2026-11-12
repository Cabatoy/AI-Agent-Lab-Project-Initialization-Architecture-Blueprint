import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { chmodSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { CHAT_PROMPT, CONFIG_VERSION, buildLaunch, checkConfig, fetchIssue, forwardOnce } from '../src/chat-forward.mjs';

const here=dirname(fileURLToPath(import.meta.url));
const REPO='Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint';
const N=4242;
const BODY='Kabul kriteri: "quotes" & %PATH% | <tag> ^ ! çalışmamalı';
const issue=(over={})=>({number:N,html_url:'https://github.com/'+REPO+'/issues/'+N,state:'open',user:{login:'Cabatoy'},title:'Synthetic relay task',body:BODY,updated_at:'2026-11-13T00:00:00Z',...over});
const config=(over={})=>({version:CONFIG_VERSION,enabled:true,repository:REPO,issueNumber:N,trustedAuthor:'Cabatoy',codeCommand:process.platform==='win32'?'C:\\fake\\code.cmd':'/fake/code',...over});
const tmp=async fn=>{const dir=mkdtempSync(join(tmpdir(),'hic-chat-t-'));try{return await fn(dir)}finally{rmSync(dir,{force:true,recursive:true})}};
const okFetch=(body=issue(),calls=[])=>async(url,opts)=>{calls.push({url:String(url),opts});return {status:200,text:async()=>JSON.stringify(body)}};
const countingLaunch=(calls,result={exitCode:0,error:null})=>async spec=>{calls.push(spec);return result};
const records=dir=>join(dir,'chat-forward','records');
const recordFile=dir=>join(records(dir),REPO.replace('/','__')+'__'+N+'.json');

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
const lines=path=>existsSync(path)?readFileSync(path,'utf8').trim().split('\n').filter(Boolean):[];

test('real fake-command handoff happens once and is not re-sent after a process restart',()=>tmp(async dir=>{
  const state=join(dir,'state'),cfg=join(dir,'chat-forward.json'),issueFile=join(dir,'issue.json');
  const codeLog=join(dir,'code.log'),fetchLog=join(dir,'fetch.log');
  writeFileSync(cfg,JSON.stringify(config({codeCommand:fakeCodeCommand(dir)})));
  writeFileSync(issueFile,JSON.stringify(issue()));
  const run=()=>{
    const r=spawnSync(process.execPath,[join(here,'fixtures','forward-process.mjs'),cfg,state,issueFile,fetchLog],{env:{...process.env,FAKE_CODE_LOG:codeLog},encoding:'utf8'});
    assert.equal(r.status,0,r.stderr);return JSON.parse(r.stdout.trim());
  };
  const first=run();
  assert.equal(first.status,'handed-off-unverified');
  assert.equal(first.launcherExitCode,0);
  assert.equal(first.completionVerified,false);
  const sent=lines(codeLog).map(l=>JSON.parse(l));
  assert.equal(sent.length,1);
  const promptPath=join(state,'chat-forward','prompts',REPO.replace('/','__')+'__'+N+'.md');
  assert.deepEqual(sent[0].argv,['chat','-m','agent','-r','-a',promptPath,CHAT_PROMPT]);
  assert.ok(sent[0].attached.includes('https://github.com/'+REPO+'/issues/'+N));
  assert.ok(sent[0].attached.includes(BODY),'issue body reaches chat only through the attached file');
  const second=run();
  assert.equal(second.status,'already-forwarded');
  assert.equal(lines(codeLog).length,1,'restart must not re-send the issue');
  assert.equal(lines(fetchLog).length,1,'restart must not even re-read the issue');
}));

test('exit code 0 is recorded as handed-off-unverified, never as completed',()=>tmp(async dir=>{
  const r=await forwardOnce({config:config(),stateDir:dir,fetchImpl:okFetch(),launch:countingLaunch([])});
  assert.equal(r.status,'handed-off-unverified');
  const rec=JSON.parse(readFileSync(recordFile(dir),'utf8'));
  assert.equal(rec.status,'handed-off-unverified');assert.equal(rec.completionVerified,false);assert.equal(rec.launcherExitCode,0);
}));

test('failed or timed-out launches are recorded and not retried automatically',()=>tmp(async dir=>{
  const calls=[];
  const r=await forwardOnce({config:config(),stateDir:dir,fetchImpl:okFetch(),launch:countingLaunch(calls,{exitCode:3,error:null})});
  assert.equal(r.status,'launch-failed');
  assert.equal((await forwardOnce({config:config(),stateDir:dir,fetchImpl:okFetch(),launch:countingLaunch(calls)})).status,'already-forwarded');
  assert.equal(calls.length,1);
}));

test('a claim left by a crash blocks re-sending without network access',()=>tmp(async dir=>{
  mkdirSync(records(dir),{recursive:true});
  writeFileSync(recordFile(dir),JSON.stringify({version:'hic.dev-chat-forward-record/1',repository:REPO,issueNumber:N,status:'claimed',completionVerified:false}));
  const fetches=[],launches=[];
  const r=await forwardOnce({config:config(),stateDir:dir,fetchImpl:okFetch(issue(),fetches),launch:countingLaunch(launches)});
  assert.equal(r.status,'already-forwarded');assert.equal(r.recordStatus,'claimed');
  assert.equal(fetches.length,0);assert.equal(launches.length,0);
}));

test('tampered or unreadable records fail closed',()=>tmp(async dir=>{
  mkdirSync(records(dir),{recursive:true});
  writeFileSync(recordFile(dir),JSON.stringify({version:'hic.dev-chat-forward-record/1',repository:REPO,issueNumber:N+1,status:'claimed',completionVerified:false}));
  await assert.rejects(forwardOnce({config:config(),stateDir:dir,fetchImpl:okFetch(),launch:countingLaunch([])}),/chat-record-conflict/);
  writeFileSync(recordFile(dir),'not json');
  await assert.rejects(forwardOnce({config:config(),stateDir:dir,fetchImpl:okFetch(),launch:countingLaunch([])}),/unreadable-chat-record/);
}));

test('only the configured open issue from the trusted author is forwarded',()=>tmp(async dir=>{
  const cases=[
    [issue({pull_request:{}}),/issue-is-pull-request/],
    [issue({state:'closed'}),/issue-not-open/],
    [issue({user:{login:'someone-else'}}),/issue-author-untrusted/],
    [issue({number:N+1}),/invalid-issue/],
    [issue({html_url:'https://github.com/attacker/repo/issues/'+N}),/invalid-issue/],
    [issue({body:'x'.repeat(20001)}),/issue-body-too-large/]
  ];
  const launches=[];
  for(const [body,error] of cases) await assert.rejects(forwardOnce({config:config(),stateDir:dir,fetchImpl:okFetch(body),launch:countingLaunch(launches)}),error);
  assert.equal(launches.length,0);
  assert.equal(existsSync(recordFile(dir)),false,'rejected issues leave no record so a fixed issue can be forwarded later');
}));

test('config is explicit opt-in with a fixed shape',()=>{
  assert.equal(checkConfig(config()).chatMode,'agent');
  assert.throws(()=>checkConfig(config({enabled:false})),/chat-forward-disabled/);
  assert.throws(()=>checkConfig({...config(),autoMerge:true}),/invalid-chat-config/);
  const {issueNumber,...missing}=config();assert.throws(()=>checkConfig(missing),/invalid-chat-config/);
  assert.throws(()=>checkConfig(config({repository:'../etc'})),/invalid-chat-repository/);
  assert.throws(()=>checkConfig(config({issueNumber:0})),/invalid-chat-issue/);
  assert.throws(()=>checkConfig(config({codeCommand:'code'})),/invalid-chat-command/);
  assert.throws(()=>checkConfig(config({codeCommand:(process.platform==='win32'?'C:\\x\\':'/x/')+'code&calc'})),/invalid-chat-command/);
  assert.throws(()=>checkConfig(config({chatMode:'yolo'})),/invalid-chat-mode/);
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

test('Windows launch line contains only validated arguments',()=>{
  const c=checkConfig(config({codeCommand:'C:\\Program Files\\Code\\bin\\code.cmd'}));
  const spec=buildLaunch(c,'C:\\Users\\dev\\AppData\\Local\\HICDevRelay\\chat-forward\\prompts\\x.md','win32');
  assert.deepEqual(spec.args.slice(0,3),['/d','/s','/c']);
  assert.equal(spec.args[3],'""C:\\Program Files\\Code\\bin\\code.cmd" chat -m agent -r -a "C:\\Users\\dev\\AppData\\Local\\HICDevRelay\\chat-forward\\prompts\\x.md" "'+CHAT_PROMPT+'""');
  assert.equal(spec.verbatim,true);
  assert.throws(()=>buildLaunch(c,'C:\\Users\\a&b\\x.md','win32'),/unsafe-prompt-path/);
});

test('CLI requires the opt-in flag and a local config before doing anything',()=>tmp(async dir=>{
  const cli=join(here,'..','src','chat-cli.mjs');
  assert.equal(spawnSync(process.execPath,[cli,'--once'],{encoding:'utf8'}).status,2);
  const r=spawnSync(process.execPath,[cli,'--enable-chat-forward','--once','--state-dir',dir],{encoding:'utf8'});
  assert.equal(r.status,1);
  assert.equal(JSON.parse(r.stderr.trim()).reason,'chat-forward-not-configured');
}));
