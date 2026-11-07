import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import { inspectLayout } from './layout-only-guard.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const branch = 'refactor/enterprise-workspace-layout-20261107-223711';
const condition =
  `(github.event_name == 'pull_request' && github.event.pull_request.head.repo.full_name == github.repository && github.head_ref == '${branch}') || ` +
  `(github.event_name == 'workflow_dispatch' && github.ref == 'refs/heads/${branch}')`;
const workflows = new Map([
  ['hic-native-ledger-ci.yml', 'ledger-isolated-postgres'],
  ['hic-ledger-concurrency-ci.yml', 'disposable-postgres-concurrency'],
  ['hic-ledger-crash-ci.yml', 'disposable-postgres-restart-and-crash'],
  ['hic-ledger-races-ci.yml', 'native-revocation-races'],
]);

function command(repo, ...args) {
  const result = spawnSync('git', ['-C', repo, ...args], {
    encoding: 'utf8',
    timeout: 30000,
    windowsHide: true,
  });
  if (result.error || result.status !== 0) {
    throw new Error(`Synthetic git ${args[0]} failed: ${result.error?.message ?? result.stderr}`);
  }
  return result.stdout.trim();
}

function write(repo, file, contents) {
  const target = path.join(repo, ...file.split('/'));
  mkdirSync(path.dirname(target), { recursive: true });
  writeFileSync(target, contents, 'utf8');
}

function commit(repo, message) {
  command(repo, 'add', '-A');
  command(
    repo, 'commit', '-q', '-m', message,
    '-m', 'Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>',
  );
  return command(repo, 'rev-parse', 'HEAD');
}

function jobs(text) {
  const section = text.split(/^jobs:\s*$/m)[1];
  assert.ok(section, 'workflow must define jobs');
  const headers = [...section.matchAll(/^  ([a-z0-9-]+):\s*$/gm)];
  return new Map(headers.map((header, index) => [
    header[1],
    section.slice(header.index, headers[index + 1]?.index ?? section.length),
  ]));
}

test('only identical relocated sources and test contracts qualify for deferral', () => {
  const repo = mkdtempSync(path.join(os.tmpdir(), 'hic-layout-test-'));
  try {
    command(repo, 'init', '-q');
    command(repo, 'config', 'user.name', 'HIC Synthetic Test');
    command(repo, 'config', 'user.email', 'hic-test@example.invalid');
    command(repo, 'config', 'core.autocrlf', 'false');
    const files = {
      'agent-lab/control-plane/Program.cs': '// unchanged control plane\n',
      'agent-lab/control-plane/Migrations/002-execution-ownership.sql': '-- synthetic v2 text only\n',
      'agent-lab/control-plane/Migrations/003-native-run-approval-ledger.sql': '-- synthetic v3 text, never executed\n',
      'agent-lab/agent-worker/Program.cs': '// unchanged worker\n',
      'agent-lab/hic-native-host/Program.cs': '// unchanged native host\n',
      'agent-lab/hic-web-ui/src/App.tsx': '// unchanged web UI\n',
      'tools/agent-bridge/AgentBridge.csproj': '<Project />\n',
      'tools/agent-bridge/sample-input.json': JSON.stringify({
        changedFiles: [{ path: 'tools/agent-bridge/AgentBridge.csproj', change: 'added' }],
        summary: 'synthetic',
      }),
      'tests/security-contracts/native-ledger-contract.sql': '-- synthetic contract, never executed\n',
      'deploy/hic-managed/migrations/000-bootstrap-v2.sql': '-- synthetic bootstrap, never executed\n',
    };
    const projects = [
      ['AgentBridge.Tests', 'tools/agent-bridge', 'src/AgentBridge'],
      ['AgentWorker.Tests', 'agent-lab/agent-worker', 'src/AgentWorker'],
      ['ControlPlane.Api.Tests', 'agent-lab/control-plane', 'src/ControlPlane'],
      ['Hic.NativeHost.Tests', 'agent-lab/hic-native-host', 'src/NativeHost'],
    ];
    for (const [name, oldRoot] of projects) {
      files[`tests/${name}/${name}.csproj`] = `<ProjectReference Include="../../${oldRoot}/x.csproj" />\n`;
      files[`tests/${name}/Fixture.cs`] = '// unchanged test\n';
    }
    for (const [file, contents] of Object.entries(files)) write(repo, file, contents);
    const base = commit(repo, 'test: create synthetic layout base');
    mkdirSync(path.join(repo, 'src'));
    for (const [oldRoot, newRoot] of [
      ['agent-lab/control-plane', 'src/ControlPlane'],
      ['agent-lab/agent-worker', 'src/AgentWorker'],
      ['agent-lab/hic-native-host', 'src/NativeHost'],
      ['agent-lab/hic-web-ui', 'src/WebUI'],
      ['tools/agent-bridge', 'src/AgentBridge'],
    ]) command(repo, 'mv', oldRoot, newRoot);
    for (const [name, oldRoot, newRoot] of projects) {
      write(repo, `tests/${name}/${name}.csproj`, files[`tests/${name}/${name}.csproj`].replace(oldRoot, newRoot));
    }
    write(repo, 'src/AgentBridge/sample-input.json', JSON.stringify({
      changedFiles: [{ path: 'src/AgentBridge/AgentBridge.csproj', change: 'added' }],
      summary: 'synthetic',
    }));
    for (const file of ['layout-only-guard.mjs', 'layout-only-guard.test.mjs', 'refuse-v3-on-layout.sh']) {
      write(repo, `tests/security-contracts/${file}`, '// synthetic new guard\n');
    }
    const good = commit(repo, 'test: relocate synthetic files');
    assert.deepEqual(inspectLayout(repo, base, good), []);

    write(repo, 'src/ControlPlane/Migrations/003-native-run-approval-ledger.sql', '-- changed, not executed\n');
    let head = commit(repo, 'test: change synthetic ledger');
    assert.ok(inspectLayout(repo, base, head).some((item) => item.includes('003-native-run-approval-ledger.sql')));
    write(repo, 'src/ControlPlane/Migrations/003-native-run-approval-ledger.sql', files['agent-lab/control-plane/Migrations/003-native-run-approval-ledger.sql']);
    commit(repo, 'test: restore synthetic ledger');

    write(repo, 'src/NativeHost/Program.cs', '// changed native boundary\n');
    head = commit(repo, 'test: change synthetic native source');
    assert.ok(inspectLayout(repo, base, head).some((item) => item.includes('hic-native-host/Program.cs')));
    write(repo, 'src/NativeHost/Program.cs', files['agent-lab/hic-native-host/Program.cs']);
    commit(repo, 'test: restore synthetic native source');

    write(repo, 'tests/security-contracts/native-ledger-contract.sql', '-- changed test contract\n');
    head = commit(repo, 'test: change synthetic contract');
    assert.ok(inspectLayout(repo, base, head).some((item) => item.includes('native-ledger-contract.sql')));
    write(repo, 'tests/security-contracts/native-ledger-contract.sql', files['tests/security-contracts/native-ledger-contract.sql']);
    commit(repo, 'test: restore synthetic contract');

    const projectPath = 'tests/ControlPlane.Api.Tests/ControlPlane.Api.Tests.csproj';
    write(repo, projectPath, '<Project><Target Name="Unexpected" /></Project>\n');
    head = commit(repo, 'test: change synthetic test project');
    assert.ok(inspectLayout(repo, base, head).some((item) => item.includes(projectPath)));
    write(repo, projectPath, files[projectPath].replace('agent-lab/control-plane', 'src/ControlPlane'));
    commit(repo, 'test: restore synthetic test project');

    write(repo, 'src/Unexpected/Program.cs', '// unreviewed source\n');
    head = commit(repo, 'test: add unexpected synthetic source');
    assert.ok(inspectLayout(repo, base, head).some((item) => item.includes('src/Unexpected/Program.cs')));
    assert.throws(() => inspectLayout(repo, good, base), /does not contain its current base/);
    assert.throws(() => inspectLayout(repo, 'invalid', head), /full lowercase Git SHA-1/);
  } finally {
    rmSync(repo, { recursive: true, force: true });
  }
});

test('all four ledger jobs have complementary branch gates and a first-step v3 refusal', () => {
  for (const [file, ledgerJob] of workflows) {
    const text = readFileSync(path.join(root, '.github', 'workflows', file), 'utf8').replaceAll('\r\n', '\n');
    const sections = jobs(text);
    assert.deepEqual(new Set(sections.keys()), new Set(['refactor-v3-deferred', ledgerJob]), file);
    const deferred = sections.get('refactor-v3-deferred');
    const ledger = sections.get(ledgerJob);
    assert.ok(deferred.includes(`    if: \${{ ${condition} }}`), `${file}: incorrect deferral predicate`);
    assert.ok(ledger.includes(`    if: \${{ !(${condition}) }}`), `${file}: incorrect ledger predicate`);
    assert.match(deferred, /uses: \.\/\.github\/workflows\/hic-layout-only-ci\.yml/);
    assert.match(deferred, /base-sha: \$\{\{ github\.event\.pull_request\.base\.sha \|\| '' \}\}/);
    assert.match(deferred, /head-sha: \$\{\{ github\.event\.pull_request\.head\.sha \|\| '' \}\}/);
    const guard = ledger.indexOf('run: bash tests/security-contracts/refuse-v3-on-layout.sh');
    const firstRun = ledger.indexOf('run:');
    const checkout = ledger.indexOf('uses: actions/checkout@v4');
    assert.ok(checkout >= 0 && checkout < guard && firstRun === guard, `${file}: v3 refusal must be the first run step`);
    assert.match(ledger, /HIC_HEAD_REPOSITORY: \$\{\{ github\.event\.pull_request\.head\.repo\.full_name \}\}/);
  }
});

test('actual shell guard blocks the protected PR and dispatch, including missing identity', () => {
  const script = path.join(root, 'tests', 'security-contracts', 'refuse-v3-on-layout.sh');
  const repository = 'owner/repo';
  const cases = [
    ['same-repository refactor PR', 'pull_request', branch, repository, repository, '', 1],
    ['case-insensitive same repository', 'pull_request', branch.toUpperCase(), repository, repository.toUpperCase(), '', 1],
    ['missing head repository', 'pull_request', branch, repository, '', '', 1],
    ['missing PR branch', 'pull_request', '', repository, repository, '', 1],
    ['other same-repository PR', 'pull_request', 'feature/other', repository, repository, '', 0],
    ['same-named fork branch', 'pull_request', branch, repository, 'fork/repo', '', 0],
    ['refactor manual dispatch', 'workflow_dispatch', '', repository, '', `refs/heads/${branch}`, 1],
    ['other manual dispatch', 'workflow_dispatch', '', repository, '', 'refs/heads/feature/other', 0],
    ['missing dispatch ref', 'workflow_dispatch', '', repository, '', '', 1],
    ['unexpected event', 'push', branch, repository, repository, `refs/heads/${branch}`, 1],
  ];
  for (const [label, event, head, repo, headRepo, ref, expected] of cases) {
    const result = spawnSync('bash', [script], {
      encoding: 'utf8',
      timeout: 15000,
      windowsHide: true,
      env: {
        ...process.env,
        GITHUB_EVENT_NAME: event,
        GITHUB_HEAD_REF: head,
        GITHUB_REPOSITORY: repo,
        HIC_HEAD_REPOSITORY: headRepo,
        GITHUB_REF: ref,
      },
    });
    if (result.error) throw result.error;
    assert.equal(result.status, expected, `${label}: ${result.stderr}`);
  }
});

test('repository workflows cannot trigger on push or invoke an unguarded ledger entry point', () => {
  const workflowDir = path.join(root, '.github', 'workflows');
  const expectedEvents = new Map([
    ...[...workflows.keys()].map((file) => [file, ['pull_request', 'workflow_dispatch']]),
    ['hic-control-plane-ui-ci.yml', ['pull_request']],
    ['hic-managed-runtime-smoke.yml', ['pull_request', 'workflow_dispatch']],
    ['hic-runtime-boundary-ci.yml', ['pull_request', 'workflow_dispatch']],
    ['hic-layout-only-ci.yml', ['workflow_call']],
  ]);
  const actual = readdirSync(workflowDir).filter((file) => file.endsWith('.yml')).sort();
  assert.deepEqual(actual, [...expectedEvents.keys()].sort());
  for (const file of actual) {
    const text = readFileSync(path.join(workflowDir, file), 'utf8').replaceAll('\r\n', '\n');
    const on = /^on:\s*\n([\s\S]*?)(?=^[a-z][a-z-]*:\s*$)/m.exec(text);
    assert.ok(on, `${file}: missing event block`);
    const events = [...on[1].matchAll(/^  ([a-z_]+):\s*$/gm)].map((match) => match[1]);
    assert.deepEqual(events, expectedEvents.get(file), `${file}: unexpected event`);
    if (!workflows.has(file)) {
      assert.doesNotMatch(text, /003-native-run-approval-ledger\.sql|native-ledger-(concurrency|crash|races)\.(sh|py)/);
    }
  }
  const reusable = readFileSync(path.join(workflowDir, 'hic-layout-only-ci.yml'), 'utf8');
  assert.doesNotMatch(reusable, /\b(docker|podman|psql)\b/i);
  const bootstrap = readFileSync(path.join(root, 'deploy', 'hic-managed', 'migrations', '000-bootstrap-v2.sql'), 'utf8');
  const includes = [...bootstrap.matchAll(/^\s*\\(?:i|include)\s+(\S+)/gm)].map((match) => match[1]);
  assert.deepEqual(includes, ['/control-plane-migrations/002-execution-ownership.sql']);
  assert.doesNotMatch(bootstrap, /003-native-run-approval-ledger\.sql/);
});
