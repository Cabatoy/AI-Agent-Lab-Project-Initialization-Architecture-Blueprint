import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync,mkdirSync,readFileSync,rmSync,writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { REPO,sha256,INBOX_PATH,contentPath } from '../src/relay.mjs';
import { checkReceipt,ackMarker,readReceipt,publishSyntheticReceipt,relayAndPublish } from '../src/ack.mjs';

const id='123e4567-e89b-42d3-a456-426614174000', digest='a'.repeat(64);
const token='github_pat_'+'A'.repeat(32);
const receipt={version:'hic.dev-relay-ack/1',orderId:id,digest,type:'synthetic-ping',observedAt:'2026-11-10T15:30:00Z',status:'seen-untrusted',executionAllowed:false};
const temp=async fn=>{const d=mkdtempSync(join(tmpdir(),'hic-ack-t-'));try{return await fn(d)}finally{rmSync(d,{recursive:true,force:true})}};
test('strict local receipt never grants execution',()=>{
  assert.equal(checkReceipt(receipt,{orderId:id,digest}).executionAllowed,false);
  assert.throws(()=>checkReceipt({...receipt,executionAllowed:true},{orderId:id,digest}),/invalid-ack/);
  assert.throws(()=>checkReceipt({...receipt,type:'review-only-proposal'},{orderId:id,digest}),/invalid-ack/);
  assert.throws(()=>checkReceipt({...receipt,extra:'secret'},{orderId:id,digest}),/invalid-ack/);
});
test('receipt must match precise id and digest',()=>{
  assert.throws(()=>checkReceipt(receipt,{orderId:id,digest:'b'.repeat(64)}),/invalid-ack/);
});
test('local read rejects mismatched receipts and does not rewrite',()=>temp(async d=>{
  mkdirSync(join(d,'acks'));writeFileSync(join(d,'acks',id+'.json'),JSON.stringify(receipt));
  assert.equal(readReceipt({orderId:id,digest},d).orderId,id);
  assert.throws(()=>readReceipt({orderId:id,digest:'c'.repeat(64)},d),/invalid-ack/);
}));
test('publisher without explicitly provided scoped token cannot call network',async()=>{
  let calls=0;
  await assert.rejects(publishSyntheticReceipt(checkReceipt(receipt,{orderId:id,digest}),{fetchImpl:async()=>{calls++;return {}}}),/missing-scoped-token/);
  assert.equal(calls,0);
});
test('GET existing acknowledgment deduplicates; no POST or token logging',async()=>{
  const checked=checkReceipt(receipt,{orderId:id,digest});
  const calls=[];
  const result=await publishSyntheticReceipt(checked,{token,fetchImpl:async(url,options)=>{
    calls.push({url,method:options.method,headers:options.headers});
    return {status:200,text:async()=>JSON.stringify([{body:ackMarker(checked)}])};
  }});
  assert.equal(result.status,'already-published');assert.equal(calls.length,1);assert.equal(calls[0].method,'GET');
  assert.ok(calls[0].url.endsWith('/issues/97/comments?per_page=100&page=1'));
});
test('publisher sends only fixed synthetic ack to exact issue',async()=>{
  const calls=[];
  const result=await publishSyntheticReceipt(checkReceipt(receipt,{orderId:id,digest}),{token,fetchImpl:async(url,options)=>{
    calls.push({url,method:options.method,body:options.body});
    return options.method==='GET'?{status:200,text:async()=>JSON.stringify([])}:{status:201,text:async()=>JSON.stringify({id:1})};
  }});
  assert.equal(result.status,'published-synthetic-ack');
  assert.equal(calls.length,2);assert.ok(calls.every(x=>x.url.startsWith('https://api.github.com/repos/'+REPO+'/issues/97/comments')));
  const payload=JSON.parse(calls[1].body);assert.ok(payload.body.includes(ackMarker(receipt)));
  assert.ok(!JSON.stringify(payload).includes(token));
});
test('conflicting remote digest blocks publication',async()=>{
  let posts=0;
  await assert.rejects(publishSyntheticReceipt(checkReceipt(receipt,{orderId:id,digest}),{token,fetchImpl:async(_url,options)=>{
    if(options.method==='POST')posts++;
    return {status:200,text:async()=>JSON.stringify([{body:'<!-- hic-dev-ack-v1 order='+id+' digest='+'b'.repeat(64)+' -->'}])};
  }}),/ack-remote-conflict/);
  assert.equal(posts,0);
});
test('remote pagination uncertainty and provider failures cannot report success',async()=>{
  const checked=checkReceipt(receipt,{orderId:id,digest});
  const fetchImpl=async()=>({status:200,text:async()=>JSON.stringify(Array.from({length:100},()=>({body:'other'})))});
  await assert.rejects(publishSyntheticReceipt(checked,{token,fetchImpl}),/ack-pagination-unverified/);
  await assert.rejects(publishSyntheticReceipt(checked,{token,fetchImpl:async()=>({status:403,text:async()=>''})}),/ack-read-failed/);
  await assert.rejects(publishSyntheticReceipt(checked,{token,fetchImpl:async(_u,o)=>o.method==='GET'?{status:200,text:async()=>JSON.stringify([])}:{status:429,text:async()=>''}}),/ack-publish-not-confirmed/);
});
test('one synthetic GitHub ping flows to local receipt and remote mock ack',()=>temp(async d=>{
  const ping={version:'hic.dev-relay-ping/1',kind:'SyntheticPing',orderId:id,createdAt:'2026-11-10T14:59:00Z',expiresAt:'2026-12-01T00:00:00Z',source:{repository:REPO,issueNumber:93},note:'TRANSPORT_TEST_ONLY',executionAllowed:false};
  const bytes=Buffer.from(JSON.stringify(ping)+'\n'),hash=sha256(bytes);
  const index=Buffer.from(JSON.stringify({version:'hic.dev-inbox/1',kind:'ReviewOnlyInbox',orders:[{orderId:id,digest:hash}]}));
  const files=new Map([[INBOX_PATH,index],[contentPath(id),bytes]]);
  let count=0;
  const run=()=>relayAndPublish({stateDir:d,clock:()=>Date.UTC(2026,10,10,15,0,0),fetchFile:async p=>files.get(p),publish:async receipt=>{count++;assert.equal(receipt.executionAllowed,false);assert.equal(receipt.digest,hash);return {status:'published-synthetic-ack'};}});
  return run().then(async first=>{
    assert.equal(first[0].status,'published-synthetic-ack');
    assert.equal(JSON.parse(readFileSync(join(d,'acks',id+'.json'),'utf8')).status,'seen-untrusted');
    await run();assert.equal(count,2); // remote dedupe belongs to the authenticated publisher
  });
}));
