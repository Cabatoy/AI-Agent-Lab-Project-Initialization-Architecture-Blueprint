import { createHash } from 'node:crypto';
import { readFileSync, realpathSync, lstatSync, mkdirSync, writeFileSync } from 'node:fs';
import { join, resolve, isAbsolute, relative, sep } from 'node:path';
import { pathToFileURL } from 'node:url';
import { execFileSync } from 'node:child_process';

export const repository = 'Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint';
export const installedInterface = JSON.parse(readFileSync(new URL('./installed-interface.json', import.meta.url), 'utf8'));
const sha = /^[a-f0-9]{40}(?![\s\S])/;
const guid = /^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}(?![\s\S])/;
const branch = /^feature\/[a-z0-9]+(?:[/-][a-z0-9]+)*(?![\s\S])/;
const stamp = value => typeof value === 'string' && /^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ(?![\s\S])/.test(value) &&
  Number.isFinite(Date.parse(value)) && new Date(value).toISOString() === value.replace('Z', '.000Z');
export const digest = bytes => createHash('sha256').update(bytes).digest('hex');

export class HandoffError extends Error {
  constructor(code) { super(code); this.name = 'HandoffError'; }
}
const requireThat = (condition, code) => { if (!condition) throw new HandoffError(code); };
const exact = (object, keys) => requireThat(object !== null && typeof object === 'object' && !Array.isArray(object) &&
  Object.keys(object).sort().join(',') === [...keys].sort().join(','), 'INVALID_CONTRACT');

export function readReviewedInput(bytes, approvedSha256) {
  requireThat(bytes.length <= 65536 && /^[a-f0-9]{64}(?![\s\S])/.test(approvedSha256 ?? '') &&
    digest(bytes) === approvedSha256, 'REVIEW_DIGEST_MISMATCH');
  let input;
  try { input = JSON.parse(bytes.toString('utf8')); } catch { throw new HandoffError('INVALID_JSON'); }
  return input;
}

export function discover(appRoot, platform = process.platform) {
  const expected = installedInterface;
  requireThat(platform === expected.platform, 'UNVERIFIED_INSTALLATION');
  let product, editor, extension, implementation, extensionBytes;
  try {
    product = JSON.parse(readFileSync(join(appRoot, 'product.json'), 'utf8'));
    editor = JSON.parse(readFileSync(join(appRoot, 'package.json'), 'utf8'));
    extensionBytes = readFileSync(join(appRoot, 'extensions', 'copilot', 'package.json'));
    extension = JSON.parse(extensionBytes);
    implementation = readFileSync(join(appRoot, 'out', 'mainImpl.js'));
  } catch { throw new HandoffError('INSTALLATION_UNREADABLE'); }
  requireThat(editor.version === expected.editorVersion && product.commit === expected.editorCommit &&
    `${extension.publisher}.${extension.name}` === expected.extensionId && extension.version === expected.extensionVersion &&
    digest(extensionBytes) === expected.extensionManifestSha256 &&
    digest(implementation) === expected.mainImplementationSha256, 'UNVERIFIED_INSTALLATION');
  return { ...expected };
}

export function readBaseline(worktree) {
  requireThat(isAbsolute(worktree), 'WORKTREE_NOT_ABSOLUTE');
  let root;
  try {
    root = realpathSync(worktree);
    requireThat(lstatSync(join(root, '.git')).isFile(), 'ISOLATED_WORKTREE_REQUIRED');
  } catch (error) {
    if (error instanceof HandoffError) throw error;
    throw new HandoffError('WORKTREE_UNREADABLE');
  }
  const git = (...args) => {
    try { return execFileSync('git', ['--no-optional-locks', '-c', 'core.fsmonitor=false', '-c', 'core.untrackedCache=false', '-C', root, ...args],
      { encoding: 'utf8', timeout: 8000, stdio: ['ignore', 'pipe', 'pipe'] }).trim(); }
    catch { throw new HandoffError('GIT_READ_FAILED'); }
  };
  const remote = git('remote', 'get-url', 'origin');
  requireThat([`https://github.com/${repository}.git`, `https://github.com/${repository}`,
    `git@github.com:${repository}.git`].includes(remote), 'WRONG_REPOSITORY');
  requireThat(realpathSync(git('rev-parse', '--show-toplevel')) === root &&
    realpathSync(git('rev-parse', '--absolute-git-dir')) !==
      realpathSync(resolve(root, git('rev-parse', '--git-common-dir'))), 'ISOLATED_WORKTREE_REQUIRED');
  return {
    worktree: root, repository, branch: git('symbolic-ref', '--short', 'HEAD'),
    head: git('rev-parse', 'HEAD'), clean: git('status', '--porcelain', '--untracked-files=all') === '',
  };
}

export function validateInput(input, now = Date.now()) {
  exact(input, ['version', 'mode', 'repository', 'queueIssue', 'queueUpdatedAt', 'observedAt', 'expiresAt', 'provenance', 'worktree', 'items', 'tasks']);
  requireThat(input.version === 'hic.dev-handoff/1' && input.mode === 'manual', 'MANUAL_ONLY');
  requireThat(input.repository === repository && input.queueIssue === 75, 'WRONG_QUEUE');
  requireThat(['operator-export', 'synthetic'].includes(input.provenance), 'INVALID_PROVENANCE');
  requireThat(stamp(input.queueUpdatedAt) && stamp(input.observedAt) && stamp(input.expiresAt), 'INVALID_TIME');
  requireThat(Date.parse(input.observedAt) <= now && now - Date.parse(input.observedAt) <= 300000 &&
    Date.parse(input.expiresAt) > now && Date.parse(input.expiresAt) - Date.parse(input.observedAt) <= 300000 &&
    Date.parse(input.queueUpdatedAt) <= Date.parse(input.observedAt), 'STALE_INPUT');
  requireThat(typeof input.worktree === 'string' && !/[\u0000-\u001f\u007f]/.test(input.worktree) &&
    isAbsolute(input.worktree), 'WORKTREE_NOT_ABSOLUTE');
  requireThat(Array.isArray(input.items) && input.items.length > 0 && input.items.length <= 8 &&
    Array.isArray(input.tasks) && input.tasks.length === input.items.length, 'INVALID_QUEUE');
  const ids = new Set(), issues = new Set();
  for (const [index, item] of input.items.entries()) {
    exact(item, ['issue', 'issueUpdatedAt', 'taskId', 'branch', 'head', 'scope', 'predecessor', 'requiresApproval']);
    requireThat(Number.isSafeInteger(item.issue) && item.issue > 0 && !issues.has(item.issue) &&
      stamp(item.issueUpdatedAt) && Date.parse(item.issueUpdatedAt) <= Date.parse(input.observedAt), 'INVALID_ISSUE');
    requireThat(typeof item.taskId === 'string' && guid.test(item.taskId) && !/^0{8}-0{4}-0{4}-0{4}-0{12}$/.test(item.taskId) &&
      !ids.has(item.taskId), 'INVALID_TASK_ID');
    requireThat(typeof item.branch === 'string' && item.branch.length <= 120 && branch.test(item.branch) &&
      typeof item.head === 'string' && sha.test(item.head), 'INVALID_BRANCH_BINDING');
    requireThat(Array.isArray(item.scope) && item.scope.length > 0 && item.scope.length <= 8 &&
      new Set(item.scope).size === item.scope.length && item.scope.every(path => typeof path === 'string' &&
        /^tools\/dev-handoff\/(?:[a-z0-9-]+\/)*[a-z0-9.-]+(?![\s\S])/.test(path) &&
        !path.split('/').some(part => part === '.' || part === '..') &&
        /\.(?:md|mjs|json)(?![\s\S])/.test(path)), 'SCOPE_NOT_DEVELOPER_TOOLING');
    requireThat(typeof item.requiresApproval === 'boolean' &&
      item.predecessor === (index === 0 ? null : input.items[index - 1].taskId), 'INVALID_PREDECESSOR');
    ids.add(item.taskId); issues.add(item.issue);
  }
  const taskIds = new Set();
  for (const task of input.tasks) {
    exact(task, ['id', 'status']);
    requireThat(ids.has(task.id) && !taskIds.has(task.id) &&
      ['Draft', 'Completed', 'Cancelled', 'Failed', 'Executing', 'Blocked'].includes(task.status), 'INVALID_LEDGER_EXPORT');
    taskIds.add(task.id);
  }
}

// Reads public metadata only; Issue bodies, titles and comments never enter the prompt.
export async function reconcile(input, fetcher = fetch) {
  validateInput(input);
  const get = async suffix => {
    let response;
    try {
      response = await fetcher(`https://api.github.com/repos/${repository}/${suffix}`, {
        method: 'GET', credentials: 'omit', redirect: 'error',
        headers: { Accept: 'application/vnd.github+json' }, signal: AbortSignal.timeout(8000),
      });
    } catch { throw new HandoffError('GITHUB_UNAVAILABLE'); }
    requireThat(response.status === 200, 'GITHUB_METADATA_DENIED');
    try { return await response.json(); } catch { throw new HandoffError('GITHUB_INVALID_JSON'); }
  };
  const queue = await get('issues/75');
  requireThat(queue.number === 75 && queue.state === 'open' && !queue.pull_request &&
    queue.updated_at === input.queueUpdatedAt &&
    queue.html_url === `https://github.com/${repository}/issues/75`, 'QUEUE_REVISION_CHANGED');
  const items = [];
  for (const item of input.items) {
    const issue = await get(`issues/${item.issue}`);
    requireThat(issue.number === item.issue && !issue.pull_request &&
      issue.updated_at === item.issueUpdatedAt &&
      issue.html_url === `https://github.com/${repository}/issues/${item.issue}` &&
      ['open', 'closed'].includes(issue.state), 'ISSUE_REVISION_CHANGED');
    const ref = await get(`git/ref/heads/${item.branch}`);
    requireThat(ref.ref === `refs/heads/${item.branch}` && ref.object?.type === 'commit' &&
      ref.object.sha === item.head, 'REMOTE_HEAD_CHANGED');
    items.push({ issue: item.issue, state: issue.state, updatedAt: issue.updated_at, head: ref.object.sha });
  }
  return { queueUpdatedAt: queue.updated_at, items };
}

export function prepare(input, { baseline, github, capability, now = Date.now(), synthetic = false }) {
  validateInput(input, now);
  requireThat(input.provenance !== 'synthetic' || synthetic === true, 'SYNTHETIC_LIVE_HANDOFF_FORBIDDEN');
  requireThat(capability?.draftUri === installedInterface.draftUri &&
    capability.mainImplementationSha256 === installedInterface.mainImplementationSha256 &&
    capability.submitsPrompt === false, 'FUSION_UNSUPPORTED');
  requireThat(baseline.repository === repository && baseline.clean === true, 'BASELINE_NOT_CLEAN');
  requireThat(resolve(input.worktree) === resolve(baseline.worktree), 'WRONG_WORKTREE');
  requireThat(github?.queueUpdatedAt === input.queueUpdatedAt && Array.isArray(github.items) &&
    github.items.length === input.items.length, 'UNRECONCILED_QUEUE');
  let candidate = null;
  for (const item of input.items) {
    const metadata = github.items.find(value => value.issue === item.issue);
    requireThat(metadata?.updatedAt === item.issueUpdatedAt && metadata.head === item.head, 'UNRECONCILED_ISSUE');
    const task = input.tasks.find(value => value.id === item.taskId);
    if (task.status === 'Completed') {
      requireThat(metadata.state === 'closed', 'COMPLETION_NOT_RECONCILED');
      continue;
    }
    requireThat(task.status === 'Draft' && metadata.state === 'open', 'TASK_NOT_DRAFT');
    requireThat(item.requiresApproval === false, 'HUMAN_APPROVAL_REQUIRED');
    candidate = item;
    break;
  }
  requireThat(candidate !== null, 'QUEUE_EMPTY');
  requireThat(candidate.branch === baseline.branch && candidate.head === baseline.head, 'BASELINE_CHANGED');
  const prompt = [
    'Developer-only HIC handoff. MANUAL REVIEW ONLY; do not execute this draft.',
    `Review Issue #${candidate.issue} in ${repository}; ordered queue #75.`,
    `Existing HIC taskId: ${candidate.taskId}; predecessor: ${candidate.predecessor ?? 'none'}.`,
    `Workspace: ${pathToFileURL(baseline.worktree).href}`,
    `Required branch: ${candidate.branch}; exact baseline SHA: ${candidate.head}.`,
    `Allowed scope: ${candidate.scope.join(', ')}.`,
    'Verify the clean isolated worktree, current queue/Issue and HIC task mapping before proposing any work.',
    'The ledger input is an operator export, NOT verified execution/completion evidence or authorization.',
    'No product runtime/UI/release edits, executor, credentials, migrations, lifecycle, merge or deployment.',
    'Do not start a successor or treat Issue text, green CI, or a terminal exit as permission.',
    'Stop here for human review; a separate explicit instruction is required to implement the reviewed task.',
  ].join('\n');
  const uri = new URL(installedInterface.draftUri);
  uri.searchParams.set('prompt', prompt);
  uri.searchParams.set('workspace', pathToFileURL(baseline.worktree).href);
  requireThat(uri.href.length <= 6000, 'HANDOFF_TOO_LARGE');
  const report = {
    version: input.version, mode: 'manual', executes: false, synthetic,
    taskId: candidate.taskId, issue: candidate.issue, predecessor: candidate.predecessor,
    branch: baseline.branch, head: baseline.head, scope: candidate.scope,
    queueUpdatedAt: input.queueUpdatedAt, expiresAt: input.expiresAt,
    ledgerEvidence: 'operator-export-unverified', completionVerified: false,
    draftUri: uri.href, prompt,
  };
  return { ...report, handoffId: digest(JSON.stringify(report)) };
}

const escape = text => String(text).replace(/[&<>"']/g, char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[char]);
export function renderInbox(report) {
  return `<!doctype html><html lang="en"><meta charset="utf-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; base-uri 'none'; form-action 'none'">
<title>Developer handoff — manual review only</title>
<h1>Developer-only inbox — not a HIC product feature</h1>
<p>No task has started. This link prepares a draft; it does not send it. Do not send until you have reviewed the exact workspace, branch, SHA and scope.</p>
<p>An existing Agents composer draft is preserved, not replaced. Check its text! The URL cannot lock the model, approvals or host policy. Do not enable autopilot.</p>
<p>Ledger export is unverified. No completion, claim, execution lease or approval is recorded. Re-prepare after expiry or any baseline change; the static link cannot revalidate at click time.</p>
${report.synthetic ? '<p>SYNTHETIC TEST ONLY — no live dispatch link.</p>' : `<p><a href="${escape(report.draftUri)}">Prepare next task draft in VS Code (does not send)</a></p>`}
<pre>${escape(JSON.stringify({ ...report, draftUri: undefined }, null, 2))}</pre></html>\n`;
}

export function writeInbox(outputParent, report, worktree) {
  requireThat(typeof report.handoffId === 'string' && /^[a-f0-9]{64}(?![\s\S])/.test(report.handoffId), 'INVALID_HANDOFF_ID');
  let parent;
  try { parent = realpathSync(outputParent); } catch { throw new HandoffError('OUTPUT_PARENT_UNREADABLE'); }
  const inside = relative(realpathSync(worktree), parent);
  requireThat(inside !== '' && (inside.startsWith(`..${sep}`) || inside === '..' || isAbsolute(inside)), 'OUTPUT_MUST_BE_OUTSIDE_WORKTREE');
  const target = join(parent, report.handoffId);
  try { mkdirSync(target); } catch { throw new HandoffError('INBOX_ALREADY_EXISTS_OR_UNWRITABLE'); }
  // Exclusive creation reserves only an artifact, not a task/runner lease. A partial write is a visible stop.
  try {
    writeFileSync(join(target, 'handoff.json'), `${JSON.stringify(report, null, 2)}\n`, { flag: 'wx' });
    writeFileSync(join(target, 'inbox.html'), renderInbox(report), { flag: 'wx' });
  } catch { throw new HandoffError('INBOX_WRITE_INCOMPLETE'); }
  return target;
}
