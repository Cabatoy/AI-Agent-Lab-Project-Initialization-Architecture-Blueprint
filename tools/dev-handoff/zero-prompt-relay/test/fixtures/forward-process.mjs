// Simulates one relay process start: fake GitHub issue response + real child-process launch of the fake `code` command.
import { appendFileSync, readFileSync } from 'node:fs';
import { forwardOnce, loadConfig } from '../../src/chat-forward.mjs';

const [configPath,stateDir,issuePath,fetchLog]=process.argv.slice(2);
const fetchImpl=async url=>{
  appendFileSync(fetchLog,String(url)+'\n');
  return {status:200,text:async()=>readFileSync(issuePath,'utf8')};
};
const result=await forwardOnce({config:loadConfig(configPath),stateDir,fetchImpl});
console.log(JSON.stringify(result));
