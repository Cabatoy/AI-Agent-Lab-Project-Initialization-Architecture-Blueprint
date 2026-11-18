import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here=dirname(fileURLToPath(import.meta.url));
const launcher=join(here,'..','scripts','Run-DevRelay.ps1');
const powershell=process.env.POWERSHELL_EXE||'powershell.exe';

const run=(args,localAppData)=>spawnSync(powershell,['-NoProfile','-NonInteractive','-File',launcher,...args],{
  env:{...process.env,LOCALAPPDATA:localAppData},
  encoding:'utf8'
});

test('Windows launcher writes Ask by default and blocks runtime Agent promotion',{skip:process.platform!=='win32'},()=>{
  const localAppData=mkdtempSync(join(tmpdir(),'hic-relay-launcher-'));
  const workspace=mkdtempSync(join(tmpdir(),'hic-relay-workspace-'));
  try {
    const setup=[
      '-Mode','ChatForwardWatchSetup',
      '-Repository','Cabatoy/test-repo',
      '-TrustedAuthor','Cabatoy',
      '-RelayLabel','hic-relay-ready',
      '-WorkspacePath',workspace,
      '-ExpectedBranch','task/171-test'
    ];
    const setupForWorkspace=path=>{
      const args=[...setup];
      args[args.indexOf('-WorkspacePath')+1]=path;
      return args;
    };
    const askSetup=run(setup,localAppData);
    assert.equal(askSetup.status,0,askSetup.stderr);
    const configPath=join(localAppData,'HICDevRelay','watch-forward.json');
    const askConfig=JSON.parse(readFileSync(configPath,'utf8'));
    assert.equal(askConfig.chatMode,'ask');
    assert.equal(askConfig.agentModeOptIn,undefined);
    assert.equal(askConfig.workspacePath.toLowerCase(),workspace.toLowerCase());
    assert.equal(askConfig.expectedBranch,'task/171-test');

    const promotion=run(['-Mode','ChatForwardWatchOnce','-ChatMode','agent'],localAppData);
    assert.notEqual(promotion.status,0);
    const agentSetup=run([...setup,'-ChatMode','agent'],localAppData);
    assert.notEqual(agentSetup.status,0,'Agent setup must be rejected');

    const multiRoot=join(workspace,'multi-root.code-workspace');
    writeFileSync(multiRoot,JSON.stringify({folders:[{path:workspace},{path:localAppData}]}));
    const rejectedWorkspace=run([...setupForWorkspace(multiRoot),'-ReplaceConfig'],localAppData);
    assert.notEqual(rejectedWorkspace.status,0,'Multi-root workspace files must be rejected');
    assert.match(rejectedWorkspace.stderr,/single-folder Git worktree/);
    const uncWorkspace=run([...setupForWorkspace('\\\\server\\share\\repo'),'-ReplaceConfig'],localAppData);
    assert.notEqual(uncWorkspace.status,0,'UNC paths must be rejected before resolution');
    assert.match(uncWorkspace.stderr,/UNC and device paths are not allowed/);
  } finally {
    rmSync(localAppData,{recursive:true,force:true});
    rmSync(workspace,{recursive:true,force:true});
  }
});
