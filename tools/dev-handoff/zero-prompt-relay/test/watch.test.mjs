import { strict as assert } from 'node:assert';
import { checkWatchConfig, watchPoll, WATCH_VERSION, WATCH_DB_VERSION } from '../src/watch.mjs';

const config={version:WATCH_VERSION,repository:'Cabatoy/test-repo',trustedAuthor:'Cabatoy',relayLabel:'hic-relay-ready'};

const mockIssues=[
  {number:101,title:'Valid issue',body:'Test body',updated_at:'2026-11-13T01:00:00Z',state:'open',user:{login:'Cabatoy'}},
  {number:102,title:'PR should be ignored',body:'',updated_at:'2026-11-13T01:00:00Z',state:'open',user:{login:'Cabatoy'},pull_request:{url:'...'}},
  {number:103,title:'Untrusted author',body:'',updated_at:'2026-11-13T01:00:00Z',state:'open',user:{login:'unknown'}},
];

function mockFetch() {
  return async (url,opts)=>{
    return {status:200,json:async()=>mockIssues,text:async()=>JSON.stringify(mockIssues)};
  };
}

function mockFetchError() {
  return async ()=>{throw new Error('network-error');};
}

export async function testWatchPoll() {
  console.log('Test: watchPoll filters correctly');
  const result=await watchPoll({config,fetchImpl:mockFetch()});
  assert.equal(result.pending.length,1,'Only one valid issue');
  assert.equal(result.pending[0].number,101,'Issue 101 is pending');
  console.log('✓ watchPoll filters correctly');
}

export async function testConfigValidation() {
  console.log('Test: config validation');
  assert.throws(()=>checkWatchConfig({version:WATCH_VERSION,repository:'invalid',trustedAuthor:'Cabatoy',relayLabel:'label'}),'rejects invalid repo');
  assert.throws(()=>checkWatchConfig({version:WATCH_VERSION,repository:'Cabatoy/repo',trustedAuthor:'inv@lid',relayLabel:'label'}),'rejects invalid author');
  console.log('✓ config validation works');
}

export async function testFetchError() {
  console.log('Test: fetch error handling');
  try {
    await watchPoll({config,fetchImpl:mockFetchError()});
    assert.fail('should throw');
  } catch(e) {
    assert(e.message.includes('watch-fetch-failed')||e.message.includes('network-error'));
  }
  console.log('✓ fetch error handled');
}

async function runAllTests() {
  try {
    await testConfigValidation();
    await testWatchPoll();
    await testFetchError();
    console.log('\n✅ All watch tests passed!');
    process.exitCode=0;
  } catch(e) {
    console.error('\n❌ Test failed:', e.message);
    process.exitCode=1;
  }
}

runAllTests();
