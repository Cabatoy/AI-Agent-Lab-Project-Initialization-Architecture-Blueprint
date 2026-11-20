import { fileURLToPath } from 'node:url';
import { defaultStateDir } from './watch.mjs';
import { relayStatus } from './status.mjs';

if(process.argv.length!==2) {
  console.log(JSON.stringify({status:'blocked',reason:'invalid-status-invocation'}));
  process.exitCode=1;
} else {
  process.env.GIT_OPTIONAL_LOCKS='0';
  const workspaceRoot=fileURLToPath(new URL('../../../..',import.meta.url)).replace(/[\\/]$/,'');
  console.log(JSON.stringify(relayStatus({workspaceRoot,stateDir:defaultStateDir()})));
}
