import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
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
  try {
    const setup=[
      '-Mode','ChatForwardWatchSetup',
      '-Repository','Cabatoy/test-repo',
      '-TrustedAuthor','Cabatoy',
      '-RelayLabel','hic-relay-ready'
    ];
    const askSetup=run(setup,localAppData);
    assert.equal(askSetup.status,0,askSetup.stderr);
    const configPath=join(localAppData,'HICDevRelay','watch-forward.json');
    const askConfig=JSON.parse(readFileSync(configPath,'utf8'));
    assert.equal(askConfig.chatMode,'ask');
    assert.equal(askConfig.agentModeOptIn,false);

    const promotion=run(['-Mode','ChatForwardWatchOnce','-ChatMode','agent'],localAppData);
    assert.notEqual(promotion.status,0);
    assert.match(promotion.stderr,/-ChatMode is only accepted during local setup/);

    const agentSetup=run([...setup,'-ReplaceConfig','-ChatMode','agent'],localAppData);
    assert.equal(agentSetup.status,0,agentSetup.stderr);
    const agentConfig=JSON.parse(readFileSync(configPath,'utf8'));
    assert.equal(agentConfig.chatMode,'agent');
    assert.equal(agentConfig.agentModeOptIn,true);
  } finally {
    rmSync(localAppData,{recursive:true,force:true});
  }
});
