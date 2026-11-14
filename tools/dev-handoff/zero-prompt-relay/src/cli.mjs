import { pollOnce } from './relay.mjs';

const args=process.argv.slice(2);
const enabled=args.includes('--enable-read-only'), once=args.includes('--once'), watch=args.includes('--watch');
if(!enabled || once===watch || args.length!==2) {
  console.error('Usage: node src/cli.mjs --enable-read-only (--once | --watch)');
  process.exitCode=2;
} else {
  do {
    try {
      const receipts=await pollOnce();
      console.log(JSON.stringify({status:'read-only',observed:receipts.length,newlySeen:receipts.filter(x=>x.result==='seen-untrusted').length,executionAllowed:false}));
    } catch(error) {
      const reason=typeof error?.message==='string' && /^(?:invalid-[a-z-]+|not-review-only|expired|expired-or-invalid|duplicate-index-entry|size-mismatch|github-read-failed|github-rate-limited-or-forbidden|response-too-large|unsafe-[a-z-]+|unreadable-ack|ack-replay-conflict|order-digest-mismatch|approval-not-allowed|order-id-mismatch)$/.test(error.message) ? error.message : 'provider-or-filesystem-error';
      console.error(JSON.stringify({status:'blocked',reason,executionAllowed:false}));
      if(once) process.exitCode=1;
    }
    if(watch) await new Promise(resolve=>setTimeout(resolve,10*60*1000));
  }while(watch);
}
