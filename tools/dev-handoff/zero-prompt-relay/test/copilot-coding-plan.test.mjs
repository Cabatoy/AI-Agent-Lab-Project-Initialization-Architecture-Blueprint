import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { CODING_PLAN_VERSION, loadCodingPlanConfig, planCodingDryRun } from '../src/copilot-coding-plan.mjs';
import { PERSONAL_REPOSITORY } from '../src/copilot-readonly.mjs';

function fixture(t) {
  const root=mkdtempSync(join(tmpdir(),'hic-coding-plan-'));
  t.after(()=>rmSync(root,{recursive:true,force:true}));
  const workspacePath=join(root,'repo');
  mkdirSync(workspacePath);
  const git=(args,options={})=>execFileSync('git',['-C',workspacePath,...args],{
    encoding:'utf8',stdio:['ignore','pipe','ignore'],windowsHide:true,...options
  }).trim();
  git(['init','--quiet']);
  git(['checkout','--quiet','-b','task/coding-test']);
  writeFileSync(join(workspacePath,'README.md'),'fixture\n');
  git(['add','README.md']);
  git(['-c','user.name=HIC Offline Test','-c','user.email=hic-offline@example.invalid','commit','--quiet','-m','fixture']);
  git(['remote','add','origin','https://github.com/'+PERSONAL_REPOSITORY+'.git']);
  const config={
    version:CODING_PLAN_VERSION,enabled:true,repository:PERSONAL_REPOSITORY,workspacePath,
    expectedBranch:'task/coding-test',expectedHead:git(['rev-parse','HEAD']),
    task:{id:'local-1',data:'UNTRUSTED_SECRET task text'},
    permissions:{writePaths:['src/example.js'],testCommands:[['npm','run','check']]},
    approvals:{task:null,permissions:null}
  };
  return {root,workspacePath,git,config};
}

function approved(config) {
  const review=planCodingDryRun(config,{review:true});
  assert.equal(review.phase,'REVIEW');
  return {...config,approvals:{task:review.scopeSha256,permissions:review.scopeSha256}};
}

test('operator review and separately approved dry-run are redacted and never executable',t=>{
  const {config,root}=fixture(t);
  const result=planCodingDryRun(approved(config));
  assert.equal(result.status,'PASS');
  assert.deepEqual(result.execution,{status:'BLOCKED',reason:'agent-execution-unavailable'});
  assert.equal(result.agentExecutionAvailable,false);
  assert.equal(result.phase,'DRY_RUN');
  assert.deepEqual(result.intendedAction,{type:'single-shot-coding-agent',
    writePaths:['src/example.js'],testCommands:[['npm','run','check']]});
  assert.doesNotMatch(JSON.stringify(result),/UNTRUSTED_SECRET|local-1/);
  assert.equal(JSON.stringify(result).includes(root),false);
});

test('approval binds task, permissions, repository, branch and exact head to a single review',t=>{
  const {config}=fixture(t);
  const c=approved(config);
  assert.equal(planCodingDryRun({...c,approvals:{...c.approvals,task:null}}).reason,'coding-task-approval-required');
  assert.equal(planCodingDryRun({...c,approvals:{...c.approvals,permissions:null}}).reason,'coding-permission-approval-required');
  assert.equal(planCodingDryRun({...c,task:{...c.task,data:'changed'}}).reason,'coding-task-approval-required');
  assert.equal(planCodingDryRun({...c,permissions:{...c.permissions,writePaths:['src/other.js']}}).reason,'coding-task-approval-required');
  assert.equal(planCodingDryRun({...c,expectedHead:'0'.repeat(40)}).reason,'coding-head-mismatch');
});

test('wrong repository, worktree, branch and dirty tree fail closed',t=>{
  const {config,workspacePath,git}=fixture(t);
  const c=approved(config);
  assert.equal(planCodingDryRun({...c,repository:'other/repo'}).reason,'coding-repository-mismatch');
  assert.equal(planCodingDryRun({...c,workspacePath:join(workspacePath,'missing')}).reason,'workspace-path-unavailable');
  assert.equal(planCodingDryRun({...c,expectedBranch:'task/other'}).reason,'workspace-branch-mismatch');
  git(['remote','set-url','origin','https://github.com/other/repo.git']);
  assert.equal(planCodingDryRun(c).reason,'workspace-repository-mismatch');
  git(['remote','set-url','origin','https://github.com/'+PERSONAL_REPOSITORY+'.git']);
  writeFileSync(join(workspacePath,'dirty.txt'),'x');
  assert.equal(planCodingDryRun(c).reason,'workspace-not-clean');
});

test('disabled, malformed, oversized and unsafe declarations fail closed',t=>{
  const {config}=fixture(t);
  assert.equal(planCodingDryRun({...config,enabled:false}).reason,'coding-disabled');
  assert.equal(planCodingDryRun({...config,extra:'x'}).reason,'invalid-coding-config');
  assert.equal(planCodingDryRun({...config,task:{...config.task,data:'x'.repeat(8193)}}).reason,'invalid-coding-task');
  for(const path of ['../outside','C:\\outside','src/*','src/.git/config','src/file:stream','src/file.']) {
    assert.equal(planCodingDryRun({...config,permissions:{...config.permissions,writePaths:[path]}}).reason,'unsafe-coding-permissions',path);
  }
  for(const command of [['powershell','-Command','anything'],['npm','run','check;evil'],['npm','run','check','extra'],['dotnet','test','--filter=x']]) {
    assert.equal(planCodingDryRun({...config,permissions:{...config.permissions,testCommands:[command]}}).reason,'unsafe-coding-permissions');
  }
});

test('CLI is opt-in, single-shot, non-executing and redacts failure details',t=>{
  const {root,config}=fixture(t);
  const path=join(root,'config.json');
  writeFileSync(path,JSON.stringify(approved(config)));
  const cli=fileURLToPath(new URL('../src/copilot-coding-plan-cli.mjs',import.meta.url));
  const run=args=>spawnSync(process.execPath,[cli,...args],{encoding:'utf8',windowsHide:true});
  for(const args of [[],['--dry-run','--config',path],['--enable-coding-plan','--dry-run','--config',path,'--execute']]) {
    const output=run(args);
    assert.equal(output.status,1);
    assert.equal(JSON.parse(output.stdout).reason,'invalid-coding-invocation');
  }
  const output=run(['--enable-coding-plan','--dry-run','--config',path]);
  assert.equal(output.status,0);
  assert.equal(JSON.parse(output.stdout).execution.status,'BLOCKED');
  assert.equal(JSON.parse(output.stdout).agentExecutionAvailable,false);
  assert.doesNotMatch(output.stdout,/UNTRUSTED_SECRET|local-1/);
  assert.equal(output.stderr,'');
  assert.throws(()=>loadCodingPlanConfig(join(root,'missing.json')),/coding-config-unavailable/);
  writeFileSync(path,'x'.repeat(16385));
  assert.throws(()=>loadCodingPlanConfig(path),/unsafe-coding-config/);
});
