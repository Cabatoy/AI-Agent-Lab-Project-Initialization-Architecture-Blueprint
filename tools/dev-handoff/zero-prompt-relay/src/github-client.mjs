import { spawn } from 'node:child_process';

// Node port of the PR #145 RelayAuthClient semantics (GitHubRestClient/RateLimitHandler/GitCredentialManagerProvider)
// for the watcher polling path. Error messages are fixed codes; tokens never appear in messages or diagnostics.
export const AUTH_MODES=['none','gcm','env'];
export const TOKEN_ENV='HIC_RELAY_GITHUB_TOKEN';
const TOKEN=/^[A-Za-z0-9_.-]{20,255}$/;
const API_ORIGIN='https://api.github.com';

export class GitHubError extends Error {
  constructor(code,diagnostic={}) {super(code);this.name='GitHubError';this.code=code;this.diagnostic=diagnostic;}
}

export function checkAuthMode(x) {
  const mode=x??'none';
  if(!AUTH_MODES.includes(mode)) throw new Error('invalid-github-auth');
  return mode;
}

export function envTokenProvider(env=process.env) {
  return async()=>env[TOKEN_ENV]||null;
}

const LOGIN=/^[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?$/;

// Reads the github.com credential from Git Credential Manager without prompting (fails closed when absent).
// `username` asks GCM for that specific stored account instead of its default github.com account.
export function gcmTokenProvider({spawnImpl=spawn,timeoutMs=15000,username=null}={}) {
  if(username!==null&&!LOGIN.test(username)) throw new Error('invalid-github-login');
  const request='protocol=https\nhost=github.com\n'+(username?'username='+username+'\n':'')+'\n';
  return ()=>new Promise(resolve=>{
    let child,out='',done=false;
    const finish=v=>{if(!done){done=true;clearTimeout(timer);resolve(v)}};
    try {
      child=spawnImpl('git',['credential','fill'],{stdio:['pipe','pipe','ignore'],windowsHide:true,shell:false,
        env:{...process.env,GIT_TERMINAL_PROMPT:'0',GCM_INTERACTIVE:'never',GIT_ASKPASS:'',SSH_ASKPASS:''}});
    } catch {return resolve(null)}
    const timer=setTimeout(()=>{try{child.kill()}catch{}finish(null)},timeoutMs);
    child.stdout.on('data',d=>{out+=d;if(out.length>8192){try{child.kill()}catch{}finish(null)}});
    child.on('error',()=>finish(null));
    child.on('close',code=>{
      if(code!==0) return finish(null);
      const line=out.split(/\r?\n/).find(l=>l.startsWith('password='));
      finish(line?line.slice('password='.length):null);
    });
    child.stdin.on('error',()=>{});
    child.stdin.end(request);
  });
}

const header=(response,name)=>{try{return response.headers?.get?.(name)??null}catch{return null}};

export function rateLimitInfo(response,clock=Date.now) {
  const remainingRaw=header(response,'x-ratelimit-remaining');
  const resetRaw=header(response,'x-ratelimit-reset');
  const retryRaw=header(response,'retry-after');
  const remaining=remainingRaw!==null&&/^\d+$/.test(remainingRaw)?Number(remainingRaw):null;
  const resetEpoch=resetRaw!==null&&/^\d+$/.test(resetRaw)?Number(resetRaw):null;
  let retryAfterMs=null;
  if(retryRaw!==null&&/^\d+$/.test(retryRaw)) retryAfterMs=Number(retryRaw)*1000;
  else if(remaining===0&&resetEpoch!==null) retryAfterMs=Math.max(0,resetEpoch*1000-clock());
  return {remaining,resetAt:resetEpoch!==null?new Date(resetEpoch*1000).toISOString():null,retryAfterMs};
}

// expectedLogin pins the credential to one GitHub account: GET /user must return that login before any other
// request is sent with the token, so a wrong (e.g. corporate) account fails closed instead of polling.
export function createGitHubClient({auth='none',fetchImpl=fetch,tokenProvider,clock=Date.now,timeoutMs=10000,userAgent='HIC-watch/0.2',expectedLogin=null}={}) {
  const mode=checkAuthMode(auth);
  if(expectedLogin!==null&&(mode==='none'||!LOGIN.test(expectedLogin))) throw new Error('invalid-github-login');
  const provider=tokenProvider??(mode==='gcm'?gcmTokenProvider({username:expectedLogin}):mode==='env'?envTokenProvider():null);
  let cached=null,verified=false;

  async function authorization() {
    if(mode==='none') return null;
    if(!cached) {
      let token=null;
      try{token=await provider()}catch{token=null}
      if(typeof token!=='string'||!TOKEN.test(token)) throw new GitHubError('github-auth-unavailable',{authMode:mode});
      cached=token;verified=false;
    }
    return 'Bearer '+cached;
  }

  async function verifyIdentity(authz) {
    if(!expectedLogin||verified) return;
    const {json}=await request(new URL(API_ORIGIN+'/user'),authz,64*1024);
    const actual=typeof json?.login==='string'&&LOGIN.test(json.login)?json.login:null;
    if(!actual||actual.toLowerCase()!==expectedLogin.toLowerCase()) {
      cached=null;
      throw new GitHubError('github-identity-mismatch',{authMode:mode,expectedLogin,actualLogin:actual});
    }
    verified=true;
  }

  async function getJson(url,{maxBytes=2*1024*1024}={}) {
    const target=new URL(url);
    if(target.origin!==API_ORIGIN) throw new GitHubError('github-url-not-allowed',{authMode:mode});
    const authz=await authorization();
    await verifyIdentity(authz);
    return request(target,authz,maxBytes);
  }

  async function request(target,authz,maxBytes) {
    const headers={Accept:'application/vnd.github+json','X-GitHub-Api-Version':'2022-11-28','User-Agent':userAgent};
    if(authz) headers.Authorization=authz;
    let response;
    try {
      // redirect:'manual' keeps the Authorization header from following a redirect to another host.
      response=await fetchImpl(target,{method:'GET',headers,redirect:'manual',signal:AbortSignal.timeout(timeoutMs)});
    } catch(e) {
      const timeout=e?.name==='TimeoutError'||e?.name==='AbortError';
      throw new GitHubError(timeout?'github-timeout':'github-network-error',{authMode:mode});
    }
    const status=response.status;
    const rate=rateLimitInfo(response,clock);
    const requestIdRaw=header(response,'x-github-request-id');
    const requestId=requestIdRaw!==null&&/^[A-Za-z0-9:._-]{1,64}$/.test(requestIdRaw)?requestIdRaw:null;
    const diagnostic={authMode:mode,status,rateLimitRemaining:rate.remaining,rateLimitResetAt:rate.resetAt,retryAfterMs:rate.retryAfterMs,requestId};
    if(status===401) {cached=null;throw new GitHubError('github-auth-invalid',diagnostic);}
    if(status===429||(status===403&&(rate.remaining===0||rate.retryAfterMs!==null))) throw new GitHubError('github-rate-limited',diagnostic);
    if(status===403) throw new GitHubError('github-forbidden',diagnostic);
    if(status!==200) throw new GitHubError('github-read-failed',diagnostic);
    const text=await response.text();
    if(Buffer.byteLength(text)>maxBytes) throw new GitHubError('response-too-large',diagnostic);
    try{return {json:JSON.parse(text),diagnostic}}catch{throw new GitHubError('invalid-github-response',diagnostic)}
  }

  return {auth:mode,expectedLogin,getJson};
}

export const TRANSIENT_GITHUB_ERRORS=new Set(['github-rate-limited','github-read-failed','github-timeout','github-network-error']);

// Only allowlisted, non-secret fields are rendered in logs.
export function describeGitHubError(e) {
  const d=e?.diagnostic??{};
  const parts=[typeof e?.message==='string'?e.message:'error'];
  for(const k of ['authMode','expectedLogin','actualLogin','status','rateLimitRemaining','rateLimitResetAt','retryAfterMs','requestId']) {
    const v=d[k];
    if(v!==null&&v!==undefined&&/^[A-Za-z0-9:._-]{1,64}$/.test(String(v))) parts.push(k+'='+v);
  }
  return parts.join(' ');
}
