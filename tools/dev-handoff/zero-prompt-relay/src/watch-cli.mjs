import { join } from 'node:path';
import { defaultStateDir, loadWatchConfig, watchOnce, watchLoop } from './watch.mjs';
import { checkAuthMode, describeGitHubError } from './github-client.mjs';
import { forwardOnce } from './chat-forward.mjs';

const args=process.argv.slice(2);
const value=name=>{const i=args.indexOf(name);return i>=0&&i+1<args.length?args[i+1]:undefined;};
const cmd=args[0];
const withOverrides=config=>{
  let c=config;
  if(args.includes('--agent-mode')) c={...c,agentModeOptIn:true};
  if(args.includes('--github-auth')) c={...c,githubAuth:checkAuthMode(value('--github-auth')??'')};
  return c;
};
const reportBlocked=error=>{
  const allow=/^(invalid-|unsafe-|watch-|github-|forward-|chat-|response-too-large)/;
  const reason=typeof error?.message==='string'&&allow.test(error.message)?error.message:'watch-blocked';
  if(process.env.DEBUG_WATCH) console.error('[DEBUG]', describeGitHubError(error));
  console.error(JSON.stringify({status:'blocked',reason,...(error?.diagnostic?{diagnostic:describeGitHubError(error)}:{})}));
  process.exitCode=1;
};

if(cmd==='--help'||!cmd) {
  console.log('Usage:');
  console.log('  node watch-cli.mjs --poll-once [--code-command <path>] [--agent-mode] [--github-auth none|gcm|env] [--config <path>] [--state-dir <path>]');
  console.log('  node watch-cli.mjs --poll-loop [--interval <ms>] [--code-command <path>] [--agent-mode] [--github-auth none|gcm|env] [--config <path>] [--state-dir <path>]');
  process.exitCode=cmd?0:2;
} else if(cmd==='--poll-once') {
  const stateDir=value('--state-dir')??defaultStateDir();
  try {
    const watchConfigPath=value('--config')??join(stateDir,'watch-forward.json');
    const config=withOverrides(loadWatchConfig(watchConfigPath));
    const codeCommand=value('--code-command')||process.env.VSCODE_CHAT_COMMAND||undefined;
    const result=await watchOnce({config,stateDir,forwardImpl:forwardOnce,codeCommand});
    console.log(JSON.stringify({status:result.status,forwarded:result.forwarded}));
  } catch(error) {
    reportBlocked(error);
  }
} else if(cmd==='--poll-loop') {
  const stateDir=value('--state-dir')??defaultStateDir();
  const intervalMs=parseInt(value('--interval')??'60000',10);
  const codeCommand=value('--code-command')||process.env.VSCODE_CHAT_COMMAND||undefined;
  if(!Number.isFinite(intervalMs)||intervalMs<5000) {
    console.error(JSON.stringify({status:'blocked',reason:'invalid-interval'}));
    process.exitCode=1;
  } else {
    try {
      const watchConfigPath=value('--config')??join(stateDir,'watch-forward.json');
      const config=withOverrides(loadWatchConfig(watchConfigPath));
      const result=await watchLoop({config,stateDir,forwardImpl:forwardOnce,intervalMs,codeCommand});
      if(result.status==='blocked') reportBlocked(new Error(result.reason));
    } catch(error) {
      reportBlocked(error);
    }
  }
} else {
  console.error('Unknown command: '+cmd);
  process.exitCode=2;
}
