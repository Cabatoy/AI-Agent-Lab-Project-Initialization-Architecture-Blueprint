// @vitest-environment node
import http from 'node:http';
import { once } from 'node:events';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { createServer, type ViteDevServer } from 'vite';
import { apiTarget, localApiProxy } from '../vite.proxy';

describe('local read-only development proxy', () => {
  let upstream: http.Server;
  let vite: ViteDevServer;
  let origin: string;
  const paths: string[] = [];
  beforeAll(async () => {
    upstream = http.createServer((req, res) => {
      paths.push(`${req.method} ${req.url}`);
      res.writeHead(200, { 'content-type': 'application/json' }).end('[]');
    });
    upstream.listen(0, '127.0.0.1');
    await once(upstream, 'listening');
    const address = upstream.address();
    if (!address || typeof address === 'string') throw new Error('No upstream port');
    vite = await createServer({ configFile: false, plugins: [localApiProxy(`http://127.0.0.1:${address.port}`)],
      server: { host: '127.0.0.1', port: 0, cors: false, hmr: false } });
    await vite.listen();
    const bound = vite.httpServer?.address();
    if (!bound || typeof bound === 'string') throw new Error('No proxy port');
    origin = `http://127.0.0.1:${bound.port}`;
  });
  afterAll(async () => {
    await vite?.close();
    if (upstream) await new Promise<void>((resolve, reject) => upstream.close(error => error ? reject(error) : resolve()));
  });
  it('allows only the specified GET paths', async () => {
    expect((await fetch(`${origin}/api/tasks`)).status).toBe(200);
    expect((await fetch(`${origin}/api/tasks`, { method: 'POST' })).status).toBe(403);
    expect((await fetch(`${origin}/api/tasks/01234567-89ab-cdef-0123-456789abcdef/approve`)).status).toBe(403);
    expect((await fetch(`${origin}/api/tasks?x=1`)).status).toBe(403);
    expect(paths).toEqual(['GET /tasks']);
  });
  it('rejects non-loopback, credentials, paths, and TLS targets', () => {
    for (const target of ['http://localhost:8080', 'http://192.168.1.1:8080', 'http://user:pass@127.0.0.1:8080', 'http://127.0.0.1:8080/x', 'https://127.0.0.1']) {
      expect(() => apiTarget(target)).toThrow();
    }
  });
  it('does not grant cross-origin access', async () => {
    const response = await fetch(`${origin}/api/health`, { headers: { origin: 'https://attacker.example' } });
    expect(response.headers.get('access-control-allow-origin')).toBeNull();
  });
});
