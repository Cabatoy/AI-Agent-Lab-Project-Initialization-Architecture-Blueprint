import { createHash } from 'node:crypto';
import { spawn } from 'node:child_process';
import { closeSync, existsSync, lstatSync, mkdirSync, openSync, readFileSync, renameSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { isAbsolute, join } from 'node:path';

export const CONFIG_VERSION='hic.dev-chat-forward/1';
export const RECORD_VERSION='hic.dev-chat-forward-record/1';
export const CHAT_PROMPT='Follow the HIC relay task in the attached file';
const REPO=/^[A-Za-z0-9-]{1,39}\/[A-Za-z0-9._-]{1,100}$/;
const LOGIN=/^[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?$/;
// Characters that are safe inside a double-quoted cmd.exe argument (no %, ^, &, |, <, >, ", !).
const CMD_SAFE=/^[A-Za-z0-9 _.:\\/()+,=@~-]+$/;
const MODES=['agent','ask','edit'];
const MAX_ISSUE_BYTES=131072, MAX_BODY=20000, MAX_TITLE=256;
const fail=code=>{throw new Error(code);};
const isObject=x=>!!x&&typeof x==='object'&&!Array.isArray(x);
const exactKeys=(x,required,optional=[])=>isObject(x)&&Object.keys(x).every(k=>required.includes(k)||optional.includes(k))&&required.every(k=>k in x);

export const defaultStateDir=()=>process.env.LOCALAPPDATA?join(process.env.LOCALAPPDATA,'HICDevRelay'):join(homedir(),'.hic-dev-relay');

export function checkConfig(x) {
  if(!exactKeys(x,['version','enabled','repository','issueNumber','trustedAuthor','codeCommand'],['chatMode']) || x.version!==CONFIG_VERSION) fail('invalid-chat-config');
  if(x.enabled!==true) fail('chat-forward-disabled');
  if(typeof x.repository!=='string' || !REPO.test(x.repository) || x.repository.includes('..')) fail('invalid-chat-repository');
  if(!Number.isSafeInteger(x.issueNumber) || x.issueNumber<1) fail('invalid-chat-issue');
  if(typeof x.trustedAuthor!=='string' || !LOGIN.test(x.trustedAuthor)) fail('invalid-chat-author');
  if(typeof x.codeCommand!=='string' || !isAbsolute(x.codeCommand) || !CMD_SAFE.test(x.codeCommand)) fail('invalid-chat-command');
  const chatMode=x.chatMode??'agent';
  if(!MODES.includes(chatMode)) fail('invalid-chat-mode');
  return {repository:x.repository,issueNumber:x.issueNumber,trustedAuthor:x.trustedAuthor,codeCommand:x.codeCommand,chatMode};
}

export function loadConfig(path) {
  let st;try{st=lstatSync(path)}catch{fail('chat-forward-not-configured')}
  if(!st.isFile() || st.isSymbolicLink() || st.size>4096) fail('unsafe-chat-config');
  let x;try{x=JSON.parse(readFileSync(path,'utf8').replace(/^\uFEFF/,''))}catch{fail('invalid-chat-config')}
  checkConfig(x);
  return x;
}

const issueUrl=c=>'https://github.com/'+c.repository+'/issues/'+c.issueNumber;
export function checkIssue(x,c) {
  if(!isObject(x) || x.number!==c.issueNumber || x.html_url!==issueUrl(c)) fail('invalid-issue');
  if('pull_request' in x) fail('issue-is-pull-request');
  if(x.state!=='open') fail('issue-not-open');
  if(x.user?.login!==c.trustedAuthor) fail('issue-author-untrusted');
  if(typeof x.title!=='string' || x.title.length===0 || x.title.length>MAX_TITLE) fail('invalid-issue');
  if(x.body!==null && (typeof x.body!=='string' || x.body.length>MAX_BODY)) fail('issue-body-too-large');
  if(typeof x.updated_at!=='string' || !Number.isFinite(Date.parse(x.updated_at))) fail('invalid-issue');
  const body=x.body??'';
  const digest=createHash('sha256').update(JSON.stringify({title:x.title,body})).digest('hex');
  return {number:x.number,url:x.html_url,title:x.title,body,author:x.user.login,updatedAt:x.updated_at,digest};
}

export async function fetchIssue(c,{fetchImpl=fetch,timeoutMs=10000}={}) {
  const controller=new AbortController(), timer=setTimeout(()=>controller.abort(),timeoutMs);
  try {
    const url='https://api.github.com/repos/'+c.repository+'/issues/'+c.issueNumber;
    const response=await fetchImpl(new URL(url),{method:'GET',headers:{Accept:'application/vnd.github+json','User-Agent':'HIC-dev-chat-forward/0.1'},redirect:'manual',signal:controller.signal});
    if(response.status!==200) fail(response.status===403||response.status===429?'github-rate-limited-or-forbidden':'github-read-failed');
    const text=await response.text();
    if(Buffer.byteLength(text)>MAX_ISSUE_BYTES) fail('response-too-large');
    let json;try{json=JSON.parse(text)}catch{fail('invalid-github-response')}
    return checkIssue(json,c);
  } finally {clearTimeout(timer)}
}

export function buildPrompt(issue,c,forwardedAt) {
  return [
    '# HIC relay görevi: GitHub issue #'+issue.number,
    '',
    '- Kaynak: '+issue.url,
    '- Repository: '+c.repository,
    '- Issue yazarı: '+issue.author+' (yerel ayardaki güvenilir yazar)',
    '- Issue son güncelleme: '+issue.updatedAt,
    '- İçerik özeti (SHA-256): '+issue.digest,
    '- Relay iletim zamanı: '+forwardedAt,
    '',
    '## Çalışma kuralları',
    '',
    '1. Aşağıdaki issue açıklamasını ve kabul kriterlerini oku; gerekiyorsa issue sayfasındaki güncel yorumları da kontrol et.',
    '2. Mevcut checkout\'taki commit edilmemiş değişikliklere dokunma; uygun temelden ayrı bir branch/worktree aç.',
    '3. Repo talimatlarına (`.github/copilot-instructions.md`, `AGENTS.md` varsa) uy; ilgili build ve testleri çalıştır.',
    '4. Sonucu issue\'yu referanslayan (`Refs #'+issue.number+'`) bir draft PR olarak aç; değişen dosyaları ve doğrulama yöntemini PR açıklamasına yaz ve PR bağlantısını issue\'ya yorum olarak ekle.',
    '5. Merge veya deploy yapma; sırları yanıta, loga veya repoya yazma. Takılırsan hangi adımın neden başarısız olduğunu issue\'ya yaz.',
    '6. Relay bu görevi yalnızca iletti; komutun çıkış kodu işin tamamlandığını göstermez. Tamamlanma kanıtı draft PR ve issue yorumudur.',
    '',
    '## Issue başlığı',
    '',
    issue.title,
    '',
    '## Issue açıklaması (GitHub\'dan alındığı haliyle)',
    '',
    issue.body,
    ''
  ].join('\n');
}

const recordName=c=>c.repository.replace('/','__')+'__'+c.issueNumber;
function safeDir(path) {
  mkdirSync(path,{recursive:true,mode:0o700});
  const st=lstatSync(path);if(!st.isDirectory()||st.isSymbolicLink()) fail('unsafe-chat-state-dir');
  return path;
}
function readRecord(path,c) {
  const st=lstatSync(path);
  if(!st.isFile()||st.isSymbolicLink()||st.size>4096) fail('unsafe-chat-record');
  let x;try{x=JSON.parse(readFileSync(path,'utf8'))}catch{fail('unreadable-chat-record')}
  if(!isObject(x)||x.version!==RECORD_VERSION||x.repository!==c.repository||x.issueNumber!==c.issueNumber||x.completionVerified!==false) fail('chat-record-conflict');
  return x;
}
function writeRecordUpdate(path,record) {
  const tmp=path+'.tmp';
  writeFileSync(tmp,JSON.stringify(record)+'\n',{mode:0o600});
  renameSync(tmp,path);
}

export function buildLaunch(c,promptPath,platform=process.platform) {
  if(!isAbsolute(promptPath)||!CMD_SAFE.test(promptPath)) fail('unsafe-prompt-path');
  const args=['chat','-m',c.chatMode,'-r','-a',promptPath,CHAT_PROMPT];
  if(platform!=='win32') return {file:c.codeCommand,args,verbatim:false};
  // code.cmd cannot be spawned without a shell; every argument is validated against CMD_SAFE above.
  const line='"'+['"'+c.codeCommand+'"',...args.map(a=>/^[a-z-]+$/.test(a)?a:'"'+a+'"')].join(' ')+'"';
  return {file:process.env.ComSpec||'cmd.exe',args:['/d','/s','/c',line],verbatim:true};
}

export function runLaunch({file,args,verbatim},{timeoutMs=60000}={}) {
  return new Promise(resolve=>{
    let done=false;
    const finish=r=>{if(!done){done=true;clearTimeout(timer);resolve(r)}};
    let child;
    try{child=spawn(file,args,{stdio:'ignore',windowsHide:true,windowsVerbatimArguments:verbatim,shell:false})}
    catch{return resolve({exitCode:null,error:'spawn-failed'})}
    const timer=setTimeout(()=>{child.kill();finish({exitCode:null,error:'launch-timeout'})},timeoutMs);
    child.on('error',()=>finish({exitCode:null,error:'spawn-failed'}));
    child.on('exit',code=>finish({exitCode:code,error:null}));
  });
}

export async function forwardOnce({config,stateDir=defaultStateDir(),fetchImpl=fetch,launch=runLaunch,clock=Date.now,platform=process.platform}={}) {
  const c=checkConfig(config);
  const root=safeDir(join(safeDir(stateDir),'chat-forward'));
  const records=safeDir(join(root,'records')), prompts=safeDir(join(root,'prompts'));
  const recordPath=join(records,recordName(c)+'.json');
  if(existsSync(recordPath)) {
    // At-most-once: an existing record (even a crashed 'claimed' one) blocks re-sending; the operator removes it deliberately.
    const r=readRecord(recordPath,c);
    return {status:'already-forwarded',issueUrl:issueUrl(c),recordStatus:r.status,completionVerified:false};
  }
  const issue=await fetchIssue(c,{fetchImpl});
  const forwardedAt=new Date(clock()).toISOString();
  const record={version:RECORD_VERSION,repository:c.repository,issueNumber:c.issueNumber,issueUrl:issue.url,issueDigest:issue.digest,issueUpdatedAt:issue.updatedAt,chatMode:c.chatMode,claimedAt:forwardedAt,status:'claimed',launcherExitCode:null,completionVerified:false};
  let fd;
  try{fd=openSync(recordPath,'wx',0o600);writeFileSync(fd,JSON.stringify(record)+'\n')}
  catch(e){if(e?.code==='EEXIST') return {status:'already-forwarded',issueUrl:issue.url,recordStatus:'claimed',completionVerified:false};throw e}
  finally{if(fd!==undefined)closeSync(fd)}
  const promptPath=join(prompts,recordName(c)+'.md');
  writeFileSync(promptPath,buildPrompt(issue,c,forwardedAt),{mode:0o600});
  const result=await launch(buildLaunch(c,promptPath,platform));
  const status=result.error?result.error:(result.exitCode===0?'handed-off-unverified':'launch-failed');
  writeRecordUpdate(recordPath,{...record,status,launcherExitCode:result.exitCode,launchedAt:new Date(clock()).toISOString()});
  return {status,issueUrl:issue.url,launcherExitCode:result.exitCode,completionVerified:false};
}
