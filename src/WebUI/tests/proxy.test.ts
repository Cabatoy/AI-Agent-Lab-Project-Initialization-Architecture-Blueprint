// @vitest-environment node
import http from 'node:http';
import { once } from 'node:events';
import { afterAll, beforeAll, beforeEach, describe, expect, it } from 'vitest';
import { createServer, type ViteDevServer } from 'vite';
import { apiTarget, localApiProxy, type ProxyOptions } from '../vite.proxy';
import { DESCRIPTION_MAX_LENGTH, MAX_REQUEST_BYTES } from '../src/limits';

type Recorded = { method?: string; url?: string; headers: http.IncomingHttpHeaders; body: string };
type Mode = 'ok' | 'error' | 'hang' | 'reset' | 'malformed';

const created = {
  id: '01234567-89ab-cdef-0123-456789abcdef', title: 'Synthetic', description: 'Synthetic request',
  status: 'Draft', attemptCount: 0, result: null, errorMessage: null,
  createdAtUtc: '2026-11-06T12:00:00Z', updatedAtUtc: '2026-11-06T12:00:00Z',
};
const SECRET_MARKER = 'synthetic-private-request-marker';

async function listen(server: http.Server) {
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const address = server.address();
  if (!address || typeof address === 'string') throw new Error('No port');
  return address.port;
}

async function startProxy(target: string, options?: ProxyOptions) {
  const vite = await createServer({ configFile: false, logLevel: 'silent', plugins: [localApiProxy(target, options)],
    server: { host: '127.0.0.1', port: 0, cors: false, hmr: false } });
  await vite.listen();
  const bound = vite.httpServer?.address();
  if (!bound || typeof bound === 'string') throw new Error('No proxy port');
  return { vite, origin: `http://127.0.0.1:${bound.port}`, port: bound.port };
}

const close = (server: http.Server) => new Promise<void>((resolve) => { server.closeAllConnections(); server.close(() => resolve()); });

describe('persistent operator cookie boundary', () => {
  let upstream: http.Server;
  let vite: ViteDevServer;
  let origin: string;
  let target: string;
  const token = 'S'.repeat(43) + '=';
  const received: Recorded[] = [];
  let revoked = false;
  let loginMode = 'ok';
  const headers = () => ({ origin, 'x-hic-request': 'local-runner' });
  const login = () => fetch(`${origin}/api/local-runner/session`, {
    method: 'POST', headers: { ...headers(), 'content-type': 'application/json' },
    body: JSON.stringify({ bootstrapToken: 'R'.repeat(43) + '=' }),
  });
  beforeAll(async () => {
    upstream = http.createServer((req, res) => {
      const chunks: Buffer[] = [];
      req.on('data', chunk => chunks.push(chunk));
      req.on('end', () => {
        received.push({ method: req.method, url: req.url, headers: req.headers, body: Buffer.concat(chunks).toString() });
        if (req.method === 'POST' && req.url === '/local-runner/session') {
          if (loginMode === 'rejected') { res.writeHead(401).end(); return; }
          revoked = false;
          res.writeHead(200, { 'content-type': 'application/json' }).end(JSON.stringify({
            accessToken: loginMode === 'malformed' ? 'not-a-token' : token,
            expiresInSeconds: 28800,
            expiresAtUtc: new Date(Date.now() + (loginMode === 'expired' ? -1000 : 28800000)).toISOString(),
          }));
          return;
        }
        if (req.headers.authorization !== `Bearer ${token}` || revoked) { res.writeHead(401).end(); return; }
        if (req.url === '/local-runner/session/revoke') {
          revoked = true; res.writeHead(200, { 'content-type': 'application/json' }).end('{"revoked":true}'); return;
        }
        res.writeHead(200, { 'content-type': 'application/json' }).end(JSON.stringify({
          actorId: created.id, expiresInSeconds: 27000,
          expiresAtUtc: new Date(Date.now() + 27000000).toISOString(),
        }));
      });
    });
    target = `http://127.0.0.1:${await listen(upstream)}`;
    ({ vite, origin } = await startProxy(target));
  });
  beforeEach(() => { received.length = 0; revoked = false; loginMode = 'ok'; });
  afterAll(async () => { await vite.close(); await close(upstream); });

  it('keeps the secret HttpOnly and restores the same backend identity without another bootstrap', async () => {
    const response = await login();
    expect(response.status).toBe(200);
    const body = await response.json();
    expect(body.accessToken).toBe('cookie-session');
    expect(JSON.stringify(body)).not.toContain(token);
    const setCookie = response.headers.get('set-cookie')!;
    expect(setCookie).toContain('HttpOnly; SameSite=Strict; Max-Age=');
    expect(setCookie).toContain('Path=/api/local-runner');
    expect(setCookie).not.toContain('Domain=');
    const maxAge = Number(/Max-Age=(\d+)/.exec(setCookie)![1]);
    expect(maxAge).toBeGreaterThanOrEqual(28795);
    expect(maxAge).toBeLessThanOrEqual(28800);
    const cookie = setCookie.split(';')[0];
    for (let reopen = 0; reopen < 3; reopen++) {
      const restored = await fetch(`${origin}/api/local-runner/session`, {
        headers: { 'sec-fetch-site': 'same-origin', 'x-hic-request': 'local-runner', cookie },
      });
      expect(restored.status).toBe(200);
      expect((await restored.json()).actorId).toBe(created.id);
      expect(restored.headers.get('set-cookie')).toBeNull();
    }
    expect(received.filter(request => request.method === 'POST')).toHaveLength(1);
    expect(received[0].headers['x-hic-session-mode']).toBe('browser');
    for (const request of received.slice(1)) {
      expect(request.headers.authorization).toBe(`Bearer ${token}`);
      expect(request.headers.cookie).toBeUndefined();
    }
  });

  it('revokes server-side, clears the cookie and rejects replay and expiry', async () => {
    const cookie = (await login()).headers.get('set-cookie')!.split(';')[0];
    const ended = await fetch(`${origin}/api/local-runner/session/revoke`, {
      method: 'POST', headers: { ...headers(), cookie },
    });
    expect(ended.status).toBe(200);
    expect(ended.headers.get('set-cookie')).toContain('Max-Age=0');
    const replay = await fetch(`${origin}/api/local-runner/session`, { headers: { ...headers(), cookie } });
    expect(replay.status).toBe(401);
    expect(replay.headers.get('set-cookie')).toContain('Max-Age=0');
    const missing = await fetch(`${origin}/api/local-runner/session`, { headers: headers() });
    expect(missing.status).toBe(401);
    expect(missing.headers.get('set-cookie')).toContain('Max-Age=0');
    expect(received.map(request => request.url)).toEqual([
      '/local-runner/session', '/local-runner/session/revoke', '/local-runner/session',
    ]);
  });

  it('does not send a cookie across frontend or upstream runtime boundaries', async () => {
    const cookie = (await login()).headers.get('set-cookie')!.split(';')[0];
    const peers = [
      await startProxy(target),
      await startProxy('http://127.0.0.1:1'),
    ];
    try {
      for (const peer of peers) {
        const response = await fetch(`${peer.origin}/api/local-runner/session`, {
          headers: { origin: peer.origin, 'x-hic-request': 'local-runner', cookie },
        });
        expect(response.status).toBe(401);
        expect(response.headers.get('set-cookie')!.split('=')[0]).not.toBe(cookie.split('=')[0]);
      }
      expect(received).toHaveLength(1);
    } finally { await Promise.all(peers.map(peer => peer.vite.close())); }
  });

  it('rejects cookie-authenticated CSRF, navigation, duplicate cookies and revoke arguments', async () => {
    const cookie = (await login()).headers.get('set-cookie')!.split(';')[0];
    for (const overrides of [
      { origin: 'http://attacker.invalid' }, { 'sec-fetch-site': 'cross-site' },
      { 'sec-fetch-site': 'same-site' }, { 'x-hic-request': '' }, { origin: '' },
    ]) {
      const response = await fetch(`${origin}/api/local-runner/session/revoke`, {
        method: 'POST', headers: { ...headers(), cookie, ...overrides },
      });
      expect(response.status).toBe(403);
    }
    expect((await fetch(`${origin}/api/local-runner/session`, { headers: { cookie } })).status).toBe(403);
    expect((await fetch(`${origin}/api/local-runner/session`, {
      headers: { ...headers(), cookie: `${cookie}; ${cookie}` },
    })).status).toBe(401);
    expect((await fetch(`${origin}/api/local-runner/session/revoke`, {
      method: 'POST', headers: { ...headers(), cookie, 'content-type': 'application/json' }, body: '{}',
    })).status).toBe(400);
    expect(received).toHaveLength(1);
  });

  it('never creates a cookie from rejected, expired or malformed issuance', async () => {
    for (const mode of ['rejected', 'expired', 'malformed']) {
      loginMode = mode;
      const response = await login();
      expect(response.status).toBe(mode === 'rejected' ? 401 : 502);
      expect(response.headers.get('set-cookie') ?? '').not.toContain(token);
      expect(await response.text()).not.toContain(token);
    }
  });
});

describe('local development proxy', () => {
  let upstream: http.Server;
  let vite: ViteDevServer;
  let origin: string;
  let proxyPort: number;
  let mode: Mode = 'ok';
  const received: Recorded[] = [];

  const post = (body: string, headers: Record<string, string> = {}) => fetch(`${origin}/api/tasks`, {
    method: 'POST', body,
    headers: { 'content-type': 'application/json', origin, 'x-hic-request': 'command-center', ...headers },
  });
  const valid = JSON.stringify({ title: 'Synthetic', description: `Synthetic request ${SECRET_MARKER}` });

  beforeAll(async () => {
    upstream = http.createServer((req, res) => {
      const chunks: Buffer[] = [];
      req.on('data', chunk => chunks.push(chunk));
      req.on('end', () => {
        received.push({ method: req.method, url: req.url, headers: req.headers, body: Buffer.concat(chunks).toString('utf8') });
        if (mode === 'hang') return;
        if (mode === 'reset') { req.socket.destroy(); return; }
        if (mode === 'error') { res.writeHead(500, { 'content-type': 'application/json' }).end('{"error":"boom"}'); return; }
        if (mode === 'malformed') { res.writeHead(201, { 'content-type': 'application/json' }).end('{not json'); return; }
        if (req.method === 'POST') res.writeHead(201, { 'content-type': 'application/json' }).end(JSON.stringify(created));
        else res.writeHead(200, { 'content-type': 'application/json' }).end('[]');
      });
    });
    const port = await listen(upstream);
    ({ vite, origin, port: proxyPort } = await startProxy(`http://127.0.0.1:${port}`, {
      upstreamTimeoutMs: 300, bodyTimeoutMs: 300, modelTimeoutMs: 300, executionTimeoutMs: 300, githubTimeoutMs: 300,
    }));
  });
  beforeEach(() => { received.length = 0; mode = 'ok'; });
  afterAll(async () => {
    await vite?.close();
    if (upstream) await close(upstream);
  });

  it('keeps the existing GET endpoints working', async () => {
    for (const path of ['/api/health', '/api/tasks', '/api/plans', '/api/diagnostics/local', `/api/tasks/${created.id}`]) {
      expect((await fetch(`${origin}${path}`)).status).toBe(200);
      expect((await fetch(`${origin}${path}`, { headers: { origin } })).status).toBe(200);
    }
    expect(received.map(r => `${r.method} ${r.url}`)).toEqual(
      ['/health', '/health', '/tasks', '/tasks', '/plans', '/plans', '/diagnostics/local', '/diagnostics/local', `/tasks/${created.id}`, `/tasks/${created.id}`].map(p => `GET ${p}`));
  });

  it('forwards only authenticated exact GitHub publication routes and bounded digest approvals', async () => {
    const token = 'Bearer ' + 'S'.repeat(44);
    const headers = { origin, 'x-hic-request': 'local-runner', authorization: token, cookie: 'private=1' };
    for (const path of ['status', 'repository', 'bootstraps', `publications/${created.id}`]) {
      expect((await fetch(`${origin}/api/local-runner/github/${path}`, { headers })).status).toBe(200);
    }
    for (const path of ['bootstrap-preview', `runs/${created.id}/preview`,
      ...['execute', 'reconcile', 'complete-draft'].map(action => `publications/${created.id}/${action}`)]) {
      expect((await fetch(`${origin}/api/local-runner/github/${path}`, { method: 'POST', headers })).status).toBe(201);
    }
    for (const action of ['approve', 'reject', 'renew']) {
      expect((await fetch(`${origin}/api/local-runner/github/publications/${created.id}/${action}`, {
        method: 'POST', headers: { ...headers, 'content-type': 'application/json' },
        body: JSON.stringify({ intentDigestSha256: 'a'.repeat(64) }),
      })).status).toBe(201);
    }
    for (const recorded of received) {
      expect(recorded.headers.authorization).toBe(token);
      expect(recorded.headers.cookie).toBeUndefined();
    }
    const before = received.length;
    for (const [method, path, body] of [
      ['POST', `publications/${created.id}/approve`, JSON.stringify({ intentDigestSha256: 'a'.repeat(64), repository: 'other' })],
      ['POST', `publications/${created.id}/execute`, '{"force":true}'],
      ['GET', 'status?repository=other', undefined],
      ['PUT', `publications/${created.id}/execute`, undefined],
      ['POST', `publications/${created.id}/merge`, undefined],
    ]) {
      const response = await fetch(`${origin}/api/local-runner/github/${path}`, {
        method, headers: { ...headers, ...(body ? { 'content-type': 'application/json' } : {}) }, body,
      });
      expect(response.status).toBeGreaterThanOrEqual(400);
    }
    expect((await fetch(`${origin}/api/local-runner/github/status`, {
      headers: { origin, 'x-hic-request': 'local-runner' },
    })).status).toBe(401);
    expect((await fetch(`${origin}/api/local-runner/github/status`, {
      headers: { ...headers, origin: 'http://attacker.invalid' },
    })).status).toBe(403);
    expect(received).toHaveLength(before);
  });

  it('reports an interrupted GitHub approval as unknown instead of implying success', async () => {
    mode = 'hang';
    const response = await fetch(`${origin}/api/local-runner/github/publications/${created.id}/approve`, {
      method: 'POST', headers: { origin, 'x-hic-request': 'local-runner',
        authorization: 'Bearer ' + 'S'.repeat(44), 'content-type': 'application/json' },
      body: JSON.stringify({ intentDigestSha256: 'a'.repeat(64) }),
    });
    expect(response.status).toBe(504);
    expect(response.headers.get('X-HIC-Proxy-Error')).toBe('timeout');
    expect(received).toHaveLength(1);
  });

  it('allows only GET reads and POST /api/tasks', async () => {
    const denied: [string, string][] = [
      ['PUT', '/api/tasks'], ['DELETE', '/api/tasks'], ['PATCH', '/api/tasks'], ['POST', '/api/plans'],
      ['POST', '/api/health'], ['POST', '/api/diagnostics/local'], ['POST', `/api/tasks/${created.id}`], ['POST', `/api/tasks/${created.id}/approve`],
      ['POST', `/api/tasks/${created.id}/retry`], ['POST', '/api/tasks/'], ['POST', '/api/tasks?x=1'],
      ['GET', '/api/tasks?x=1'], ['GET', '/api/diagnostics/local?x=1'], ['GET', `/api/tasks/${created.id}/approve`], ['GET', '/api/agents'],
    ];
    for (const [method, path] of denied) {
      const response = await fetch(`${origin}${path}`, { method, headers: { origin, 'content-type': 'application/json', 'x-hic-request': 'command-center' },
        body: method === 'GET' ? undefined : valid });
      expect(response.status, `${method} ${path}`).toBe(403);
    }
    expect(received).toEqual([]);
  });

  it('proxies only the authenticated local runner allowlist and never forwards unrelated headers', async () => {
    const token = 'Bearer ' + 'S'.repeat(44);
    const session = await fetch(`${origin}/api/local-runner/session`, {
      method: 'POST',
      headers: { origin, 'content-type': 'application/json', 'x-hic-request': 'local-runner' },
      body: JSON.stringify({ bootstrapToken: 'R'.repeat(43) + '=' }),
    });
    expect(session.status).toBe(201);
    expect((await fetch(`${origin}/api/local-runner/capabilities`, {
      headers: { 'sec-fetch-site': 'same-origin', 'x-hic-request': 'local-runner', authorization: token, cookie: 'private=1' },
    })).status).toBe(200);
    const digest = 'a'.repeat(64);
    const approval = await fetch(`${origin}/api/local-runner/tasks/${created.id}/approve`, {
      method: 'POST',
      headers: { origin, 'content-type': 'application/json', 'x-hic-request': 'local-runner',
        authorization: token, cookie: 'private=1' },
      body: JSON.stringify({ intentDigestSha256: digest }),
    });
    expect(approval.status).toBe(201);

    const forwarded = received.slice(-3);
    expect(forwarded.map(item => `${item.method} ${item.url}`)).toEqual([
      'POST /local-runner/session',
      'GET /local-runner/capabilities',
      `POST /local-runner/tasks/${created.id}/approve`,
    ]);
    expect(JSON.parse(forwarded[0].body)).toEqual({ bootstrapToken: 'R'.repeat(43) + '=' });
    expect(forwarded[0].headers.authorization).toBeUndefined();
    expect(forwarded[1].headers.authorization).toBe(token);
    expect(forwarded[2].headers.authorization).toBe(token);
    for (const request of forwarded) expect(request.headers.cookie).toBeUndefined();

    const before = received.length;
    for (const [method, path, headers, body] of [
      ['POST', `/api/local-runner/tasks/${created.id}/approve`, { authorization: token }, JSON.stringify({ intentDigestSha256: digest, action: 'shell.run' })],
      ['POST', `/api/local-runner/runs/${created.id}/execute`, { authorization: token, 'content-type': 'application/json' }, JSON.stringify({ command: 'whoami' })],
      ['GET', `/api/local-runner/runs/${created.id}`, {}, undefined],
      ['POST', '/api/local-runner/tasks/../approve', { authorization: token }, '{}'],
    ] as [string, string, Record<string, string>, string | undefined][]) {
      const response = await fetch(`${origin}${path}`, {
        method, headers: { origin, 'x-hic-request': 'local-runner', ...headers },
        body: method === 'GET' ? undefined : body ?? '',
      });
      expect(response.status).toBeGreaterThanOrEqual(400);
    }
    expect(received).toHaveLength(before);

    const missingOrigin = await fetch(`${origin}/api/local-runner/capabilities`, {
      headers: { 'x-hic-request': 'local-runner', authorization: token },
    });
    expect(missingOrigin.status).toBe(403);
    expect(received).toHaveLength(before);
  });

  it('proxies bounded conversation, local-model and exact plan-approval endpoints only', async () => {
    const token = 'Bearer ' + 'S'.repeat(44);
    const headers = {
      origin, 'content-type': 'application/json', 'x-hic-request': 'local-runner',
      authorization: token, cookie: 'private=1',
    };
    const conversation = '11234567-89ab-cdef-0123-456789abcdef';
    const message = '21234567-89ab-cdef-0123-456789abcdef';
    expect((await fetch(`${origin}/api/local-runner/model`, {
      headers: { 'sec-fetch-site': 'same-origin', 'x-hic-request': 'local-runner', authorization: token },
    })).status).toBe(200);
    expect((await fetch(`${origin}/api/local-runner/conversations`, {
      headers: { 'sec-fetch-site': 'same-origin', 'x-hic-request': 'local-runner', authorization: token },
    })).status).toBe(200);
    const createdConversation = await fetch(`${origin}/api/local-runner/conversations`, {
      method: 'POST', headers,
      body: JSON.stringify({ title: 'Test project', repositoryId: 'fixture', file: 'src/Value.cs' }),
    });
    expect(createdConversation.status).toBe(201);
    const userMessage = await fetch(`${origin}/api/local-runner/conversations/${conversation}/messages`, {
      method: 'POST', headers, body: JSON.stringify({ clientMessageId: message, content: 'Make a bounded change.' }),
    });
    expect(userMessage.status).toBe(201);
    const approved = await fetch(`${origin}/api/local-runner/conversations/${conversation}/messages/${message}/approve`, {
      method: 'POST', headers, body: JSON.stringify({ intentDigestSha256: 'a'.repeat(64) }),
    });
    expect(approved.status).toBe(201);
    const invalid = await fetch(`${origin}/api/local-runner/conversations`, {
      method: 'POST', headers,
      body: JSON.stringify({ title: 'Traversal', repositoryId: 'fixture', file: '../outside.cs' }),
    });
    expect(invalid.status).toBe(400);

    const forwarded = received.slice(-5);
    expect(forwarded.map(item => `${item.method} ${item.url}`)).toEqual([
      'GET /local-runner/model',
      'GET /local-runner/conversations',
      'POST /local-runner/conversations',
      `POST /local-runner/conversations/${conversation}/messages`,
      `POST /local-runner/conversations/${conversation}/messages/${message}/approve`,
    ]);
    expect(forwarded[3].headers.authorization).toBe(token);
    for (const request of forwarded) expect(request.headers.cookie).toBeUndefined();
    expect(received).toHaveLength(5);
  });

  it('proxies only authenticated bounded general-conversation requests', async () => {
    const id = '11234567-89ab-cdef-0123-456789abcdef';
    const headers = { origin, 'content-type': 'application/json', 'x-hic-request': 'local-runner',
      authorization: 'Bearer ' + 'S'.repeat(44) };
    expect((await fetch(`${origin}/api/local-runner/general-conversations`, {
      method: 'POST', headers, body: JSON.stringify({ title: 'Ideas' }),
    })).status).toBe(201);
    expect((await fetch(`${origin}/api/local-runner/general-conversations/${id}/messages`, {
      method: 'POST', headers, body: JSON.stringify({ clientMessageId: id, content: 'Hello' }),
    })).status).toBe(201);
    expect((await fetch(`${origin}/api/local-runner/general-conversations/${id}/plan/generate`, {
      method: 'POST', headers, body: JSON.stringify({ expectedRevision: 0 }),
    })).status).toBe(201);
    expect((await fetch(`${origin}/api/local-runner/general-conversations/${id}/plan/approve`, {
      method: 'POST', headers, body: JSON.stringify({ expectedRevision: 1, command: 'not allowed' }),
    })).status).toBe(400);
    expect((await fetch(`${origin}/api/local-runner/general-conversations`, {
      method: 'POST', headers, body: JSON.stringify({ title: 'Ideas', command: 'not allowed' }),
    })).status).toBe(400);
    expect((await fetch(`${origin}/api/local-runner/general-conversations/${id}/execute`, {
      method: 'POST', headers, body: '{}',
    })).status).toBe(403);
    expect(received.map(item => item.url)).toEqual([
      '/local-runner/general-conversations', `/local-runner/general-conversations/${id}/messages`,
      `/local-runner/general-conversations/${id}/plan/generate`,
    ]);
  });

  it('forwards one validated POST with a re-serialized body and no client credentials', async () => {
    const response = await post(JSON.stringify({ description: 'Exact\n  request text  ', title: 'Title' }),
      { cookie: 'session=private', 'x-forwarded-for': '10.0.0.1', 'content-type': 'application/json; charset=utf-8' });
    expect(response.status).toBe(201);
    expect(await response.json()).toEqual(created);
    expect(response.headers.get('access-control-allow-origin')).toBeNull();
    expect(received).toHaveLength(1);
    const [forwarded] = received;
    expect(forwarded.method).toBe('POST');
    expect(forwarded.url).toBe('/tasks');
    expect(JSON.parse(forwarded.body)).toEqual({ title: 'Title', description: 'Exact\n  request text  ' });
    expect(forwarded.headers['content-type']).toBe('application/json; charset=utf-8');
    for (const name of ['cookie', 'authorization', 'origin', 'referer', 'x-hic-request', 'x-forwarded-for']) {
      expect(forwarded.headers[name], name).toBeUndefined();
    }
  });

  it('rejects unexpected origins, cross-site requests and missing CSRF headers', async () => {
    const cases: Record<string, string>[] = [
      { origin: 'https://attacker.example' },
      { origin: 'http://localhost:5173' },
      { origin: 'null' },
      { 'sec-fetch-site': 'cross-site' },
      { 'sec-fetch-site': 'same-site' },
      { 'x-hic-request': 'other' },
    ];
    for (const headers of cases) {
      const response = await post(valid, headers);
      expect(response.status, JSON.stringify(headers)).toBe(403);
      expect(response.headers.get('access-control-allow-origin')).toBeNull();
    }
    const withoutOrigin = await fetch(`${origin}/api/tasks`, { method: 'POST', body: valid,
      headers: { 'content-type': 'application/json', 'x-hic-request': 'command-center' } });
    expect(withoutOrigin.status).toBe(403);
    const withoutHeader = await fetch(`${origin}/api/tasks`, { method: 'POST', body: valid,
      headers: { 'content-type': 'application/json', origin } });
    expect(withoutHeader.status).toBe(403);
    expect((await fetch(`${origin}/api/tasks`, { headers: { origin: 'https://attacker.example' } })).status).toBe(403);
    expect(received).toEqual([]);
  });

  it('rejects requests whose Host header is not the loopback dashboard (DNS rebinding)', async () => {
    const status = await new Promise<number>((resolve, reject) => {
      http.get({ hostname: '127.0.0.1', port: proxyPort, path: '/api/tasks', headers: { host: 'attacker.example' } },
        res => { res.resume(); resolve(res.statusCode ?? 0); }).on('error', reject);
    });
    expect(status).toBe(403);
    expect(received).toEqual([]);
  });

  it('rejects authorization headers', async () => {
    expect((await post(valid, { authorization: 'Bearer synthetic' })).status).toBe(403);
    expect((await post(valid, { 'proxy-authorization': 'Basic synthetic' })).status).toBe(403);
    expect(received).toEqual([]);
  });

  it('rejects non-JSON, invalid, and oversized bodies without echoing input', async () => {
    const responses = [
      [await post(valid, { 'content-type': 'text/plain' }), 415],
      [await post(valid, { 'content-type': 'application/x-www-form-urlencoded' }), 415],
      [await post(valid, { 'content-type': 'application/json; charset=latin1' }), 415],
      [await post(`{"title":"x","description":"${SECRET_MARKER}"`), 400],
      [await post(JSON.stringify([SECRET_MARKER])), 400],
      [await post(JSON.stringify({ title: 'x', description: SECRET_MARKER, status: 'Approved' })), 400],
      [await post(JSON.stringify({ title: '   ', description: SECRET_MARKER })), 400],
      [await post(JSON.stringify({ title: 'x', description: ' ' })), 400],
      [await post(JSON.stringify({ title: 'x'.repeat(201), description: SECRET_MARKER })), 400],
      [await post(JSON.stringify({ title: 'x', description: `${SECRET_MARKER}${'y'.repeat(DESCRIPTION_MAX_LENGTH)}` })), 400],
      [await post(JSON.stringify({ title: 'x', description: SECRET_MARKER + 'z'.repeat(MAX_REQUEST_BYTES) })), 413],
    ] as const;
    for (const [response, status] of responses) {
      expect(response.status).toBe(status);
      expect(response.headers.get('x-hic-proxy-error')).toBe('rejected');
      expect(await response.text()).not.toContain(SECRET_MARKER);
    }
    expect(received).toEqual([]);
  });

  it('passes through Control Plane errors and malformed responses for the client to handle', async () => {
    mode = 'error';
    const failed = await post(valid);
    expect(failed.status).toBe(500);
    expect(failed.headers.get('x-hic-proxy-error')).toBeNull();
    mode = 'malformed';
    const malformed = await post(valid);
    expect(malformed.status).toBe(201);
    expect(await malformed.text()).toBe('{not json');
  });

  it('reports upstream timeouts and interrupted connections as uncertain outcomes', async () => {
    mode = 'hang';
    const timedOut = await post(valid);
    expect(timedOut.status).toBe(504);
    expect(timedOut.headers.get('x-hic-proxy-error')).toBe('timeout');
    mode = 'reset';
    const reset = await post(valid);
    expect(reset.status).toBe(502);
    expect(reset.headers.get('x-hic-proxy-error')).toBe('interrupted');
    expect(await reset.text()).not.toContain(SECRET_MARKER);
  });

  it('reports an unreachable Control Plane as not delivered', async () => {
    const closed = http.createServer();
    const port = await listen(closed);
    await close(closed);
    const proxy = await startProxy(`http://127.0.0.1:${port}`);
    try {
      const response = await fetch(`${proxy.origin}/api/tasks`, { method: 'POST', body: valid,
        headers: { 'content-type': 'application/json', origin: proxy.origin, 'x-hic-request': 'command-center' } });
      expect(response.status).toBe(502);
      expect(response.headers.get('x-hic-proxy-error')).toBe('unreachable');
      expect((await fetch(`${proxy.origin}/api/health`)).headers.get('x-hic-proxy-error')).toBe('unreachable');
    } finally {
      await proxy.vite.close();
    }
  });

  it('rejects non-loopback, credentials, paths, and TLS targets', () => {
    for (const target of ['http://localhost:8080', 'http://192.168.1.1:8080', 'http://user:pass@127.0.0.1:8080', 'http://127.0.0.1:8080/x', 'https://127.0.0.1']) {
      expect(() => apiTarget(target)).toThrow();
    }
  });

  it('does not grant cross-origin access', async () => {
    const response = await fetch(`${origin}/api/health`, { headers: { origin: 'https://attacker.example' } });
    expect(response.headers.get('access-control-allow-origin')).toBeNull();
    const preflight = await fetch(`${origin}/api/tasks`, { method: 'OPTIONS', headers: {
      origin: 'https://attacker.example', 'access-control-request-method': 'POST', 'access-control-request-headers': 'content-type,x-hic-request' } });
    expect(preflight.headers.get('access-control-allow-origin')).toBeNull();
    expect(preflight.headers.get('access-control-allow-headers')).toBeNull();
  });
});
