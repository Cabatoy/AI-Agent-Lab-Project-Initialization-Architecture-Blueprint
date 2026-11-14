// Read-only, bounded status projection for future HIC UI integration.
// Local state is untrusted; no execution or approval can be derived from it.
import {readdirSync,readFileSync,lstatSync} from 'node:fs';
import {homedir} from 'node:os';
import {join} from 'node:path';
const UUID=/^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
export function readStagingObservations({stateDir=join(homedir(),'.hic-dev-relay'),limit=20}={}){
 if(!Number.isSafeInteger(limit)||limit<1||limit>100)throw Error('invalid-limit');
 const path=join(stateDir,'staging-observed');
 let dir;
 try{dir=lstatSync(path)}catch(e){if(e?.code==='ENOENT')return Object.freeze({version:'hic.staging-ui-status/1',trust:'untrusted-local-observation',executionAllowed:false,observations:[]});throw e}
 if(!dir.isDirectory()||dir.isSymbolicLink())throw Error('unsafe-status-directory');
 const names=readdirSync(path);
 if(names.length>1000)throw Error('too-many-status-files');
 const observations=[];
 for(const name of names){
  if(!/^[0-9a-f-]{36}\.json$/.test(name))throw Error('unexpected-status-file');
  const id=name.slice(0,-5);
  if(!UUID.test(id))throw Error('invalid-status-id');
  const file=join(path,name),st=lstatSync(file);
  if(!st.isFile()||st.isSymbolicLink()||st.size<1||st.size>1024)throw Error('unsafe-status-file');
  let obj;try{obj=JSON.parse(readFileSync(file,'utf8'))}catch{throw Error('invalid-status-json')}
  if(Object.keys(obj).sort().join(',')!==['orderId','status','executionAllowed','observedAt'].sort().join(',')||
    obj.orderId!==id||obj.status!=='observed-untrusted'||obj.executionAllowed!==false||
    typeof obj.observedAt!=='string'||!Number.isFinite(Date.parse(obj.observedAt)))throw Error('invalid-status-record');
  observations.push({orderId:id,status:'observed-untrusted',observedAt:obj.observedAt,executionAllowed:false});
 }
 observations.sort((a,b)=>b.observedAt.localeCompare(a.observedAt)||a.orderId.localeCompare(b.orderId));
 return Object.freeze({version:'hic.staging-ui-status/1',trust:'untrusted-local-observation',executionAllowed:false,observations:observations.slice(0,limit)});
}
