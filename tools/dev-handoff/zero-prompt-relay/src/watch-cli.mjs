import { join } from 'node:path';
import { defaultStateDir, loadWatchConfig, watchPoll } from './watch.mjs';

const args=process.argv.slice(2);
const value=name=>{const i=args.indexOf(name);return i>=0&&i+1<args.length?args[i+1]:undefined;};
const known=new Set(['--enable-watch','--poll-once','--config','--state-dir']);
const flags=args.filter((a,i)=>!(i>0&&['--config','--state-dir'].includes(args[i-1])));
const valid=args.includes('--enable-watch')&&args.includes('--poll-once')&&flags.every(a=>known.has(a))&&new Set(flags).size===flags.length;

if(!valid) {
  console.error('Usage: node watch-cli.mjs --enable-watch --poll-once [--config <path>] [--state-dir <path>]');
  process.exitCode=2;
} else {
  const stateDir=value('--state-dir')??defaultStateDir();
  try {
    const watchConfigPath=value('--config')??join(stateDir,'watch-forward.json');
    const config=loadWatchConfig(watchConfigPath);
    const result=await watchPoll({config,stateDir});
    console.log(JSON.stringify({status:'ok',config:result.config,pendingCount:result.issues.length,issues:result.issues.map(x=>({number:x.number,title:x.title}))}));
  } catch(error) {
    const allow=/^(invalid-|unsafe-|watch-|github-)/;
    const reason=typeof error?.message==='string'&&allow.test(error.message)?error.message:'watch-blocked';
    console.error(JSON.stringify({status:'blocked',reason}));
    process.exitCode=1;
  }
}
