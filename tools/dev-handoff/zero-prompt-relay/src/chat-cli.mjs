import { join } from 'node:path';
import { defaultStateDir, forwardOnce, loadConfig } from './chat-forward.mjs';

const args=process.argv.slice(2);
const value=name=>{const i=args.indexOf(name);return i>=0&&i+1<args.length?args[i+1]:undefined;};
const known=new Set(['--enable-chat-forward','--once','--config','--state-dir']);
const flags=args.filter((a,i)=>!(i>0&&['--config','--state-dir'].includes(args[i-1])));
const valid=args.includes('--enable-chat-forward')&&args.includes('--once')&&flags.every(a=>known.has(a))&&new Set(flags).size===flags.length;
if(!valid){
  console.error('Usage: node src/chat-cli.mjs --enable-chat-forward --once [--config <path>] [--state-dir <path>]');
  process.exitCode=2;
} else {
  const stateDir=value('--state-dir')??defaultStateDir();
  try{
    const config=loadConfig(value('--config')??join(stateDir,'chat-forward.json'));
    const result=await forwardOnce({config,stateDir});
    console.log(JSON.stringify({...result,note:'launcher exit code is not evidence of task completion'}));
    if(!['handed-off-unverified','already-forwarded'].includes(result.status)) process.exitCode=1;
  }catch(error){
    const allow=/^(invalid-[a-z-]+|unsafe-[a-z-]+|chat-[a-z-]+|issue-[a-z-]+|github-[a-z-]+|unreadable-chat-record|response-too-large)$/;
    const reason=typeof error?.message==='string'&&allow.test(error.message)?error.message:'chat-forward-blocked';
    console.error(JSON.stringify({status:'blocked',reason,completionVerified:false}));
    process.exitCode=1;
  }
}
