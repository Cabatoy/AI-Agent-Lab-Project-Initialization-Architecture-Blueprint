// Explicit operator-only adapter for an SSH-tunneled, loopback staging endpoint.
// Does not run as a watcher, read GitHub, execute tasks or access AWS credentials.
import {readFileSync} from 'node:fs';
import {homedir} from 'node:os';
import {join} from 'node:path';
import {randomUUID} from 'node:crypto';
import {buildStagingProbe,verifyStagingResponse} from './aws-staging-contract.mjs';
const UUID=/^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
export const LOOPBACK_URL='http://127.0.0.1:18765/observe';
export async function observeLocalReceipt({orderId,stateDir=join(homedir(),'.hic-dev-relay'),fetchImpl=fetch,probeId=randomUUID(),timeoutMs=10000}){
  if(!UUID.test(orderId))throw Error('invalid-order-id');
  if(!Number.isInteger(timeoutMs)||timeoutMs<1||timeoutMs>10000)throw Error('invalid-timeout');
  const receiptBytes=readFileSync(join(stateDir,'acks',orderId+'.json'));
  const request=buildStagingProbe({probeId,receiptBytes});
  if(request.orderId!==orderId)throw Error('receipt-order-mismatch');
  const controller=new AbortController();
  const timeout=setTimeout(()=>controller.abort(),timeoutMs);
  try{
    const response=await fetchImpl(LOOPBACK_URL,{method:'POST',redirect:'manual',
      headers:{'Content-Type':'application/json','Accept':'application/json'},
      body:JSON.stringify(request),signal:controller.signal});
    if(response.status!==200)throw Error('staging-http-failed');
    const body=await response.text();
    if(Buffer.byteLength(body)>2048)throw Error('staging-response-too-large');
    let data;try{data=JSON.parse(body)}catch{throw Error('invalid-staging-json')}
    return verifyStagingResponse(data,request);
  }finally{clearTimeout(timeout)}
}
export async function main(argv=process.argv.slice(2)){
  if(argv.length!==2||argv[0]!=='--observe-synthetic'||!UUID.test(argv[1])){
    console.error('Usage: node src/aws-staging-cli.mjs --observe-synthetic <order-uuid>');
    process.exitCode=2;return;
  }
  try{
    const result=await observeLocalReceipt({orderId:argv[1]});
    console.log(JSON.stringify(result));
  }catch(e){
    console.error(JSON.stringify({status:'blocked',reason:e?.message||'staging-failed',executionAllowed:false}));
    process.exitCode=1;
  }
}
if(process.argv[1] && import.meta.url===new URL('file://'+process.argv[1].replace(/\\/g,'/')).href)await main();
