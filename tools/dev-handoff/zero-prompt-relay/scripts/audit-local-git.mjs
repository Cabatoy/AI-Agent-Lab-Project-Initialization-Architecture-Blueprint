import { execFileSync } from 'node:child_process';
import { resolve } from 'node:path';
const repo=resolve(process.argv[2]||process.cwd());
function git(...args){
  try{return execFileSync('git',['-C',repo,...args],{encoding:'utf8',timeout:15000,windowsHide:true,stdio:['ignore','pipe','ignore']}).trim()}
  catch{return null}
}
const root=git('rev-parse','--show-toplevel');
if(!root){console.error('Not a Git repository; no changes made.');process.exitCode=2}
else {
  const worktrees=git('worktree','list','--porcelain')||'';
  const parsed=[], lines=worktrees.split(/\r?\n/);let w;
  for(const line of lines){if(line.startsWith('worktree ')){if(w)parsed.push(w);w={path:line.slice(9),branch:'detached'};}
    else if(line.startsWith('branch ')&&w)w.branch=line.slice(7).replace(/^refs\/heads\//,'');
    else if(line==='prunable'&&w)w.prunable=true;
  }
  if(w)parsed.push(w);
  const protectedNames=new Set(['main','master','develop']);
  const refs=(git('for-each-ref','--format=%(refname:short)|%(objectname)|%(upstream:short)','refs/heads')||'').split(/\r?\n/).filter(Boolean);
  const rows=[];
  for(const ref of refs){
    const [branch,sha,upstream]=ref.split('|');
    if(!branch||!/^[A-Za-z0-9_.\/-]+$/.test(branch))continue;
    const upstreamExists=upstream?git('rev-parse','--verify',upstream):null;
    const counts=upstreamExists?git('rev-list','--left-right','--count',upstream+'...'+branch):null;
    const [behind,ahead]=counts&&/^\d+\s+\d+$/.test(counts)?counts.split(/\s+/).map(Number):[null,null];
    const active=parsed.some(x=>x.branch===branch);
    const protectedBranch=protectedNames.has(branch);
    const merged=git('merge-base','--is-ancestor',sha,'origin/develop')=== ''?'yes':'unverified';
    let disposition=protectedBranch?'PROTECTED':active?'ACTIVE-WORKTREE':upstreamExists&&ahead>0?'AHEAD-REVIEW-BEFORE-PUSH':!upstreamExists?'NO-UPSTREAM-REVIEW':merged==='yes'?'CANDIDATE-ONLY-REQUIRES-PR-CHECK':'KEEP-REVIEW';
    rows.push({branch,upstream:upstream||'none',ahead,behind,worktreeActive:active,mergedIntoDevelop:merged,disposition});
  }
  console.log(JSON.stringify({mode:'AUDIT-ONLY-NO-PUSH-NO-DELETE',localBranches:rows.length,worktrees:parsed.length,prunableWorktreeEntries:parsed.filter(x=>x.prunable).length,rows},null,2));
  // No git fetch/prune, push, switch, worktree remove, branch -d/-D or remote delete.
}
