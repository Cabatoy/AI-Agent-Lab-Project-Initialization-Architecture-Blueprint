import { isAbsolute, resolve } from 'node:path';
import { defaultStateDir } from './chat-forward.mjs';
import { copilotConfigPath, loadCopilotConfig, runCopilotOnce } from './copilot-readonly.mjs';

const args=process.argv.slice(2);
const value=name=>{const index=args.indexOf(name);return index>=0&&index+1<args.length?args[index+1]:undefined;};
const valueFlags=new Set(['--config','--state-dir']);
const known=new Set(['--enable-copilot-read-only','--once',...valueFlags]);
const flags=args.filter((arg,index)=>!(index>0&&valueFlags.has(args[index-1])));
const duplicateValues=[...valueFlags].some(flag=>args.filter(arg=>arg===flag).length>1);
const valid=args.includes('--enable-copilot-read-only')&&args.includes('--once')&&
  flags.every(arg=>known.has(arg))&&new Set(flags).size===flags.length&&!duplicateValues&&
  [...valueFlags].every(flag=>!args.includes(flag)||(value(flag)&&!value(flag).startsWith('--')));

if(!valid) {
  console.error('Usage: node src/copilot-readonly-cli.mjs --enable-copilot-read-only --once [--config <absolute-path>] [--state-dir <absolute-path>]');
  process.exitCode=2;
} else {
  const stateDir=resolve(value('--state-dir')??defaultStateDir());
  const configPath=value('--config')??copilotConfigPath(stateDir);
  if(!isAbsolute(configPath)) {
    console.error(JSON.stringify({status:'blocked',reason:'invalid-copilot-config-path'}));
    process.exitCode=1;
  } else {
    try {
      const result=await runCopilotOnce({config:loadCopilotConfig(configPath),stateDir});
      console.log(JSON.stringify({
        status:result.status,reason:result.reason,runId:result.runId,
        repository:result.repository,branch:result.branch
      }));
      if(result.status!=='completed') process.exitCode=1;
    } catch(error) {
      const allowed=new Set([
        'copilot-read-only-not-configured','unsafe-copilot-config','invalid-copilot-config',
        'copilot-read-only-disabled','invalid-copilot-repository','invalid-copilot-config-path',
        'unsafe-copilot-state-dir','copilot-result-record-failed'
      ]);
      const reason=allowed.has(error?.message)?error.message:'copilot-execution-blocked';
      console.error(JSON.stringify({status:'blocked',reason}));
      process.exitCode=1;
    }
  }
}
