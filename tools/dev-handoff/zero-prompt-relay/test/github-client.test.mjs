import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, rmSync } from 'node:fs';
import { EventEmitter } from 'node:events';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createGitHubClient, describeGitHubError, gcmTokenProvider } from '../src/github-client.mjs';
import { MAX_BACKOFF_MS, WATCH_VERSION, checkWatchConfig, nextDelayMs, watchLoop, watchOnce, watchPoll } from '../src/watch.mjs';
import { CONFIG_VERSION, fetchIssue, checkConfig } from '../src/chat-forward.mjs';

// Synthetic, credential-shaped marker only; never a real token.
const TOKEN='synthetic_marker_'+'x'.repeat(24);
const NOW=Date.parse('2026-11-14T12:00:00Z');
const REPO='Cabatoy/test-repo';
const config=(over={})=>({version:WATCH_VERSION,repository:REPO,trustedAuthor:'Cabatoy',relayLabel:'hic-relay-ready',githubAuth:'gcm',...over});
const issues=[
  {number:101,title:'Valid',body:'b',updated_at:'2026-11-13T01:00:00Z',state:'open',user:{login:'Cabatoy'}},
  {number:102,title:'PR',body:'',updated_at:'2026-11-13T01:00:00Z',state:'open',user:{login:'Cabatoy'},pull_request:{}},
  {number:103,title:'Untrusted',body:'',updated_at:'2026-11-13T01:00:00Z',state:'open',user:{login:'mallory'}},
];
const respond=(status,body='',headers={})=>({status,headers:new Headers(headers),text:async()=>typeof body==='string'?body:JSON.stringify(body)});
const scripted=(responses,calls=[])=>async(url,opts)=>{calls.push({url:String(url),opts});const r=responses.shift();if(r instanceof Error)throw r;return r;};
const client=(fetchImpl,over={})=>createGitHubClient({auth:'gcm',fetchImpl,tokenProvider:async()=>TOKEN,clock:()=>NOW,...over});
const tmp=async fn=>{const dir=mkdtempSync(join(tmpdir(),'hic-gh-t-'));try{return await fn(dir)}finally{rmSync(dir,{force:true,recursive:true})}};
const quiet={log:()=>{},onError:()=>{}};

test('authenticated polling sends a Bearer token and keeps trusted-author/PR filtering',async()=>{
  const calls=[];
  const r=await watchPoll({config:config(),stateDir:mkdtempSync(join(tmpdir(),'hic-gh-t-')),github:client(scripted([respond(200,issues)],calls))});
  assert.deepEqual(r.pending.map(x=>x.number),[101]);
  assert.equal(calls[0].opts.headers.Authorization,'Bearer '+TOKEN);
  assert.equal(calls[0].opts.redirect,'manual');
  assert.ok(calls[0].url.startsWith('https://api.github.com/repos/'+REPO+'/issues?'));
  assert.match(calls[0].url,/labels=hic-relay-ready/);
});

test('configured auth with no credential fails closed without any anonymous request',async()=>{
  for(const token of [null,'',undefined,'bad token\nwith newline']) {
    const calls=[];
    const c=createGitHubClient({auth:'gcm',fetchImpl:scripted([respond(200,issues)],calls),tokenProvider:async()=>token});
    await assert.rejects(c.getJson('https://api.github.com/repos/'+REPO+'/issues'),/github-auth-unavailable/);
    assert.equal(calls.length,0,'no request may be sent when configured auth is unavailable');
  }
  const throwing=createGitHubClient({auth:'env',fetchImpl:scripted([]),tokenProvider:async()=>{throw new Error(TOKEN)}});
  await assert.rejects(throwing.getJson('https://api.github.com/x'),e=>e.message==='github-auth-unavailable'&&!JSON.stringify(e.diagnostic).includes(TOKEN));
});

test('HTTP 401 is a fatal auth error that clears the cached token and stops the loop',async()=>{
  let provided=0;
  const c=createGitHubClient({auth:'gcm',fetchImpl:scripted([respond(401,'{"message":"Bad credentials"}'),respond(200,[])]),tokenProvider:async()=>{provided++;return TOKEN}});
  await assert.rejects(c.getJson('https://api.github.com/x'),e=>e.message==='github-auth-invalid'&&e.diagnostic.status===401);
  await c.getJson('https://api.github.com/x');
  assert.equal(provided,2,'token is re-read after 401');

  const sleeps=[];
  const r=await watchLoop({config:config(),stateDir:mkdtempSync(join(tmpdir(),'hic-gh-t-')),forwardImpl:async()=>assert.fail('must not forward'),github:client(scripted([respond(401)])),sleep:async ms=>sleeps.push(ms),maxPolls:3,...quiet});
  assert.deepEqual(r,{status:'blocked',reason:'github-auth-invalid',polls:1});
  assert.deepEqual(sleeps,[]);
});

test('HTTP 403 without rate-limit signals is a fatal forbidden error',async()=>{
  const r=await watchLoop({config:config(),stateDir:mkdtempSync(join(tmpdir(),'hic-gh-t-')),forwardImpl:async()=>({status:'handed-off-unverified'}),github:client(scripted([respond(403,'{}',{'x-ratelimit-remaining':'4000'})])),sleep:async()=>{},maxPolls:3,...quiet});
  assert.equal(r.status,'blocked');assert.equal(r.reason,'github-forbidden');
});

test('HTTP 403 with exhausted quota is rate limited and waits until reset (bounded)',async()=>{
  const reset=Math.floor(NOW/1000)+120;
  const c=client(scripted([respond(403,'{}',{'x-ratelimit-remaining':'0','x-ratelimit-reset':String(reset),'x-github-request-id':'ABC1:2'})]));
  const e=await c.getJson('https://api.github.com/x').catch(x=>x);
  assert.equal(e.message,'github-rate-limited');
  assert.equal(e.diagnostic.retryAfterMs,120000);
  assert.equal(e.diagnostic.rateLimitRemaining,0);
  assert.equal(nextDelayMs({error:e,intervalMs:60000,consecutiveFailures:1}),121000);
  assert.equal(nextDelayMs({error:{message:'github-rate-limited',diagnostic:{retryAfterMs:6*3600*1000}},intervalMs:60000,consecutiveFailures:1}),MAX_BACKOFF_MS);
});

test('HTTP 429 honours Retry-After, keeps polling and resets after success',async()=>{
  await tmp(async dir=>{
    const sleeps=[],forwarded=[],logs=[];
    const fetchImpl=scripted([respond(429,'{}',{'retry-after':'90'}),respond(200,issues)]);
    const r=await watchLoop({config:config(),stateDir:dir,github:client(fetchImpl),forwardImpl:async({config:fc,github})=>{forwarded.push({n:fc.issueNumber,auth:github.auth});return {status:'handed-off-unverified'}},sleep:async ms=>sleeps.push(ms),maxPolls:2,log:m=>logs.push(m),onError:()=>{}});
    assert.equal(r.status,'stopped');
    assert.deepEqual(sleeps,[91000]);
    assert.deepEqual(forwarded,[{n:101,auth:'gcm'}],'authenticated client reaches chat-forward; only trusted issue forwarded');
    assert.ok(logs.some(l=>l.includes('github-rate-limited')&&l.includes('retryAfterMs=90000')));
    assert.ok(logs.every(l=>!l.includes(TOKEN)),'token never logged');
  });
});

test('network failures back off exponentially and stay bounded',()=>{
  const e={message:'github-network-error',diagnostic:{}};
  assert.deepEqual([1,2,3].map(n=>nextDelayMs({error:e,intervalMs:60000,consecutiveFailures:n})),[60000,120000,240000]);
  assert.equal(nextDelayMs({error:e,intervalMs:60000,consecutiveFailures:50}),MAX_BACKOFF_MS);
  assert.equal(nextDelayMs({intervalMs:60000}),60000);
});

test('deduplication still prevents re-forwarding with authenticated polling',async()=>{
  await tmp(async dir=>{
    const forwards=[];
    const forwardImpl=async({config:fc})=>{forwards.push(fc);return {status:'handed-off-unverified'}};
    const github=client(scripted([respond(200,issues),respond(200,issues)]));
    await watchOnce({config:config(),stateDir:dir,github,forwardImpl});
    const second=await watchOnce({config:config(),stateDir:dir,github,forwardImpl});
    assert.equal(forwards.length,1);
    assert.equal(second.status,'no-pending-issues');
    assert.equal(forwards[0].chatMode,'ask','human-reviewed Ask mode remains the default');
  });
});

test('diagnostics never include the token or response bodies',async()=>{
  const c=client(scripted([respond(500,'Authorization: Bearer '+TOKEN,{'x-github-request-id':'id;'+TOKEN})]));
  const e=await c.getJson('https://api.github.com/x').catch(x=>x);
  const text=describeGitHubError(e)+JSON.stringify(e.diagnostic)+e.message+String(e.stack);
  assert.ok(!text.includes(TOKEN));
  assert.match(describeGitHubError(e),/^github-read-failed authMode=gcm status=500/);
});

test('client refuses non-api.github.com URLs so the token cannot leak',async()=>{
  const calls=[];
  await assert.rejects(client(scripted([respond(200,'[]')],calls)).getJson('https://example.com/x'),/github-url-not-allowed/);
  assert.equal(calls.length,0);
});

test('watch config defaults to anonymous and rejects unknown auth modes',()=>{
  assert.equal(checkWatchConfig(config({githubAuth:undefined})).githubAuth,'none');
  assert.throws(()=>checkWatchConfig(config({githubAuth:'token'})),/invalid-github-auth/);
});

test('chat-forward issue read uses the supplied authenticated client',async()=>{
  const calls=[];
  const N=7;
  const c=checkConfig({version:CONFIG_VERSION,enabled:true,repository:REPO,issueNumber:N,trustedAuthor:'Cabatoy',codeCommand:process.platform==='win32'?'C:\\fake\\code.cmd':'/fake/code'});
  const body={number:N,html_url:'https://github.com/'+REPO+'/issues/'+N,state:'open',user:{login:'Cabatoy'},title:'t',body:'b',updated_at:'2026-11-13T00:00:00Z'};
  await fetchIssue(c,{github:client(scripted([respond(200,body)],calls))});
  assert.equal(calls[0].opts.headers.Authorization,'Bearer '+TOKEN);
  await assert.rejects(fetchIssue(c,{github:client(scripted([respond(200,{...body,user:{login:'mallory'}})]))}),/issue-author-untrusted/);
});

test('GCM provider reads only the password field without prompting',async()=>{
  const fakeSpawn=(stdoutText,code=0,seen={})=>(cmd,args,opts)=>{
    Object.assign(seen,{cmd,args,env:opts.env});
    const child=new EventEmitter();
    child.stdout=new EventEmitter();
    child.stdin={on(){},end(input){seen.input=input;setImmediate(()=>{child.stdout.emit('data',stdoutText);child.emit('close',code)})}};
    child.kill=()=>{};
    return child;
  };
  const seen={};
  assert.equal(await gcmTokenProvider({spawnImpl:fakeSpawn('protocol=https\r\nhost=github.com\r\nusername=x\r\npassword='+TOKEN+'\r\n',0,seen)})(),TOKEN);
  assert.deepEqual([seen.cmd,...seen.args],['git','credential','fill']);
  assert.equal(seen.env.GIT_TERMINAL_PROMPT,'0');
  assert.equal(seen.env.GCM_INTERACTIVE,'never');
  assert.equal(seen.input,'protocol=https\nhost=github.com\n\n');
  assert.equal(await gcmTokenProvider({spawnImpl:fakeSpawn('',128)})(),null);
  assert.equal(await gcmTokenProvider({spawnImpl:fakeSpawn('username=x\n',0)})(),null);
});
