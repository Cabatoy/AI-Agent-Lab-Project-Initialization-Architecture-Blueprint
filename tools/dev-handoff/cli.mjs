import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { discover, HandoffError, prepare, readBaseline, readReviewedInput, reconcile, validateInput, writeInbox } from './handoff.mjs';

export async function main(args) {
  const usage = 'Disabled by default. prepare --enable-manual --input <reviewed.json> --approved-sha256 <digest> --installation <VS Code app directory> --out <existing external directory>';
  if (args.length === 0 || args[0] === '--help') return usage;
  if (args[0] !== 'prepare' || args[1] !== '--enable-manual') throw new HandoffError('DISABLED');
  const values = new Map();
  for (let i = 2; i < args.length; i += 2) {
    if (!['--input', '--approved-sha256', '--installation', '--out'].includes(args[i]) ||
      typeof args[i + 1] !== 'string' || args[i + 1].startsWith('--') || values.has(args[i])) throw new HandoffError('INVALID_ARGUMENTS');
    values.set(args[i], args[i + 1]);
  }
  if (values.size !== 4) throw new HandoffError('INVALID_ARGUMENTS');
  let bytes;
  try { bytes = readFileSync(resolve(values.get('--input'))); } catch { throw new HandoffError('INPUT_UNREADABLE'); }
  const input = readReviewedInput(bytes, values.get('--approved-sha256'));
  validateInput(input);
  if (input.provenance !== 'operator-export') throw new HandoffError('SYNTHETIC_LIVE_HANDOFF_FORBIDDEN');
  const capability = discover(resolve(values.get('--installation')));
  const baseline = readBaseline(input.worktree);
  const github = await reconcile(input);
  // Re-read after network I/O; no stale baseline or expired approval may be silently carried forward.
  const currentBaseline = readBaseline(input.worktree);
  if (JSON.stringify(baseline) !== JSON.stringify(currentBaseline)) throw new HandoffError('BASELINE_CHANGED');
  const report = prepare(input, { baseline: currentBaseline, github, capability });
  return `MANUAL_DRAFT_ONLY ${writeInbox(resolve(values.get('--out')), report, input.worktree)}`;
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  try { console.log(await main(process.argv.slice(2))); }
  catch (error) {
    console.error(error instanceof HandoffError ? error.message : 'UNEXPECTED_FAILURE');
    process.exitCode = 2;
  }
}
