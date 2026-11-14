import { relayAndPublish } from './ack.mjs';

const args=process.argv.slice(2);
const once=args.includes('--once'),watch=args.includes('--watch');
const authorized=args.includes('--enable-read-only') && args.includes('--enable-ack-publish');
if(!authorized || once===watch || args.length!==3){
  console.error('Usage: node src/ack-cli.mjs --enable-read-only --enable-ack-publish (--once | --watch)');
  process.exitCode=2;
} else {
  do {
    try{
      const results=await relayAndPublish({token:process.env.HIC_DEV_ACK_TOKEN});
      console.log(JSON.stringify({status:'synthetic-ack-only',observed:results.length,published:results.filter(x=>x.status==='published-synthetic-ack').length,executionAllowed:false}));
    }catch(error){
      const allow=/^(invalid-[a-z-]+|unsafe-[a-z-]+|ack-[a-z-]+|missing-scoped-token|github-[a-z-]+|response-too-large|size-mismatch|order-digest-mismatch|expired|not-review-only|duplicate-index-entry|approval-not-allowed|inbox-raced)$/;
      const reason=typeof error?.message==='string'&&allow.test(error.message)?error.message:'relay-blocked';
      console.error(JSON.stringify({status:'blocked',reason,executionAllowed:false}));
      if(once) process.exitCode=1;
    }
    if(watch) await new Promise(resolve=>setTimeout(resolve,10*60*1000));
  }while(watch);
}
