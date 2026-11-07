import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { isDeepStrictEqual } from 'node:util';

const sourceMoves = [
  ['agent-lab/control-plane', 'src/ControlPlane'],
  ['agent-lab/agent-worker', 'src/AgentWorker'],
  ['agent-lab/hic-native-host', 'src/NativeHost'],
  ['agent-lab/hic-web-ui', 'src/WebUI'],
  ['tools/agent-bridge', 'src/AgentBridge'],
];
const testRoots = [
  'tests/AgentBridge.Tests',
  'tests/AgentWorker.Tests',
  'tests/ControlPlane.Api.Tests',
  'tests/Hic.NativeHost.Tests',
];
const allowedSecurityAdditions = new Set([
  'layout-only-guard.mjs',
  'layout-only-guard.test.mjs',
  'refuse-v3-on-layout.sh',
]);
const sample = 'sample-input.json';

function git(repo, args, allowedStatuses = [0]) {
  const result = spawnSync('git', ['-C', repo, ...args], {
    encoding: 'utf8',
    maxBuffer: 16 * 1024 * 1024,
    timeout: 30000,
    windowsHide: true,
  });
  if (result.error) {
    throw new Error(`git ${args[0]} failed: ${result.error.message}`);
  }
  if (!allowedStatuses.includes(result.status)) {
    throw new Error(`git ${args[0]} exited ${result.status}: ${result.stderr.trim()}`);
  }
  return result;
}

function tree(repo, revision, root) {
  const output = git(repo, ['ls-tree', '-r', '-z', revision, '--', root]).stdout;
  const entries = new Map();
  for (const record of output.split('\0').filter(Boolean)) {
    const match = /^([0-7]{6}) (blob|commit) ([0-9a-f]{40})\t(.+)$/.exec(record);
    if (!match || !match[4].startsWith(`${root}/`)) {
      throw new Error(`Unexpected Git tree record under ${root}`);
    }
    entries.set(match[4].slice(root.length + 1), `${match[1]} ${match[2]} ${match[3]}`);
  }
  return entries;
}

function blob(repo, revision, file) {
  return git(repo, ['show', `${revision}:${file}`]).stdout;
}

function rewriteProjectReferences(content) {
  for (const [oldRoot, newRoot] of sourceMoves) {
    content = content.replaceAll(`../../${oldRoot}/`, `../../${newRoot}/`);
    content = content.replaceAll(
      `..\\..\\${oldRoot.replaceAll('/', '\\')}\\`,
      `..\\..\\${newRoot.replaceAll('/', '\\')}\\`,
    );
  }
  return content;
}

function compareTrees(repo, base, head, oldRoot, newRoot, errors, options = {}) {
  const before = tree(repo, base, oldRoot);
  const after = tree(repo, head, newRoot);
  if (before.size === 0 || after.size === 0) {
    errors.push(`Missing tracked files in ${oldRoot} or ${newRoot}`);
    return;
  }
  const files = new Set([...before.keys(), ...after.keys()]);
  for (const file of [...files].sort()) {
    if (options.allowAdded?.has(file) && !before.has(file)) continue;
    if (!before.has(file) || !after.has(file)) {
      errors.push(`Tracked file added or removed: ${oldRoot}/${file}`);
      continue;
    }
    const oldEntry = before.get(file);
    const newEntry = after.get(file);
    if (oldEntry === newEntry) continue;
    if (oldEntry.slice(0, 6) !== newEntry.slice(0, 6)) {
      errors.push(`File mode changed: ${oldRoot}/${file}`);
      continue;
    }
    if (options.projectReferences && file.endsWith('.csproj')) {
      const expected = rewriteProjectReferences(blob(repo, base, `${oldRoot}/${file}`));
      if (expected === blob(repo, head, `${newRoot}/${file}`)) continue;
    }
    if (options.syntheticSample && file === sample) {
      const expected = JSON.parse(blob(repo, base, `${oldRoot}/${file}`));
      const actual = JSON.parse(blob(repo, head, `${newRoot}/${file}`));
      let updated = 0;
      for (const entry of expected.changedFiles ?? []) {
        if (entry.path === 'tools/agent-bridge/AgentBridge.csproj') {
          entry.path = 'src/AgentBridge/AgentBridge.csproj';
          updated++;
        }
      }
      if (updated === 1 && isDeepStrictEqual(expected, actual)) continue;
    }
    errors.push(`Protected content changed: ${oldRoot}/${file}`);
  }
}

export function inspectLayout(repo, base, head) {
  if (!/^[0-9a-f]{40}$/.test(base) || !/^[0-9a-f]{40}$/.test(head)) {
    throw new Error('Both revisions must be full lowercase Git SHA-1 values');
  }
  const ancestor = git(repo, ['merge-base', '--is-ancestor', base, head], [0, 1]);
  if (ancestor.status !== 0) {
    throw new Error('The PR head does not contain its current base commit');
  }

  const errors = [];
  for (const [oldRoot, newRoot] of sourceMoves) {
    compareTrees(repo, base, head, oldRoot, newRoot, errors, {
      syntheticSample: oldRoot === 'tools/agent-bridge',
    });
  }
  for (const root of testRoots) {
    compareTrees(repo, base, head, root, root, errors, { projectReferences: true });
  }
  compareTrees(
    repo, base, head, 'tests/security-contracts', 'tests/security-contracts', errors,
    { allowAdded: allowedSecurityAdditions },
  );
  compareTrees(
    repo, base, head, 'deploy/hic-managed/migrations', 'deploy/hic-managed/migrations', errors,
  );

  const allowedRoots = sourceMoves.map(([, newRoot]) => `${newRoot.slice(4)}/`);
  for (const file of tree(repo, head, 'src').keys()) {
    if (!allowedRoots.some((root) => file.startsWith(root))) {
      errors.push(`Unexpected source outside the five relocated projects: src/${file}`);
    }
  }
  return errors;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    if (process.argv.length !== 4) {
      throw new Error('Usage: node layout-only-guard.mjs <base-sha> <head-sha>');
    }
    const problems = inspectLayout(process.cwd(), process.argv[2], process.argv[3]);
    if (problems.length) {
      for (const problem of problems) console.error(`FAIL: ${problem}`);
      process.exitCode = 1;
    } else {
      console.log('PASS: protected project, migration and test contents are layout-only; v3 was NOT executed.');
    }
  } catch (error) {
    console.error(`FAIL: ${error.message}`);
    process.exitCode = 1;
  }
}
