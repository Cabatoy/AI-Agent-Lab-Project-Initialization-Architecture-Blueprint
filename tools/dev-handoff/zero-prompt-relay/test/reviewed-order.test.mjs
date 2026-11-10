import test from 'node:test';
import assert from 'node:assert/strict';
import { generateKeyPairSync, sign, randomUUID } from 'node:crypto';
import { REPO } from '../src/relay.mjs';
import { canonicalJson, verifyReviewedEnvelope, SIGNING_CONTEXT_FOR_TESTS } from '../src/reviewed-order.mjs';
import { inspectExistingTask } from '../src/ledger-review.mjs';

const NOW = Date.UTC(2026,10,10,18,0,0);
const sha='a'.repeat(40);
const {publicKey,privateKey}=generateKeyPairSync('ed25519');
const {publicKey:otherPublic,privateKey:otherPrivate}=generateKeyPairSync('ed25519');
const keyId='hic-reviewer-local-test';
const keys=new Map([[keyId,{publicKeyPem:publicKey.export({type:'spki',format:'pem'}),reviewerId:'local-reviewer',authorized:true,revoked:false}]]);
const fresh=()=>({
  version:'hic.dev-reviewed-work-order/1',kind:'ReviewedDevWorkOrder',orderId:randomUUID(),
  createdAt:'2026-11-10T17:50:00Z',expiresAt:'2026-11-10T18:50:00Z',
  source:{repository:REPO,transportRef:'hic-dev-orders',queueIssue:75,issueNumber:100,reviewedGitSha:sha},
  task:{taskId:randomUUID(),repository:REPO,branch:'feature/signed-review-test',baseSha:'b'.repeat(40),allowedPaths:['tools/dev-handoff/zero-prompt-relay/README.md'],objective:'Inspect test changes only',requiresHumanApproval:true},
  review:{reviewId:randomUUID(),reviewerId:'local-reviewer',reviewedAt:'2026-11-10T17:55:00Z',revisionDigest:'c'.repeat(64),status:'reviewed'},
  limits:{executionAllowed:false,allowedTools:[],maxModelCostUsd:0,maxExecutionSeconds:0,allowNetwork:false,allowGitWrite:false}
});
function envelope(payload, {signingKey=privateKey,customBytes,signingId=keyId,signatureOverride}={}){
  const bytes=customBytes ?? Buffer.from(canonicalJson(payload));
  const signature=signatureOverride??sign(null,Buffer.concat([Buffer.from(SIGNING_CONTEXT_FOR_TESTS+signingId+'\n'),bytes]),signingKey);
  return Buffer.from(JSON.stringify({version:'hic.dev-review-envelope/1',kind:'SignedReviewedDevWorkOrder',algorithm:'Ed25519',keyId:signingId,payload:bytes.toString('base64url'),signature:signature.toString('base64url')}));
}
const check=(bytes,options={})=>verifyReviewedEnvelope(bytes,{trustedKeys:keys,now:NOW,expectedGitSha:sha,...options});

test('valid independent Ed25519 reviewer produces a NON-executable review',()=>{
 const p=fresh(),v=check(envelope(p));
 assert.equal(v.status,'accepted-for-review');assert.equal(v.executionAllowed,false);
 assert.equal(v.ledgerVerified,false);assert.equal(v.orderId,p.orderId);
 assert.deepEqual(v.allowedPaths,p.task.allowedPaths);assert.ok(Object.isFrozen(v));
});
test('missing, revoked, mismatched, or forged keys fail closed',()=>{
 const e=envelope(fresh());
 for(const k of [undefined,new Map(),new Map([[keyId,{...keys.get(keyId),revoked:true}]]),new Map([[keyId,{...keys.get(keyId),reviewerId:'other-reviewer'}]])]) {
   assert.throws(()=>check(e,{trustedKeys:k}),/untrusted-reviewer-key|reviewer-mismatch/);
 }
 assert.throws(()=>check(envelope(fresh(),{signingKey:otherPrivate})),/invalid-signature/);
 assert.throws(()=>check(e,{trustedKeys:new Map([[keyId,{...keys.get(keyId),publicKeyPem:otherPublic.export({type:'spki',format:'pem'})}]])}),/invalid-signature/);
});
test('raw payload tampering and substitution are rejected',()=>{
 const p=fresh(),bytes=envelope(p),x=JSON.parse(bytes);
 x.payload=Buffer.from(canonicalJson({...p,task:{...p.task,objective:'TAMPER'}})).toString('base64url');
 assert.throws(()=>check(Buffer.from(JSON.stringify(x))),/invalid-signature/);
 assert.throws(()=>check(envelope({...p,task:{...p.task,allowedPaths:['../secrets']}})),/invalid-paths/);
 assert.throws(()=>check(envelope({...p,limits:{...p.limits,executionAllowed:true}})),/execution-denied-v1/);
 assert.throws(()=>check(envelope({...p,task:{...p.task,allowedPaths:['tools/dev-handoff/zero-prompt-relay/README.md','tools/dev-handoff/zero-prompt-relay/README.md']}})),/invalid-paths/);
});
test('non-canonical payload, unknown fields and duplicate JSON keys blocked',()=>{
 const p=fresh(),plain=Buffer.from(JSON.stringify(p));
 assert.throws(()=>check(envelope(p,{customBytes:plain})),/noncanonical-payload/);
 assert.throws(()=>check(envelope({...p,evil:'shell'})),/invalid-payload/);
 const canonical=canonicalJson(p).replace('"kind":"ReviewedDevWorkOrder",','"kind":"ReviewedDevWorkOrder","kind":"ReviewedDevWorkOrder",');
 assert.throws(()=>check(envelope(p,{customBytes:Buffer.from(canonical)})),/noncanonical-payload/);
});
test('GitHub SHA, audience, expiration and human review bindings are enforced',()=>{
 const p=fresh(),e=envelope(p);
 assert.throws(()=>check(e,{expectedGitSha:'d'.repeat(40)}),/github-sha-not-verified/);
 assert.throws(()=>check(e,{expectedGitSha:undefined}),/github-sha-not-verified/);
 assert.throws(()=>check(envelope({...p,source:{...p.source,repository:'attacker/other'}})),/invalid-source/);
 assert.throws(()=>check(envelope({...p,review:{...p.review,reviewerId:'someoneelse'}})),/reviewer-mismatch/);
 assert.throws(()=>check(envelope({...p,expiresAt:'2026-11-10T17:59:00Z'})),/invalid-lifetime/);
 assert.throws(()=>check(e,{now:NOW+2*3600000}),/invalid-lifetime/);
 assert.throws(()=>check(envelope({...p,source:{...p.source,transportRef:'main'}})),/invalid-source/);
});
test('malformed envelopes, bad signature encoding, unsupported algorithm fail',()=>{
 const p=fresh(),v=JSON.parse(envelope(p));
 assert.throws(()=>check(Buffer.from('not json')),/invalid-envelope-json/);
 assert.throws(()=>check(envelope(p),{trustedKeys:keys,expectedGitSha:'invalid'}),/github-sha-not-verified/);
 assert.throws(()=>check(Buffer.alloc(8200)),/invalid-envelope-bytes/);
 assert.throws(()=>check(Buffer.from(JSON.stringify({...v,algorithm:'RSA'}))),/invalid-envelope/);
 assert.throws(()=>check(Buffer.from(JSON.stringify({...v,signature:'not+url'}))),/invalid-base64url/);
 assert.throws(()=>check(Buffer.from(JSON.stringify({...v,secret:'bad'}))),/invalid-envelope/);
});
test('read-only ledger adapter never claims, approves, queues or executes',async()=>{
 const v=check(envelope(fresh())); let reads=0;
 assert.deepEqual(await inspectExistingTask(v),{status:'blocked',reason:'ledger-unavailable',executionAllowed:false});
 const missing=await inspectExistingTask(v,{readTask:async id=>{reads++;assert.equal(id,v.taskId);return null;}});
 assert.equal(missing.reason,'task-not-found-or-mismatch');
 const legacy=await inspectExistingTask(v,{readTask:async()=>({id:v.taskId,status:'Draft'})});
 assert.equal(legacy.reason,'authoritative-task-revision-unavailable');
 const observed=await inspectExistingTask(v,{readTask:async()=>({id:v.taskId,status:'Draft',nativeRunRevision:1})});
 assert.equal(observed.status,'observed-draft-unverified');assert.equal(observed.executionAllowed,false);
 assert.equal((await inspectExistingTask(v,{readTask:async()=>({id:v.taskId,status:'Approved',nativeRunRevision:1})})).reason,'task-not-draft');
 assert.equal((await inspectExistingTask(v,{readTask:async()=>{throw Error('SECRET')}})).reason,'ledger-read-failed');
 assert.equal(reads,1);
});
