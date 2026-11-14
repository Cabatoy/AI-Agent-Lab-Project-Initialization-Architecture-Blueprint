// Opt-in, observation-only bridge. No agent execution or GitHub writes.
import {existsSync,readFileSync,mkdirSync,openSync,closeSync,writeFileSync,lstatSync} from 'node:fs';
import {join} from 'node:path';
import {homedir} from 'node:os';
import {pollOnce} from './relay.mjs';
import {observeLocalReceipt} from './aws-staging-cli.mjs';
const UUID=/^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
const defaultDir=()=>join(homedir(),'.hic-dev-relay');
function safeDirectory(dir){mkdirSync(dir,{recursive:true,mode:0o700});const st=lstatSync(dir);if(!st.isDirectory()||st.isSymbolicLink())throw Error('unsafe-bridge-dir')}
function ackType(dir,id){
  const path=join(dir,'acks',id+'.json'),st=lstatSync(path);
  if(!st.isFile()||st.isSymbolicLink()||st.size>2048)throw Error('unsafe-existing-ack');
  const r=JSON.parse(readFileSync(path,'utf8'));
  if(r.orderId!==id||r.version!=='hic.dev-relay-ack/1'||r.status!=='seen-untrusted'||r.executionAllowed!==false)throw Error('untrusted-receipt');
  return r.type;
}
export async function bridgeOnce({stateDir=defaultDir(),poll=pollOnce,observe=observeLocalReceipt}={}){
  const receipts=await poll({stateDir});
  const doneDir=join(stateDir,'staging-observed');safeDirectory(doneDir);
  let sent=0,skipped=0,failed=0;
  for(const r of receipts){
    if(!UUID.test(r.orderId))throw Error('invalid-order-id');
    if(ackType(stateDir,r.orderId)!=='synthetic-ping'){skipped++;continue}
    const marker=join(doneDir,r.orderId+'.json');
    if(existsSync(marker)){
      const st=lstatSync(marker);if(!st.isFile()||st.isSymbolicLink()||st.size>1024)throw Error('unsafe-bridge-marker');
      const saved=JSON.parse(readFileSync(marker,'utf8'));
      if(saved.orderId!==r.orderId||saved.status!=='observed-untrusted'||saved.executionAllowed!==false)throw Error('invalid-bridge-marker');
      skipped++;continue;
    }
    try{
      const response=await observe({orderId:r.orderId,stateDir});
      if(response.status!=='observed-untrusted'||response.executionAllowed!==false)throw Error('untrusted-observation');
      let fd;
      try{fd=openSync(marker,'wx',0o600);writeFileSync(fd,JSON.stringify({orderId:r.orderId,status:'observed-untrusted',executionAllowed:false,observedAt:new Date().toISOString()})+'\n')}
      finally{if(fd!==undefined)closeSync(fd)}
      sent++;
    }catch(error){failed++;console.error(JSON.stringify({status:'staging-retry-pending',orderId:r.orderId,reason:error?.message||'unknown',executionAllowed:false}))}
  }
  return {status:'bridge-observe-only',observed:receipts.length,sent,skipped,failed,executionAllowed:false};
}
export async function main(argv=process.argv.slice(2)){
  const once=argv.length===2&&argv[0]==='--enable-staging-observe'&&argv[1]==='--once';
  const watch=argv.length===2&&argv[0]==='--enable-staging-observe'&&argv[1]==='--watch';
  if(!once&&!watch){console.error('Usage: node src/aws-staging-watch.mjs --enable-staging-observe (--once | --watch)');process.exitCode=2;return}
  do{
    try{const result=await bridgeOnce();console.log(JSON.stringify(result));if(once&&result.failed)process.exitCode=1}
    catch(error){console.error(JSON.stringify({status:'blocked',reason:error?.message||'bridge-failed',executionAllowed:false}));if(once)process.exitCode=1}
    if(watch)await new Promise(resolve=>setTimeout(resolve,30000));
  }while(watch);
}
if(process.argv[1]&&import.meta.url===(await import('node:url')).pathToFileURL((await import('node:path')).resolve(process.argv[1])).href)await main();
