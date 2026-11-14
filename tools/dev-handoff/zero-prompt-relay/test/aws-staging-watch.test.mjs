import test from 'node:test';
import assert from 'node:assert/strict';
import {mkdtempSync,mkdirSync,writeFileSync,readFileSync,rmSync,existsSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import {bridgeOnce} from '../src/aws-staging-watch.mjs';
const id='8c75b55a-fb83-4f9e-a99a-cccf079d2a1b';
const other='b017ec2a-5ab7-486f-a239-1e1d5f488f12';
function setup(type='synthetic-ping'){
 const dir=mkdtempSync(join(tmpdir(),'hic-bridge-'));mkdirSync(join(dir,'acks'));
 writeFileSync(join(dir,'acks',id+'.json'),JSON.stringify({version:'hic.dev-relay-ack/1',orderId:id,type,status:'seen-untrusted',executionAllowed:false}));
 return dir;
}
const poll=async()=>[{result:'already-seen',orderId:id}];
const observe=async()=>({status:'observed-untrusted',executionAllowed:false});
test('observes synthetic receipt exactly once across cycles',async()=>{
 const dir=setup();let calls=0;
 try{
  const one=await bridgeOnce({stateDir:dir,poll,observe:async()=>{calls++;return observe()}});
  const two=await bridgeOnce({stateDir:dir,poll,observe:async()=>{calls++;return observe()}});
  assert.equal(one.sent,1);assert.equal(two.skipped,1);assert.equal(calls,1);
  assert.equal(JSON.parse(readFileSync(join(dir,'staging-observed',id+'.json'))).executionAllowed,false);
 }finally{rmSync(dir,{recursive:true,force:true})}
});
test('does not send review-only proposal',async()=>{
 const dir=setup('review-only-proposal');
 try{const result=await bridgeOnce({stateDir:dir,poll,observe:()=>{throw Error('must-not-send')}});assert.equal(result.skipped,1);assert.equal(result.sent,0)}
 finally{rmSync(dir,{recursive:true,force:true})}
});
test('failed transport is retried next cycle without success marker',async()=>{
 const dir=setup();let calls=0;
 try{
  const first=await bridgeOnce({stateDir:dir,poll,observe:async()=>{calls++;throw Error('offline')}});
  assert.equal(first.failed,1);assert.equal(existsSync(join(dir,'staging-observed',id+'.json')),false);
  const second=await bridgeOnce({stateDir:dir,poll,observe:async()=>{calls++;return observe()}});
  assert.equal(second.sent,1);assert.equal(calls,2);
 }finally{rmSync(dir,{recursive:true,force:true})}
});
test('rejects corrupted acknowledgment and does not send',async()=>{
 const dir=setup();writeFileSync(join(dir,'acks',id+'.json'),JSON.stringify({version:'hic.dev-relay-ack/1',orderId:other,type:'synthetic-ping',status:'seen-untrusted',executionAllowed:false}));
 try{await assert.rejects(bridgeOnce({stateDir:dir,poll,observe:()=>{throw Error('must-not-send')}}),/untrusted-receipt/)}
 finally{rmSync(dir,{recursive:true,force:true})}
});
test('rejects tampered success marker',async()=>{
 const dir=setup();mkdirSync(join(dir,'staging-observed'));writeFileSync(join(dir,'staging-observed',id+'.json'),JSON.stringify({orderId:id,status:'executed',executionAllowed:true}));
 try{await assert.rejects(bridgeOnce({stateDir:dir,poll,observe:()=>{throw Error('must-not-send')}}),/invalid-bridge-marker/)}
 finally{rmSync(dir,{recursive:true,force:true})}
});
