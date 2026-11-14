import test from 'node:test';
import assert from 'node:assert/strict';
import {mkdtempSync,rmSync,readFileSync,writeFileSync,mkdirSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import {REPO,INBOX_PATH,contentPath,sha256,checkPing,checkInbox,checkReviewOnlyProposal,pollOnce,githubContent} from '../src/relay.mjs';

const id='123e4567-e89b-42d3-a456-426614174000', id2='123e4567-e89b-42d3-a456-426614174001';
const now=Date.UTC(2026,10,10,15,0,0);
const ping=()=>({version:'hic.dev-relay-ping/1',kind:'SyntheticPing',orderId:id,createdAt:'2026-11-10T14:59:00Z',expiresAt:'2026-12-01T00:00:00Z',source:{repository:REPO,issueNumber:93},note:'TRANSPORT_TEST_ONLY',executionAllowed:false});
const proposal=()=>({version:'hic.dev-work-order.proposal/1',kind:'DeveloperWorkOrderProposal',mode:'review-only',orderId:id2,createdAt:'2026-11-10T14:59:00Z',expiresAt:'2026-11-11T00:00:00Z',source:{repository:REPO,queueIssue:75,issueNumber:93,issueUpdatedAt:'2026-11-10T14:58:00Z'},task:{taskId:id,repository:REPO,branch:'feature/synthetic',baseSha:'a'.repeat(40),allowedPaths:['tools/dev-handoff/readme.md'],objective:'Display only',requiresHumanApproval:true},review:{status:'unsigned',executionAllowed:false,signature:null}});
const tmp=async fn=>{const dir=mkdtempSync(join(tmpdir(),'hic-relay-t-'));try{return await fn(dir)}finally{rmSync(dir,{force:true,recursive:true})}};
const fixture=items=>{const m=new Map(),orders=[];for(const x of items){const bytes=Buffer.from(JSON.stringify(x)+'\n');orders.push({orderId:x.orderId,digest:sha256(bytes)});m.set(contentPath(x.orderId),bytes)}m.set(INBOX_PATH,Buffer.from(JSON.stringify({version:'hic.dev-inbox/1',kind:'ReviewOnlyInbox',orders})));return {get:async p=>m.get(p),map:m}};

test('synthetic ping produces durable untrusted receipt, no execution',()=>tmp(async dir=>{
 const f=fixture([ping()]),r=await pollOnce({fetchFile:f.get,stateDir:dir,clock:()=>now});
 assert.equal(r[0].result,'seen-untrusted');
 const ack=JSON.parse(readFileSync(join(dir,'acks',id+'.json'),'utf8'));
 assert.equal(ack.executionAllowed,false);
 assert.equal((await pollOnce({fetchFile:f.get,stateDir:dir,clock:()=>now}))[0].result,'already-seen');
}));
test('cached receipt prevents repeated remote order fetch',()=>tmp(async dir=>{
 const f=fixture([ping()]),calls=[],get=async p=>{calls.push(p);return f.get(p)};
 await pollOnce({fetchFile:get,stateDir:dir,clock:()=>now});calls.length=0;
 await pollOnce({fetchFile:get,stateDir:dir,clock:()=>now});assert.deepEqual(calls,[INBOX_PATH]);
}));
test('changed bytes are blocked by digest',()=>tmp(async dir=>{
 const f=fixture([ping()]);f.map.set(contentPath(id),Buffer.from(JSON.stringify({...ping(),note:'altered'})));
 await assert.rejects(pollOnce({fetchFile:f.get,stateDir:dir,clock:()=>now}),/order-digest-mismatch/);
}));
test('same ID with changed digest cannot overwrite receipt',()=>tmp(async dir=>{
 await pollOnce({fetchFile:fixture([ping()]).get,stateDir:dir,clock:()=>now});
 await assert.rejects(pollOnce({fetchFile:fixture([{...ping(),expiresAt:'2026-12-02T00:00:00Z'}]).get,stateDir:dir,clock:()=>now}),/ack-replay-conflict/);
}));
test('unsigned proposals cannot acquire execution authority',()=>{
 const p=proposal();assert.equal(checkReviewOnlyProposal(p,now).type,'review-only-proposal');
 assert.throws(()=>checkReviewOnlyProposal({...p,review:{...p.review,executionAllowed:true}},now),/approval-not-allowed/);
 assert.throws(()=>checkReviewOnlyProposal({...p,exec:'cmd'},now),/invalid-proposal/);
 assert.throws(()=>checkReviewOnlyProposal({...p,task:{...p.task,allowedPaths:['../secrets']}},now),/invalid-paths/);
});
test('cross-repo, expiration and malformed dates fail closed',()=>{
 assert.throws(()=>checkPing({...ping(),source:{repository:'attacker/repo',issueNumber:93}},now),/invalid-source/);
 assert.throws(()=>checkPing({...ping(),createdAt:'2026-10-31T00:00:00Z',expiresAt:'2026-11-01T00:00:00Z'},now),/expired/);
 assert.throws(()=>checkPing({...ping(),createdAt:'invalid'},now),/invalid-date/);
});
test('duplicate or extended index is rejected',()=>{
 const x={version:'hic.dev-inbox/1',kind:'ReviewOnlyInbox',orders:[{orderId:id,digest:'f'.repeat(64)}]};
 assert.equal(checkInbox(x).length,1);
 assert.throws(()=>checkInbox({...x,orders:[...x.orders,...x.orders]}),/duplicate-index-entry/);
 assert.throws(()=>checkInbox({...x,autoRun:true}),/invalid-inbox/);
});
test('untrusted existing receipt cannot be overwritten',()=>tmp(async dir=>{
 mkdirSync(join(dir,'acks'),{recursive:true});writeFileSync(join(dir,'acks',id+'.json'),'not JSON');
 await assert.rejects(pollOnce({fetchFile:fixture([ping()]).get,stateDir:dir,clock:()=>now}),/unreadable-ack/);
}));
test('GitHub fetch is anonymous GET, fixed repo/ref, no redirects',async()=>{
 const bytes=Buffer.from('{}'),requests=[];
 const payload={type:'file',path:INBOX_PATH,encoding:'base64',sha:'a'.repeat(40),size:bytes.length,content:bytes.toString('base64')};
 const out=await githubContent(INBOX_PATH,{fetchImpl:async(url,opts)=>{requests.push({url:String(url),opts});return {status:200,text:async()=>JSON.stringify(payload)}}});
 assert.deepEqual(out,bytes);
 assert.ok(requests[0].url.startsWith('https://api.github.com/repos/'+REPO+'/contents/'));
 assert.equal(requests[0].opts.method,'GET');assert.equal(requests[0].opts.redirect,'manual');
 assert.equal(requests[0].opts.headers.Authorization,undefined);
 await assert.rejects(githubContent(INBOX_PATH,{fetchImpl:async()=>({status:302,text:async()=>''})}),/github-read-failed/);
});
test('GitHub symlinks and oversized objects are rejected',async()=>{
 const payload={type:'symlink',path:INBOX_PATH,encoding:'base64',sha:'a'.repeat(40),size:2,content:'e30='};
 const get=async()=>({status:200,text:async()=>JSON.stringify(payload)});
 await assert.rejects(githubContent(INBOX_PATH,{fetchImpl:get}),/invalid-github-file/);
 payload.type='file';payload.size=30000;
 await assert.rejects(githubContent(INBOX_PATH,{fetchImpl:get}),/invalid-github-file/);
});
