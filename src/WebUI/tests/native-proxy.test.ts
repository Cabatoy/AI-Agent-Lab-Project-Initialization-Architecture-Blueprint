// @vitest-environment node
import http from 'node:http';
import { once } from 'node:events';
import { afterAll, beforeAll, beforeEach, describe, expect, it } from 'vitest';
import { createServer, type ViteDevServer } from 'vite';
import { localNativeProxy, nativeTarget } from '../vite.native.proxy';

async function listen(server: http.Server): Promise<number> {
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const address = server.address();
  if (!address || typeof address === 'string') throw new Error('No bound port');
  return address.port;
}

async function startProxy(target: string, token: string) {
  const vite = await createServer({
    configFile: false, logLevel: 'silent',
    plugins: [localNativeProxy(target, token)],
    server: { host: '127.0.0.1', port: 0, cors: false, hmr: false },
  });
  await vite.listen();
  const addr = vite.httpServer?.address();
  if (!addr || typeof addr === 'string') throw new Error('No proxy port');
  return { vite, origin: `http://127.0.0.1:${addr.port}` };
}

async function close(server: http.Server) {
  server.closeAllConnections();
  await new Promise<void>(resolve => server.close(() => resolve()));
}

describe('native host bridge', () => {
  let host: http.Server;
  let vite: ViteDevServer;
  let origin: string;
  const received: { url?: string; method?: string; token?: string; body: string }[] = [];

  const post = (payload: string, headers: Record<string, string> = {}) => fetch(`${origin}/api/native/actions`, {
    method: 'POST', body: payload,
    headers: { 'content-type': 'application/json', origin, 'x-hic-request': 'native-action', ...headers },
  });

  beforeAll(async () => {
    host = http.createServer((req, res) => {
      const chunks: Buffer[] = [];
      req.on('data', chunk => chunks.push(chunk));
      req.on('end', () => {
        received.push({ url: req.url, method: req.method,
          token: req.headers['x-hic-native-token'] as string | undefined, body: Buffer.concat(chunks).toString('utf8') });
        res.setHeader('Content-Type', 'application/json').end(JSON.stringify({
          action: 'workspace.inspect', runId: '01234567-89ab-cdef-0123-456789abcdef',
          checkedAtUtc: '2026-11-07T00:00:00Z', workspaceAvailable: true, gitCheckoutFound: true, solutionFound: true,
        }));
      });
    });
    const port = await listen(host);
    ({ vite, origin } = await startProxy(`http://127.0.0.1:${port}`, 'synthetic-native-token'));
  });
  beforeEach(() => { received.length = 0; });
  afterAll(async () => { await vite?.close(); if (host) await close(host); });

  it('forwards exactly one fixed allowlisted request, with the server-side secret', async () => {
    const response = await post('{"action":"workspace.inspect"}', { cookie: 'not-forwarded=1', 'x-forwarded-for': '1.2.3.4' });
    expect(response.status).toBe(200);
    expect(received).toEqual([{ url: '/v1/actions/run', method: 'POST',
      token: 'synthetic-native-token', body: '{"action":"workspace.inspect"}' }]);
  });

  it('rejects injection, extra keys, unsupported methods and forged origins before forwarding', async () => {
    const rejects: Array<[string, Record<string, string>, number]> = [
      ['{"action":"shell.run"}', {}, 400],
      ['{"action":"workspace.inspect","command":"rm -rf"}', {}, 400],
      ['{"action":"workspace.inspect","path":"../private"}', {}, 400],
      ['[]', {}, 400],
      ['{"action":"workspace.inspect"}', { origin: 'https://attacker.example' }, 403],
      ['{"action":"workspace.inspect"}', { 'x-hic-request': 'wrong' }, 403],
      ['{"action":"workspace.inspect"}', { authorization: 'Bearer secret' }, 403],
    ];
    for (const [body, headers, expected] of rejects) {
      expect((await post(body, headers)).status, body).toBe(expected);
    }
    expect((await fetch(`${origin}/api/native/actions`)).status).toBe(403);
    expect((await post('{"action":"workspace.inspect"}', { 'sec-fetch-site': 'cross-site' })).status).toBe(403);
    expect(received).toEqual([]);
  });

  it('fails closed without a configured token', async () => {
    const port = (host.address() as { port: number }).port;
    const other = await startProxy(`http://127.0.0.1:${port}`, '');
    try {
      const response = await fetch(`${other.origin}/api/native/actions`, { method: 'POST',
        headers: { 'content-type': 'application/json', origin: other.origin, 'x-hic-request': 'native-action' },
        body: '{"action":"workspace.inspect"}' });
      expect(response.status).toBe(503);
      expect(received).toEqual([]);
    } finally { await other.vite.close(); }
  });

  it('rejects non-loopback native destinations', () => {
    for (const url of ['https://127.0.0.1:5248', 'http://localhost:5248',
      'http://192.168.0.2:5248', 'http://user:pass@127.0.0.1:5248', 'http://127.0.0.1:5248/path']) {
      expect(() => nativeTarget(url)).toThrow();
    }
  });
});
