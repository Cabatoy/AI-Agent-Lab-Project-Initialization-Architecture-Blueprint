import { blockedCodingConfig, loadCodingPlanConfig, planCodingDryRun } from './copilot-coding-plan.mjs';

const args=process.argv.slice(2);
const valid=args.length===4&&args[0]==='--enable-coding-plan'&&
  (args[1]==='--review'||args[1]==='--dry-run')&&args[2]==='--config'&&
  typeof args[3]==='string'&&!args[3].startsWith('--');
let result;
if(!valid) {
  result={status:'BLOCKED',reason:'invalid-coding-invocation',agentExecutionAvailable:false};
} else {
  try {
    result=planCodingDryRun(loadCodingPlanConfig(args[3]),{review:args[1]==='--review'});
  } catch(error) {
    result=blockedCodingConfig(error);
  }
}
console.log(JSON.stringify(result));
process.exitCode=result.status==='PASS'?0:1;
