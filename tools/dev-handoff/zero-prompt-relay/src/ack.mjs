import { readFileSync, lstatSync } from 'node:fs';
import { homedir } from 'node:os';
import { join } from 'node:path';
import { INBOX_PATH, REPO, checkInbox, githubContent, pollOnce } from './relay.mjs';

const ACK_ISSUE=97;
const EXPECTED_COMMENT_AUTHOR='Cabatoy';
const API='https://api.github.com/repos/'+REPO+'/issues/'+ACK_ISSUE+'/comments';
const isRecord=v=>v!==null&&typeof v==='object'&&!Array.isArray(v);
const UUID=/^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
const DIGEST=/^[0-9a-f]{64}$/;
const fail=reason=>{throw new Error(reason);};
export const stateRoot=()=>join(homedir(),'.hic-dev-relay');
export function ackMarker(receipt) {
  return '<!-- hic-dev-ack-v1 order='+receipt.orderId+' digest='+receipt.digest+' -->';
}
export function checkReceipt(x,requested) {
  if(!isRecord(x)||Object.keys(x).sort().join()!==['version','orderId','digest','type','observedAt','status','executionAllowed'].sort().join() ||
    x.version!=='hic.dev-relay-ack/1'||x.orderId!==requested.orderId||x.digest!==requested.digest||
    x.type!=='synthetic-ping'||x.status!=='seen-untrusted'||x.executionAllowed!==false ||
    typeof x.observedAt!=='string'||!Number.isFinite(Date.parse(x.observedAt))) fail('invalid-ack');
  if(!UUID.test(x.orderId)||!DIGEST.test(x.digest)) fail('invalid-ack');
  return {orderId:x.orderId,digest:x.digest,type:'synthetic-ping',status:'seen-untrusted',executionAllowed:false};
}
export function readReceipt(order,root=stateRoot()) {
  const directory=join(root,'acks');
  const d=lstatSync(directory);
  if(!d.isDirectory()||d.isSymbolicLink())fail('unsafe-ack-directory');
  const file=join(directory,order.orderId+'.json');
  const st=lstatSync(file);
  if(!st.isFile()||st.isSymbolicLink()||st.size>2048)fail('unsafe-ack-file');
  return checkReceipt(JSON.parse(readFileSync(file,'utf8')),order);
}
const headers=(token)=>({
  'Accept':'application/vnd.github+json',
  'User-Agent':'HIC-dev-ack/0.1',
  ...(token?{'Authorization':'Bearer '+token}:{})
});
const readBody=async response=>{
  if(response.status!==200)fail('ack-read-failed');
  const txt=await response.text();
  if(Buffer.byteLength(txt)>200000)fail('ack-response-too-large');
  let data;try{data=JSON.parse(txt)}catch{fail('ack-response-invalid')}
  if(!Array.isArray(data)||data.length>100)fail('ack-response-invalid');
  return data;
};
export async function publishSyntheticReceipt(receipt,{token,fetchImpl=fetch}={}) {
  if(!isRecord(receipt)||!UUID.test(receipt.orderId)||!DIGEST.test(receipt.digest)||receipt.type!=='synthetic-ping'||receipt.status!=='seen-untrusted'||receipt.executionAllowed!==false)fail('invalid-ack');
  if(typeof token!=='string'||!/^github_pat_[A-Za-z0-9_]+$/.test(token)||token.length>300)fail('missing-scoped-token');
  // GitHub issue comments are a transport-only at-least-once channel, not a durable HIC ledger.
  const existing=await fetchImpl(API+'?per_page=100&page=1',{method:'GET',headers:headers(token),redirect:'manual'});
  const comments=(await readBody(existing)).filter(c=>c?.user?.login===EXPECTED_COMMENT_AUTHOR);
  const marker=ackMarker(receipt);
  const prefix='<!-- hic-dev-ack-v1 order='+receipt.orderId+' digest=';
  if(comments.some(c=>typeof c?.body==='string'&&c.body.includes(prefix)&&!c.body.includes(marker)))fail('ack-remote-conflict');
  if(comments.some(c=>typeof c?.body==='string'&&c.body.includes(marker)))return {status:'already-published',orderId:receipt.orderId};
  if(comments.length===100)fail('ack-pagination-unverified');
  const body=marker+'\nHIC developer relay: synthetic transport receipt; **seen-untrusted**; executionAllowed=false. No agent or task executed.';
  const response=await fetchImpl(API,{method:'POST',headers:{...headers(token),'Content-Type':'application/json'},body:JSON.stringify({body}),redirect:'manual'});
  if(response.status!==201)fail('ack-publish-not-confirmed');
  let result;
  try { result=await response.json(); } catch { fail('ack-publish-response-invalid'); }
  if(!Number.isSafeInteger(result?.id) || result.id<1 || result?.user?.login!==EXPECTED_COMMENT_AUTHOR || typeof result?.body!=='string' || !result.body.includes(marker)) fail('ack-publish-author-unverified');
  // Do not interpret publication as code completion, authorization, or a HIC ledger transition.
  return {status:'published-synthetic-ack',orderId:receipt.orderId};
}
export async function relayAndPublish({fetchFile=githubContent,publish=publishSyntheticReceipt,token,stateDir=stateRoot(),clock=Date.now}={}) {
  // pollOnce is the only order parser; no issue body or README is an instruction.
  const receipts=await pollOnce({fetchFile,stateDir,clock});
  const bytes=await fetchFile(INBOX_PATH);
  if(!Buffer.isBuffer(bytes)||bytes.length>16384)fail('invalid-inbox-bytes');
  let index;try{index=JSON.parse(bytes.toString('utf8'))}catch{fail('invalid-inbox-json')}
  const entries=checkInbox(index);
  if(entries.length!==receipts.length)fail('inbox-raced');
  const results=[];
  for(const entry of entries){
    // Only read local receipts for the exact immutable GitHub entry digest.
    const receipt=readReceipt(entry,stateDir);
    results.push(await publish(receipt,{token}));
  }
  return results;
}
