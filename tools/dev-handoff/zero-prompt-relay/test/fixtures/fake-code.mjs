// Fake `code` CLI used by offline tests: records the argv and the attached prompt file; never opens VS Code.
import { appendFileSync, readFileSync } from 'node:fs';

const argv=process.argv.slice(2);
const i=argv.indexOf('-a');
const attached=i>=0?readFileSync(argv[i+1],'utf8'):null;
appendFileSync(process.env.FAKE_CODE_LOG,JSON.stringify({argv,attached})+'\n');
process.exitCode=Number(process.env.FAKE_CODE_EXIT??0);
