import { createHash } from 'node:crypto';
import { existsSync, lstatSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { join } from 'node:path';

export const WATCH_VERSION='hic.dev-watch/1';
export const WATCH_DB_VERSION='hic.dev-watch-db/1';
const REPO=/^[A-Za-z0-9-]{1,39}\/[A-Za-z0-9._-]{1,100}$/;
const LOGIN=/^[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?$/;
const LABEL=/^[a-z-]{1,50}$/i;
const fail=code=>{throw new Error(code);};
const isObject=x=>!!x&&typeof x==='object'&&!Array.isArray(x);

export const defaultStateDir=()=>process.env.LOCALAPPDATA?join(process.env.LOCALAPPDATA,'HICDevRelay'):join(homedir(),'.hic-dev-relay');

export function checkWatchConfig(x) {
  if(!isObject(x)||x.version!==WATCH_VERSION||typeof x.repository!=='string'||typeof x.trustedAuthor!=='string'||typeof x.relayLabel!=='string') fail('invalid-watch-config');
  if(!REPO.test(x.repository)||x.repository.includes('..')) fail('invalid-watch-repository');
  if(!LOGIN.test(x.trustedAuthor)) fail('invalid-watch-author');
  if(!LABEL.test(x.relayLabel)) fail('invalid-watch-label');
  return {repository:x.repository,trustedAuthor:x.trustedAuthor,relayLabel:x.relayLabel};
}

export function loadWatchConfig(path) {
  let st;try{st=lstatSync(path)}catch{fail('watch-config-not-found')}
  if(!st.isFile()||st.isSymbolicLink()||st.size>4096) fail('unsafe-watch-config');
  let x;try{x=JSON.parse(readFileSync(path,'utf8').replace(/^\uFEFF/,''))}catch{fail('invalid-watch-config-json')}
  return checkWatchConfig(x);
}

export async function fetchWatchIssues(repo,label,author,{fetchImpl=fetch}={}) {
  const q=new URLSearchParams({state:'open',per_page:'30',labels:label});
  const url='https://api.github.com/repos/'+repo+'/issues?'+q;
  try {
    const response=await fetchImpl(url,{method:'GET',headers:{Accept:'application/vnd.github+json','User-Agent':'HIC-watch/0.1'},redirect:'manual',timeout:10000});
    if(response.status!==200) fail('github-read-failed');
    const json=await response.json();
    if(!Array.isArray(json)) fail('invalid-github-response');
    return json.filter(x=>!('pull_request' in x)&&x.state==='open'&&x.user?.login===author&&Number.isSafeInteger(x.number)&&x.number>0);
  } catch(e) {
    fail('watch-fetch-failed');
  }
}

function safeDir(path) {
  mkdirSync(path,{recursive:true,mode:0o700});
  const st=lstatSync(path);if(!st.isDirectory()||st.isSymbolicLink()) fail('unsafe-watch-state-dir');
  return path;
}

function readWatchDB(path) {
  if(!existsSync(path)) return {version:WATCH_DB_VERSION,records:{}};
  const st=lstatSync(path);
  if(!st.isFile()||st.isSymbolicLink()||st.size>65536) fail('unsafe-watch-db');
  let x;try{x=JSON.parse(readFileSync(path,'utf8'))}catch{fail('unreadable-watch-db')}
  if(!isObject(x)||x.version!==WATCH_DB_VERSION||!isObject(x.records)) fail('watch-db-conflict');
  return x;
}

function writeWatchDB(path,db) {
  const tmp=path+'.tmp';
  writeFileSync(tmp,JSON.stringify(db)+'\n',{mode:0o600});
  renameSync(tmp,path);
}

function getRecordKey(repo,issueNumber) {
  return repo.replace('/','__')+'__'+issueNumber;
}

function isAlreadyForwarded(record) {
  return record&&(record.status==='forwarded'||record.status==='handed-off-unverified'||record.status==='launch-failed');
}

export async function watchPoll({config,stateDir=defaultStateDir(),fetchImpl=fetch}={}) {
  const c=checkWatchConfig(config);
  const issues=await fetchWatchIssues(c.repository,c.relayLabel,c.trustedAuthor,{fetchImpl});
  
  const root=safeDir(join(safeDir(stateDir),'watch-forward'));
  const dbPath=join(root,'issues.json');
  const db=readWatchDB(dbPath);
  
  const pending=[];
  for(const issue of issues) {
    const key=getRecordKey(c.repository,issue.number);
    const record=db.records[key];
    if(!isAlreadyForwarded(record)) {
      pending.push({number:issue.number,title:issue.title,body:issue.body??'',updatedAt:issue.updated_at});
    }
  }
  
  return {config:c,pending,dbPath,db};
}

export async function recordForwardAttempt({db,dbPath,repository,issueNumber,status='pending',stateDir=defaultStateDir()}={}) {
  const key=getRecordKey(repository,issueNumber);
  db.records[key]={issueNumber,repository,status,attemptedAt:new Date().toISOString()};
  const root=safeDir(join(safeDir(stateDir),'watch-forward'));
  const path=dbPath||join(root,'issues.json');
  const tmp=path+'.tmp';
  writeFileSync(tmp,JSON.stringify(db)+'\n',{mode:0o600});
  const fs=require('node:fs');
  fs.renameSync(tmp,path);
  return db.records[key];
}

