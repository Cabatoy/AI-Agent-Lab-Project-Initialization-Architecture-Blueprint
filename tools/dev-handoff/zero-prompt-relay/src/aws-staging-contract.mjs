// Offline-only staging contract. No AWS credentials, networking or execution.
import { createHash, timingSafeEqual } from 'node:crypto';
const UUID=/^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
const HEX=/^[0-9a-f]{64}$/;
const fail=reason=>{throw Error(reason)};
const exact=(x,fields)=>x!==null&&typeof x==='object'&&!Array.isArray(x)&&Object.keys(x).sort().join(',')===fields.slice().sort().join(',');
const digest=b=>createHash('sha256').update(b).digest('hex');
export function buildStagingProbe({probeId,receiptBytes}) {
  if(typeof probeId!=='string'||!UUID.test(probeId))fail('invalid-probe-id');
  if(!Buffer.isBuffer(receiptBytes)||receiptBytes.length<1||receiptBytes.length>2048)fail('invalid-receipt-bytes');
  let receipt;try{receipt=JSON.parse(receiptBytes.toString('utf8'))}catch{fail('invalid-receipt-json')}
  if(!exact(receipt,['version','orderId','digest','type','observedAt','status','executionAllowed'])||
    receipt.version!=='hic.dev-relay-ack/1'||!UUID.test(receipt.orderId)||!HEX.test(receipt.digest)||
    !['synthetic-ping','review-only-proposal'].includes(receipt.type)||
    receipt.status!=='seen-untrusted'||receipt.executionAllowed!==false||
    typeof receipt.observedAt!=='string'||!Number.isFinite(Date.parse(receipt.observedAt)))fail('untrusted-receipt');
  // A proposal receipt does not authorize cloud transport; only synthetic pings are eligible.
  if(receipt.type!=='synthetic-ping')fail('proposal-transport-disabled');
  return Object.freeze({version:'hic.staging-probe/1',kind:'SyntheticObservationRequest',
    probeId,orderId:receipt.orderId,receiptDigest:digest(receiptBytes),
    executionAllowed:false,requestedAction:'observe-only'});
}
export function verifyStagingResponse(response,request) {
  if(!exact(request,['version','kind','probeId','orderId','receiptDigest','executionAllowed','requestedAction'])||
    request.version!=='hic.staging-probe/1'||request.kind!=='SyntheticObservationRequest'||
    !UUID.test(request.probeId)||!UUID.test(request.orderId)||!HEX.test(request.receiptDigest)||
    request.executionAllowed!==false||request.requestedAction!=='observe-only')fail('invalid-probe-request');
  if(!exact(response,['version','kind','probeId','orderId','receiptDigest','status','agentExecutionAllowed','runtimeIsolationVerified'])||
    response.version!=='hic.staging-observation/1'||response.kind!=='SyntheticObservation'||
    response.status!=='observed'||response.agentExecutionAllowed!==false||
    response.runtimeIsolationVerified!==false)fail('invalid-staging-response');
  for(const key of ['probeId','orderId','receiptDigest']){
    if(typeof response[key]!=='string'||response[key].length!==request[key].length||
      !timingSafeEqual(Buffer.from(response[key]),Buffer.from(request[key])))fail('staging-correlation-mismatch');
  }
  return Object.freeze({status:'observed-untrusted',probeId:request.probeId,executionAllowed:false});
}
