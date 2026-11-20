import { blockedCodingConfig, loadCodingPlanConfig, planCodingDryRun } from './copilot-coding-plan.mjs';
import { CODING_RUNNER_LIVE_ENABLED, runApprovedCodingTask } from './copilot-coding-runner.mjs';

const args=process.argv.slice(2);
const valid=args.length===6&&args[0]==='--enable-coding-agent'&&args[1]==='--once'&&
  args[2]==='--config'&&typeof args[3]==='string'&&!args[3].startsWith('--')&&
  args[4]==='--copilot-path'&&typeof args[5]==='string'&&!args[5].startsWith('--');
let result;
if(!valid) {
  result={status:'BLOCKED',reason:'invalid-coding-invocation',agentExecutionAvailable:false};
} else {
  try {
    const config=loadCodingPlanConfig(args[3]);
    const preflight=planCodingDryRun(config);
    if(preflight.status!=='PASS') {
      result={...preflight,agentExecutionAvailable:false};
    } else if(!CODING_RUNNER_LIVE_ENABLED) {
      result={status:'BLOCKED',reason:'coding-live-execution-disabled',preflightStatus:'PASS',
        agentExecutionAvailable:false};
    } else {
      result=await runApprovedCodingTask(config,{copilotPath:args[5]});
    }
  } catch(error) {
    result=blockedCodingConfig(error);
  }
}
console.log(JSON.stringify(result));
process.exitCode=result.status==='COMPLETED'?0:1;
