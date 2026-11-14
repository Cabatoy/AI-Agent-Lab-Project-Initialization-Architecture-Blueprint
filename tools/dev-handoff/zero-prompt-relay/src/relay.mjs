import { createHash } from 'node:crypto';
import { existsSync, lstatSync, mkdirSync, openSync, closeSync, writeFileSync, readFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { join } from 'node:path';

export const REPO = 'Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint';
export const TRANSPORT_REF = 'hic-dev-orders';
export const INBOX_PATH = 'dev-work-orders/inbox.json';
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;
const HEX = /^[0-9a-f]{64}$/;
const SHA = /^[0-9a-f]{40}$/;
const fail = code => { throw Error(code); };
const isObject = x => !!x && typeof x === 'object' && !Array.isArray(x);
const keys = (x,k) => isObject(x) && Object.keys(x).sort().join() === k.slice().sort().join();
const dateMs = x => {
  if(typeof x !== 'string' || !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,3})?Z$/.test(x) || !Number.isFinite(Date.parse(x))) fail('invalid-date');
  return Date.parse(x);
};
export const sha256 = b => createHash('sha256').update(b).digest('hex');
export function contentPath(id) { if(!UUID.test(id)) fail('invalid-order-id'); return 'dev-work-orders/orders/' + id.toLowerCase() + '.json'; }
export function checkInbox(x) {
  if(!keys(x,['version','kind','orders']) || x.version !== 'hic.dev-inbox/1' || x.kind !== 'ReviewOnlyInbox' || !Array.isArray(x.orders) || x.orders.length > 20) fail('invalid-inbox');
  const seen = new Set();
  return x.orders.map(e => {
    if(!keys(e,['orderId','digest']) || !UUID.test(e.orderId) || !HEX.test(e.digest)) fail('invalid-index-entry');
    const id=e.orderId.toLowerCase(); if(seen.has(id)) fail('duplicate-index-entry'); seen.add(id);
    return {orderId:id,digest:e.digest};
  });
}
function lifetime(x,now) {
  const created=dateMs(x.createdAt), expires=dateMs(x.expiresAt);
  if(created > now + 60000 || expires <= created || expires-created > 30*86400000) fail('invalid-lifetime');
  if(expires <= now) fail('expired');
}
export function checkPing(x,now=Date.now()) {
  if(!keys(x,['version','kind','orderId','createdAt','expiresAt','source','note','executionAllowed']) ||
    x.version!=='hic.dev-relay-ping/1' || x.kind!=='SyntheticPing' || x.note!=='TRANSPORT_TEST_ONLY' || x.executionAllowed!==false) fail('not-review-only');
  if(!UUID.test(x.orderId)) fail('invalid-order-id');
  if(!keys(x.source,['repository','issueNumber']) || x.source.repository!==REPO || x.source.issueNumber!==93) fail('invalid-source');
  lifetime(x,now);
  return {orderId:x.orderId.toLowerCase(),type:'synthetic-ping'};
}
export function checkReviewOnlyProposal(x,now=Date.now()) {
  if(!keys(x,['version','kind','mode','orderId','createdAt','expiresAt','source','task','review']) ||
     x.version!=='hic.dev-work-order.proposal/1' || x.kind!=='DeveloperWorkOrderProposal' || x.mode!=='review-only') fail('invalid-proposal');
  if(!UUID.test(x.orderId)) fail('invalid-order-id'); lifetime(x,now);
  if(!keys(x.source,['repository','queueIssue','issueNumber','issueUpdatedAt']) || x.source.repository!==REPO || x.source.queueIssue!==75 || !Number.isSafeInteger(x.source.issueNumber) || x.source.issueNumber<1) fail('invalid-source');
  dateMs(x.source.issueUpdatedAt);
  const t=x.task;
  if(!keys(t,['taskId','repository','branch','baseSha','allowedPaths','objective','requiresHumanApproval']) ||
    !UUID.test(t.taskId) || t.repository!==REPO || typeof t.branch!=='string' ||
    t.branch.length>120 || !/^feature\/[a-z0-9-]+(?:\/[a-z0-9-]+)*$/.test(t.branch) ||
    !SHA.test(t.baseSha) || t.requiresHumanApproval!==true || typeof t.objective!=='string' ||
    t.objective.length===0 || t.objective.length>400) fail('invalid-task');
  if(!Array.isArray(t.allowedPaths) || t.allowedPaths.length<1 || t.allowedPaths.length>8 ||
    t.allowedPaths.some(p=>typeof p!=='string' || !/^tools\/dev-handoff\/(?:[a-z0-9-]+\/)*[a-z0-9.-]+\.(?:md|mjs|json)$/.test(p)) ||
    new Set(t.allowedPaths).size!==t.allowedPaths.length) fail('invalid-paths');
  if(!keys(x.review,['status','executionAllowed','signature']) || x.review.status!=='unsigned' || x.review.executionAllowed!==false || x.review.signature!==null) fail('approval-not-allowed');
  return {orderId:x.orderId.toLowerCase(),type:'review-only-proposal'};
}
function decodeFile(x,path,limit) {
  if(!isObject(x) || x.type!=='file' || x.path!==path || x.encoding!=='base64' || !SHA.test(x.sha) ||
    typeof x.content!=='string' || !Number.isSafeInteger(x.size) || x.size<0 || x.size>limit) fail('invalid-github-file');
  const e=x.content.replace(/\s/g,'');
  if(!/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/.test(e)) fail('invalid-base64');
  const b=Buffer.from(e,'base64');
  if(b.length!==x.size || b.length>limit) fail('size-mismatch');
  return b;
}
export async function githubContent(path,{fetchImpl=fetch,timeoutMs=7000}={}) {
  if(path!==INBOX_PATH && !/^dev-work-orders\/orders\/[0-9a-f-]{36}\.json$/.test(path)) fail('invalid-github-path');
  const controller=new AbortController(), timeout=setTimeout(()=>controller.abort(),timeoutMs);
  try {
    const url='https://api.github.com/repos/'+REPO+'/contents/'+path+'?ref='+TRANSPORT_REF;
    const response=await fetchImpl(new URL(url),{method:'GET',headers:{Accept:'application/vnd.github+json','User-Agent':'HIC-dev-relay/0.1'},redirect:'manual',signal:controller.signal});
    if(response.status!==200) fail(response.status===403 || response.status===429?'github-rate-limited-or-forbidden':'github-read-failed');
    const text=await response.text();
    if(Buffer.byteLength(text)>32768) fail('response-too-large');
    let json; try {json=JSON.parse(text)} catch {fail('invalid-github-response')}
    return decodeFile(json,path,path===INBOX_PATH?16384:4096);
  } finally {clearTimeout(timeout)}
}
const defaultDir=()=>join(homedir(),'.hic-dev-relay');
function ackDir(dir) {
  mkdirSync(dir,{recursive:true,mode:0o700});
  const st=lstatSync(dir);if(!st.isDirectory() || st.isSymbolicLink()) fail('unsafe-state-dir');
  const p=join(dir,'acks');mkdirSync(p,{recursive:true,mode:0o700});
  const a=lstatSync(p);if(!a.isDirectory() || a.isSymbolicLink()) fail('unsafe-ack-dir');
  return p;
}
export function acknowledge({orderId,digest,type},{stateDir=defaultDir(),clock=Date.now}={}) {
  if(!UUID.test(orderId) || !HEX.test(digest) || !['synthetic-ping','review-only-proposal'].includes(type)) fail('invalid-ack');
  const path=join(ackDir(stateDir),orderId+'.json');
  if(existsSync(path)) {
    const st=lstatSync(path);if(!st.isFile() || st.isSymbolicLink() || st.size>2048) fail('unsafe-existing-ack');
    let x;try{x=JSON.parse(readFileSync(path,'utf8'))}catch{fail('unreadable-ack')}
    if(x.version!=='hic.dev-relay-ack/1'||x.orderId!==orderId||x.digest!==digest||x.type!==type||x.executionAllowed!==false||x.status!=='seen-untrusted') fail('ack-replay-conflict');
    return {result:'already-seen',orderId};
  }
  const receipt={version:'hic.dev-relay-ack/1',orderId,digest,type,observedAt:new Date(clock()).toISOString(),status:'seen-untrusted',executionAllowed:false};
  let fd;try{fd=openSync(path,'wx',0o600);writeFileSync(fd,JSON.stringify(receipt)+'\n')}finally{if(fd!==undefined)closeSync(fd)}
  return {result:'seen-untrusted',orderId};
}
export async function pollOnce({fetchFile=githubContent,stateDir=defaultDir(),clock=Date.now}={}) {
  const indexBytes=await fetchFile(INBOX_PATH);
  if(!Buffer.isBuffer(indexBytes) || indexBytes.length>16384) fail('invalid-inbox-bytes');
  let index;try{index=JSON.parse(indexBytes.toString('utf8'))}catch{fail('invalid-inbox-json')}
  const orders=checkInbox(index), results=[];
  for(const entry of orders) {
    const existing=join(ackDir(stateDir),entry.orderId+'.json');
    if(existsSync(existing)) {
      const st=lstatSync(existing);if(!st.isFile()||st.isSymbolicLink()||st.size>2048)fail('unsafe-existing-ack');
      let receipt;try{receipt=JSON.parse(readFileSync(existing,'utf8'))}catch{fail('unreadable-ack')}
      if(receipt.version!=='hic.dev-relay-ack/1'||receipt.orderId!==entry.orderId||receipt.digest!==entry.digest||receipt.executionAllowed!==false||receipt.status!=='seen-untrusted'||!['synthetic-ping','review-only-proposal'].includes(receipt.type))fail('ack-replay-conflict');
      results.push({result:'already-seen',orderId:entry.orderId});continue;
    }
    const bytes=await fetchFile(contentPath(entry.orderId));
    if(!Buffer.isBuffer(bytes)||bytes.length>4096||sha256(bytes)!==entry.digest)fail('order-digest-mismatch');
    let x;try{x=JSON.parse(bytes.toString('utf8'))}catch{fail('invalid-order-json')}
    const checked=x?.kind==='SyntheticPing'?checkPing(x,clock()):checkReviewOnlyProposal(x,clock());
    if(checked.orderId!==entry.orderId)fail('order-id-mismatch');
    results.push(acknowledge({...checked,digest:entry.digest},{stateDir,clock}));
  }
  return results;
}
