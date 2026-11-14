import test from 'node:test';
import assert from 'node:assert/strict';
import {mkdtempSync,mkdirSync,writeFileSync,rmSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import {observeLocalReceipt,LOOPBACK_URL} from '../src/aws-staging-cli.mjs';
const orderId='8c75b55a-fb83-4f9e-a99a-cccf079d2a1b';
const probeId='eecc2a31-7f72-41f3-bcf8-c74d91580d93';
const receipt={version:'hic.dev-relay-ack/1',orderId,digest:'a'.repeat(64),type:'synthetic-ping',observedAt:'2026-11-15T00:00:00Z',status:'seen-untrusted',executionAllowed:false};
function setup(x=receipt){const dir=mkdtempSync(join(tmpdir(),'hic-staging-'));mkdirSync(join(dir,'acks'));writeFileSync(join(dir,'acks',orderId+'.json'),JSON.stringify(x)+'\n');return dir}
const reply=req=>({version:'hic.staging-observation/1',kind:'SyntheticObservation',probeId:req.probeId,orderId:req.orderId,receiptDigest:req.receiptDigest,status:'observed',agentExecutionAllowed:false,runtimeIsolationVerified:false});
const ok=data=>({status:200,text:async()=>JSON.stringify(data)});
test('uses only pinned loopback endpoint and validates response',async()=>{const dir=setup();try{const result=await observeLocalReceipt({orderId,probeId,stateDir:dir,fetchImpl:async(url,opts)=>{assert.equal(url,LOOPBACK_URL);assert.equal(opts.method,'POST');assert.equal(opts.redirect,'manual');const req=JSON.parse(opts.body);assert.equal(req.executionAllowed,false);return ok(reply(req))}});assert.equal(result.status,'observed-untrusted')}finally{rmSync(dir,{recursive:true,force:true})}});
test('rejects mismatched order in receipt before fetch',async()=>{const dir=setup({...receipt,orderId:'b017ec2a-5ab7-486f-a239-1e1d5f488f12'});try{await assert.rejects(observeLocalReceipt({orderId,probeId,stateDir:dir,fetchImpl:()=>{throw Error('network-must-not-run')}}),/receipt-order-mismatch/)}finally{rmSync(dir,{recursive:true,force:true})}});
test('rejects forged executable response',async()=>{const dir=setup();try{await assert.rejects(observeLocalReceipt({orderId,probeId,stateDir:dir,fetchImpl:async(_u,o)=>ok({...reply(JSON.parse(o.body)),agentExecutionAllowed:true})}),/invalid-staging-response/)}finally{rmSync(dir,{recursive:true,force:true})}});
test('rejects non-200 response',async()=>{const dir=setup();try{await assert.rejects(observeLocalReceipt({orderId,probeId,stateDir:dir,fetchImpl:async()=>({status:401})}),/staging-http-failed/)}finally{rmSync(dir,{recursive:true,force:true})}});
test('rejects malformed order id before disk access',async()=>{await assert.rejects(observeLocalReceipt({orderId:'../secrets'}),/invalid-order-id/)});
