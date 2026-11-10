import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { dirname, extname, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

// Synthetic browser fixture only: never forwards requests to any HIC runtime.
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../dist');
const timestamp = '2026-11-10T12:00:00Z';
const task = {
  id: '01234567-89ab-cdef-0123-456789abcdef',
  title: `Synthetic task ${'long-id-'.repeat(16)}`, description: '<script>untrusted text, not markup</script>',
  status: 'Executing', attemptCount: 1, result: 'Synthetic result', errorMessage: null,
  createdAtUtc: timestamp, updatedAtUtc: timestamp,
};
const diagnostics = {
  action: 'system.health', checkedAtUtc: timestamp,
  controlPlane: 'ok', postgres: 'ok', redis: 'unavailable', worker: 'not-checked',
};
const reads = [];
const server = createServer(async (req, res) => {
  const url = new URL(req.url ?? '/', 'http://127.0.0.1');
  const json = (body, status = 200) => {
    res.writeHead(status, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
    res.end(JSON.stringify(body));
  };
  if (req.method !== 'GET') { json({ error: 'Synthetic fixture rejects every write' }, 405); return; }
  if (url.pathname === '/__fixture/report') { json(reads); return; }
  if (url.pathname.startsWith('/api/')) {
    reads.push({ method: req.method, path: url.pathname });
    const scenario = new URL(req.headers.referer ?? '/', 'http://127.0.0.1').searchParams.get('scenario');
    if (scenario === 'unauthenticated') { json({ error: 'Synthetic 401' }, 401); return; }
    if (scenario === 'forbidden') { json({ error: 'Synthetic 403' }, 403); return; }
    if (scenario === 'unavailable') { json({ error: 'Synthetic 503' }, 503); return; }
    if (scenario === 'invalid' && url.pathname === '/api/tasks') { json({ tasks: 'not a list' }); return; }
    if (url.pathname === '/api/health') json({ status: 'ok', service: 'control-plane' });
    else if (url.pathname === '/api/tasks') json(scenario === 'empty' ? [] : [task]);
    else if (url.pathname === `/api/tasks/${task.id}`) json(task);
    else if (url.pathname === '/api/plans') json([]);
    else if (url.pathname === '/api/diagnostics/local') json(diagnostics);
    else json({ error: 'No synthetic endpoint' }, 404);
    return;
  }
  const file = resolve(root, `.${decodeURIComponent(url.pathname === '/' ? '/index.html' : url.pathname)}`);
  if (!file.startsWith(`${root}${sep}`)) { json({ error: 'Denied' }, 403); return; }
  try {
    const data = await readFile(file);
    const type = { '.js': 'text/javascript', '.css': 'text/css', '.html': 'text/html' }[extname(file)];
    res.writeHead(200, { 'Content-Type': type ?? 'application/octet-stream', 'Cache-Control': 'no-store' });
    res.end(data);
  } catch (error) {
    if (error.code !== 'ENOENT') { console.error('Synthetic static fixture read failed:', error.code); res.writeHead(500); }
    else res.writeHead(404);
    res.end();
  }
});
server.listen(0, '127.0.0.1', () => {
  console.log(`Synthetic Dashboard fixture (no runtime proxy): http://127.0.0.1:${server.address().port}`);
});
