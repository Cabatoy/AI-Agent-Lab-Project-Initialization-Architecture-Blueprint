import test from 'node:test';
import assert from 'node:assert/strict';
import { globSync, readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../', import.meta.url));

test('npm offline test discovery includes test files and excludes fixture helpers', () => {
  const { scripts } = JSON.parse(readFileSync(new URL('../package.json', import.meta.url), 'utf8'));
  assert.equal(scripts.test, 'npm run check');

  const command = /^node --test (\S+)$/.exec(scripts.check);
  assert.ok(command, 'check must explicitly select test files');
  const files = pattern => globSync(pattern, { cwd: root })
    .map(path => path.replaceAll('\\', '/')).sort();
  const selected = files(command[1]);
  assert.ok(selected.length > 0, 'test discovery must not be empty');
  assert.deepEqual(selected, files('test/**/*.test.mjs'));
  assert.ok(selected.every(path => !path.startsWith('test/fixtures/')));
});
