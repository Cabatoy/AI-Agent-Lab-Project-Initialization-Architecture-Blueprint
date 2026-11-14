import test from 'node:test';
import assert from 'node:assert/strict';
import {mkdtempSync,mkdirSync,writeFileSync,rmSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import {readStagingObservations} from '../src/aws-staging-ui-status.mjs';
const id='f86ff6cd-61cc-4252-bfe6-4831e2c9ddd7';
function fixture(){const dir=mkdtempSync(join(tmpdir(),'hic-ui-status-'));mkdirSync(join(dir,'staging-observed'));return dir}
test('missing status folder is safe and empty',()=>{const dir=mkdtempSync(join(tmpdir(),'hic-empty-'));try{assert.deepEqual(readStagingObservations({stateDir:dir}).observations,[])}finally{rmSync(dir,{recursive:true,force:true})}});
test('valid observation is projected without granting execution',()=>{
 const dir=fixture();try{
 writeFileSync(join(dir,'staging-observed',id+'.json'),JSON.stringify({orderId:id,status:'observed-untrusted',executionAllowed:false,observedAt:'2026-11-15T00:00:00.000Z'}));
 const status=readStagingObservations({stateDir:dir});assert.equal(status.executionAllowed,false);assert.equal(status.observations.length,1);assert.equal(status.observations[0].orderId,id);assert.equal(status.trust,'untrusted-local-observation');
 }finally{rmSync(dir,{recursive:true,force:true})}
});
test('rejects forged execution marker',()=>{
 const dir=fixture();try{writeFileSync(join(dir,'staging-observed',id+'.json'),JSON.stringify({orderId:id,status:'observed-untrusted',executionAllowed:true,observedAt:'2026-11-15T00:00:00.000Z'}));assert.throws(()=>readStagingObservations({stateDir:dir}),/invalid-status-record/)}finally{rmSync(dir,{recursive:true,force:true})}
});
test('rejects extra fields',()=>{
 const dir=fixture();try{writeFileSync(join(dir,'staging-observed',id+'.json'),JSON.stringify({orderId:id,status:'observed-untrusted',executionAllowed:false,observedAt:'2026-11-15T00:00:00.000Z',approved:true}));assert.throws(()=>readStagingObservations({stateDir:dir}),/invalid-status-record/)}finally{rmSync(dir,{recursive:true,force:true})}
});
