import http from 'node:http';
import { createHash } from 'node:crypto';
import type { Plugin } from 'vite';
import { COMMAND_CENTER_HEADER, COMMAND_CENTER_HEADER_VALUE, MAX_REQUEST_BYTES, validateCreateTask } from './src/limits';

const readPath = /^\/api\/(?:health|tasks|plans|diagnostics\/local|tasks\/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$/i;
const createPath = '/api/tasks';
const guidPath = '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}';
const githubPaths = `github/(?:status|repository|bootstraps|bootstrap-preview|runs/${guidPath}/preview|publications/${guidPath}(?:/(?:approve|reject|renew|execute|reconcile|complete-draft))?)`;
const localRunnerPath = new RegExp(
  `^/api/local-runner/(?:${githubPaths}|session(?:/revoke)?|capabilities|model|repositories|general-conversations(?:/${guidPath}(?:/messages|/plan(?:/(?:generate|revise|approve|reject))?)?)?|conversations(?:/${guidPath}(?:/messages(?:/${guidPath}/approve)?)?)?|tasks/${guidPath}/(?:intent|approve|git-preview|git-approve)|runs/${guidPath}(?:/execute)?)$`, 'i');
const UPSTREAM_TIMEOUT_MS = 8000;
const BODY_TIMEOUT_MS = 8000;
const MODEL_UPSTREAM_TIMEOUT_MS = 125000;

export function apiTarget(value = process.env.HIC_API_URL ?? 'http://127.0.0.1:19480'): URL {
  let url: URL;
  try {
    url = new URL(value);
  } catch {
    throw new Error('HIC_API_URL must be an HTTP URL pointing to 127.0.0.1.');
  }
  if (url.protocol !== 'http:' || url.hostname !== '127.0.0.1' || url.username || url.password ||
      url.pathname !== '/' || url.search || url.hash) {
    throw new Error('HIC_API_URL must be an HTTP origin on 127.0.0.1 without credentials or a path.');
  }
  return url;
}

function reject(res: http.ServerResponse, status: number, message: string, close = false) {
  if (res.headersSent) return;
  res.writeHead(status, {
    'Content-Type': 'text/plain; charset=utf-8',
    'Cache-Control': 'no-store',
    'X-Content-Type-Options': 'nosniff',
    'X-HIC-Proxy-Error': 'rejected',
    ...(close ? { Connection: 'close' } : {}),
  }).end(message);
}

const header = (req: http.IncomingMessage, name: string) => {
  const value = req.headers[name];
  return Array.isArray(value) ? value.join(',') : value;
};

function cookieName(req: http.IncomingMessage, target: URL) {
  return `hic_operator_${req.socket.localPort}_${createHash('sha256').update(target.origin).digest('hex').slice(0, 16)}`;
}

function operatorCookie(req: http.IncomingMessage, target: URL, value: string, maxAge: number) {
  return `${cookieName(req, target)}=${value}; Path=/api/local-runner; HttpOnly; SameSite=Strict; Max-Age=${maxAge}`;
}

function cookieAuthorization(req: http.IncomingMessage, target: URL) {
  const prefix = `${cookieName(req, target)}=`;
  const matches = (header(req, 'cookie') ?? '').split(';').map(part => part.trim()).filter(part => part.startsWith(prefix));
  if (matches.length !== 1) return undefined;
  const value = matches[0].slice(prefix.length);
  return /^[A-Za-z0-9+/]{43}=$/.test(value) ? `Bearer ${value}` : undefined;
}

function forward(req: http.IncomingMessage, res: http.ServerResponse, target: URL, method: 'GET' | 'POST', path: string, timeoutMs: number, body?: string, authorization?: string) {
  let timedOut = false;
  let connected = false;
  const headers: http.OutgoingHttpHeaders = { accept: 'application/json' };
  if (authorization) headers.authorization = authorization;
  const browserLogin = method === 'POST' && path === '/local-runner/session';
  if (browserLogin) headers['x-hic-session-mode'] = 'browser';
  if (body !== undefined) {
    headers['content-type'] = 'application/json; charset=utf-8';
    headers['content-length'] = Buffer.byteLength(body);
  }
  const upstream = http.request({ hostname: target.hostname, port: target.port || 80, path, method, headers }, (response) => {
    if (path.startsWith('/local-runner/') &&
        (response.statusCode === 401 || path === '/local-runner/session/revoke' && response.statusCode === 200)) {
      res.setHeader('Set-Cookie', operatorCookie(req, target, '', 0));
    }
    if (browserLogin && response.statusCode === 200) {
      const chunks: Buffer[] = [];
      let size = 0;
      response.on('data', (chunk: Buffer) => {
        size += chunk.length;
        if (size > 16384) { response.destroy(); reject(res, 502, 'Invalid session response.'); }
        else chunks.push(chunk);
      });
      response.on('end', () => {
        if (res.headersSent) return;
        try {
          const session = JSON.parse(Buffer.concat(chunks).toString('utf8'));
          if (!/^[A-Za-z0-9+/]{43}=$/.test(session.accessToken) ||
              !Number.isSafeInteger(session.expiresInSeconds) ||
              session.expiresInSeconds <= 0 || session.expiresInSeconds > 28800) throw new Error();
          const remaining = Math.floor(Math.min(session.expiresInSeconds,
            (Date.parse(session.expiresAtUtc) - Date.now()) / 1000));
          if (!Number.isSafeInteger(remaining) || remaining <= 0) throw new Error();
          res.writeHead(200, {
            'Content-Type': 'application/json',
            'Cache-Control': 'no-store',
            'X-Content-Type-Options': 'nosniff',
            'Set-Cookie': operatorCookie(req, target, session.accessToken, remaining),
          }).end(JSON.stringify({
            accessToken: 'cookie-session', expiresInSeconds: remaining, expiresAtUtc: session.expiresAtUtc,
          }));
        } catch { reject(res, 502, 'Invalid session response.'); }
      });
      response.on('error', () => reject(res, 502, 'Session response interrupted.'));
      return;
    }
    res.writeHead(response.statusCode ?? 502, {
      'Content-Type': response.headers['content-type'] ?? 'application/octet-stream',
      'Cache-Control': 'no-store',
      'X-Content-Type-Options': 'nosniff',
    });
    response.pipe(res);
  });
  upstream.on('socket', (socket) => {
    if (!socket.connecting) connected = true;
    else socket.once('connect', () => { connected = true; });
  });
  upstream.setTimeout(timeoutMs, () => {
    timedOut = true;
    upstream.destroy(new Error('Control Plane request timed out'));
  });
  upstream.on('error', () => {
    // "unreachable" is only reported when no connection was made, so a POST with that outcome was never delivered.
    const kind = timedOut ? 'timeout' : connected ? 'interrupted' : 'unreachable';
    if (!res.headersSent) res.writeHead(kind === 'timeout' ? 504 : 502, {
      'Content-Type': 'text/plain; charset=utf-8',
      'Cache-Control': 'no-store',
      'X-HIC-Proxy-Error': kind,
    });
    res.end('Control Plane is unavailable.');
  });
  req.on('aborted', () => upstream.destroy());
  upstream.end(body);
}

function readBody(req: http.IncomingMessage, timeoutMs: number): Promise<{ ok: true; body: Buffer } | { ok: false; status: number; message: string }> {
  return new Promise((resolve) => {
    const chunks: Buffer[] = [];
    let size = 0;
    let settled = false;
    const finish = (result: { ok: true; body: Buffer } | { ok: false; status: number; message: string }) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      resolve(result);
    };
    const timer = setTimeout(() => {
      finish({ ok: false, status: 408, message: 'Request body was not received in time.' });
      req.destroy();
    }, timeoutMs);
    req.on('data', (chunk: Buffer) => {
      if (settled) return;
      size += chunk.length;
      if (size > MAX_REQUEST_BYTES) {
        chunks.length = 0;
        finish({ ok: false, status: 413, message: 'Request body is too large.' });
        return;
      }
      chunks.push(chunk);
    });
    req.on('end', () => finish({ ok: true, body: Buffer.concat(chunks) }));
    req.on('error', () => finish({ ok: false, status: 400, message: 'Request body could not be read.' }));
  });
}

async function createTask(req: http.IncomingMessage, res: http.ServerResponse, target: URL, origin: string, timeouts: Required<ProxyOptions>) {
  if (header(req, 'origin') !== origin) return reject(res, 403, 'Requests must originate from the HIC dashboard.', true);
  const site = header(req, 'sec-fetch-site');
  if (site !== undefined && site !== 'same-origin') return reject(res, 403, 'Cross-site requests are not accepted.', true);
  if (header(req, 'authorization') !== undefined || header(req, 'proxy-authorization') !== undefined) {
    return reject(res, 403, 'Credentials are not accepted.', true);
  }
  if (header(req, COMMAND_CENTER_HEADER.toLowerCase()) !== COMMAND_CENTER_HEADER_VALUE) {
    return reject(res, 403, 'Missing Command Center request header.', true);
  }
  const [mediaType, ...parameters] = (header(req, 'content-type') ?? '').split(';').map(part => part.trim().toLowerCase());
  if (mediaType !== 'application/json' || parameters.some(part => part !== '' && part !== 'charset=utf-8')) {
    return reject(res, 415, 'Content-Type must be application/json.', true);
  }
  const encoding = header(req, 'content-encoding');
  if (encoding !== undefined && encoding.toLowerCase() !== 'identity') return reject(res, 415, 'Encoded request bodies are not accepted.', true);
  const declared = Number(header(req, 'content-length') ?? '0');
  if (Number.isFinite(declared) && declared > MAX_REQUEST_BYTES) return reject(res, 413, 'Request body is too large.', true);

  const received = await readBody(req, timeouts.bodyTimeoutMs);
  if (!received.ok) return reject(res, received.status, received.message, true);
  let parsed: unknown;
  try {
    parsed = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(received.body));
  } catch {
    return reject(res, 400, 'Request body must be valid UTF-8 JSON.');
  }
  const validation = validateCreateTask(parsed);
  if (!validation.ok) return reject(res, 400, validation.error);
  forward(req, res, target, 'POST', '/tasks', timeouts.upstreamTimeoutMs, JSON.stringify(validation.value));
}

function validRunnerAuthorization(value: string | undefined): value is string {
  return value !== undefined && /^Bearer [A-Za-z0-9+/_=-]{40,128}$/.test(value);
}

async function localRunnerRequest(
  req: http.IncomingMessage, res: http.ServerResponse, target: URL, origin: string,
  path: string, timeouts: Required<ProxyOptions>,
) {
  const requestOrigin = header(req, 'origin');
  const site = header(req, 'sec-fetch-site');
  if (site !== undefined && site !== 'same-origin') return reject(res, 403, 'Cross-site requests are not accepted.', true);
  const sameOriginReadWithoutOrigin = req.method === 'GET' && requestOrigin === undefined && site === 'same-origin';
  if (!sameOriginReadWithoutOrigin && requestOrigin !== origin)
    return reject(res, 403, 'Requests must originate from the HIC dashboard.', true);
  if (header(req, 'proxy-authorization') !== undefined ||
      header(req, 'x-hic-request') !== 'local-runner') {
    return reject(res, 403, 'Local runner request was rejected.', true);
  }
  const isSession = req.method === 'POST' && path === '/api/local-runner/session';
  const isRevoke = req.method === 'POST' && path === '/api/local-runner/session/revoke';
  const isGitHubRead = req.method === 'GET' && new RegExp(
    `^/api/local-runner/github/(?:status|repository|bootstraps|publications/${guidPath})$`, 'i').test(path);
  const isGitHubApprove = req.method === 'POST' && new RegExp(
    `^/api/local-runner/github/publications/${guidPath}/(?:approve|reject|renew)$`, 'i').test(path);
  const isGitHubEmptyWrite = req.method === 'POST' && new RegExp(
    `^/api/local-runner/github/(?:bootstrap-preview|runs/${guidPath}/preview|publications/${guidPath}/(?:execute|reconcile|complete-draft))$`, 'i').test(path);
  const isRead = req.method === 'GET' &&
    (isGitHubRead || /^\/api\/local-runner\/(?:session|capabilities|model|repositories|(?:general-)?conversations(?:\/[0-9a-f-]+)?|general-conversations\/[0-9a-f-]+\/plan|tasks\/[0-9a-f-]+\/intent|runs\/[0-9a-f-]+)$/i.test(path));
  const isGitPlan = req.method === 'POST' &&
    /^\/api\/local-runner\/tasks\/[0-9a-f-]+\/git-(?:preview|approve)$/i.test(path);
  const isApprove = req.method === 'POST' &&
    (isGitHubApprove || /^\/api\/local-runner\/tasks\/[0-9a-f-]+\/approve$/i.test(path));
  const isConversationCreate = req.method === 'POST' && path === '/api/local-runner/conversations';
  const isGeneralCreate = req.method === 'POST' && path === '/api/local-runner/general-conversations';
  const isConversationMessage = req.method === 'POST' &&
    /^\/api\/local-runner\/(?:general-)?conversations\/[0-9a-f-]+\/messages$/i.test(path);
  const isConversationApprove = req.method === 'POST' &&
    /^\/api\/local-runner\/conversations\/[0-9a-f-]+\/messages\/[0-9a-f-]+\/approve$/i.test(path);
  const isPlanWrite = req.method === 'POST' &&
    /^\/api\/local-runner\/general-conversations\/[0-9a-f-]+\/plan\/(?:generate|revise|approve|reject)$/i.test(path);
  const isConversationWrite = isConversationCreate || isGeneralCreate || isConversationMessage || isConversationApprove;
  const isExecute = req.method === 'POST' &&
    (isRevoke || isGitHubEmptyWrite || /^\/api\/local-runner\/runs\/[0-9a-f-]+\/execute$/i.test(path));
  if (!isSession && !isRead && !isApprove && !isExecute && !isGitPlan && !isConversationWrite && !isPlanWrite)
    return reject(res, 403, 'Local runner endpoint is not allowlisted.', true);

  const suppliedAuthorization = header(req, 'authorization');
  const authorization = suppliedAuthorization ?? cookieAuthorization(req, target);
  if ((isSession && suppliedAuthorization !== undefined) ||
      (!isSession && suppliedAuthorization !== undefined && !validRunnerAuthorization(suppliedAuthorization))) {
    return reject(res, 403, 'Local runner credentials were rejected.', true);
  }
  if (!isSession && !validRunnerAuthorization(authorization)) {
    res.setHeader('Set-Cookie', operatorCookie(req, target, '', 0));
    return reject(res, 401, 'Operator session is missing or expired.', true);
  }
  if (isRead) {
    if (header(req, 'content-length') !== undefined || header(req, 'transfer-encoding') !== undefined)
      return reject(res, 400, 'Read requests must not include a body.', true);
    forward(req, res, target, 'GET', path.slice(4), isGitHubRead
      ? timeouts.githubTimeoutMs : timeouts.upstreamTimeoutMs, undefined, authorization);
    return;
  }

  if (isExecute) {
    if ((Number(header(req, 'content-length') ?? '0') || 0) > 0 ||
        header(req, 'transfer-encoding') !== undefined ||
        header(req, 'content-type') !== undefined)
      return reject(res, 400, 'The allowlisted action does not accept arguments.', true);
    const received = await readBody(req, timeouts.bodyTimeoutMs);
    if (!received.ok || received.body.length !== 0) return reject(res, 400, 'The allowlisted action does not accept arguments.', true);
    forward(req, res, target, 'POST', path.slice(4), isGitHubEmptyWrite
      ? timeouts.githubTimeoutMs : timeouts.executionTimeoutMs, undefined, authorization);
    return;
  }

  const [mediaType, ...parameters] = (header(req, 'content-type') ?? '').split(';').map(part => part.trim().toLowerCase());
  if (mediaType !== 'application/json' || parameters.some(part => part !== '' && part !== 'charset=utf-8') ||
      (header(req, 'content-encoding') !== undefined && header(req, 'content-encoding')?.toLowerCase() !== 'identity'))
    return reject(res, 415, 'Content-Type must be application/json.', true);
  const received = await readBody(req, timeouts.bodyTimeoutMs);
  if (!received.ok) return reject(res, received.status, received.message, true);
  let parsed: unknown;
  try { parsed = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(received.body)); }
  catch { return reject(res, 400, 'Request body must be valid UTF-8 JSON.'); }

  if (isSession) {
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed) ||
        Object.keys(parsed).length !== 1 ||
        typeof (parsed as Record<string, unknown>).bootstrapToken !== 'string' ||
        !/^[A-Za-z0-9+/]{43}=$/.test((parsed as Record<string, string>).bootstrapToken))
      return reject(res, 400, 'Invalid local session request.');
    forward(req, res, target, 'POST', path.slice(4), timeouts.upstreamTimeoutMs,
      JSON.stringify({ bootstrapToken: (parsed as Record<string, string>).bootstrapToken }));
    return;
  }

  if (isPlanWrite) {
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed) || received.body.length > 12 * 1024)
      return reject(res, 400, 'Invalid bounded plan request.');
    const body = parsed as Record<string, unknown>;
    const revise = path.endsWith('/revise');
    if (Object.keys(body).length !== (revise ? 2 : 1) ||
        !Number.isSafeInteger(body.expectedRevision) || Number(body.expectedRevision) < 0 ||
        Number(body.expectedRevision) >= 2147483647 ||
        (revise && (!Array.isArray(body.tasks) || body.tasks.length < 1 || body.tasks.length > 8 ||
          body.tasks.some((item: unknown) => !item || typeof item !== 'object' || Array.isArray(item) ||
            Object.keys(item).length !== 2 || typeof (item as Record<string, unknown>).title !== 'string' ||
            typeof (item as Record<string, unknown>).description !== 'string'))))
      return reject(res, 400, 'Invalid bounded plan request.');
    forward(req, res, target, 'POST', path.slice(4), path.endsWith('/generate')
      ? timeouts.modelTimeoutMs : timeouts.upstreamTimeoutMs, JSON.stringify(body), authorization);
    return;
  }

  if (isConversationWrite) {
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed) || received.body.length > 12 * 1024)
      return reject(res, 400, 'Invalid bounded conversation request.');
    const body = parsed as Record<string, unknown>;
    if (isGeneralCreate) {
      if (Object.keys(body).length !== 1 || typeof body.title !== 'string' ||
          !body.title.trim() || body.title.length > 120 || /[\u0000-\u001f\u007f]/.test(body.title))
        return reject(res, 400, 'Invalid general conversation title.');
    } else if (isConversationCreate) {
      if (Object.keys(body).length !== 3 || typeof body.title !== 'string' ||
          body.title.length < 1 || body.title.length > 120 || /[\u0000-\u001f\u007f]/.test(body.title) ||
          typeof body.repositoryId !== 'string' || !/^[a-z0-9-]{1,40}$/.test(body.repositoryId) ||
          typeof body.file !== 'string' || body.file.length > 120 ||
          !/^(?!\.git(?:\/|$))(?!\.hic-disposable$)[A-Za-z0-9_.-]+(?:\/[A-Za-z0-9_.-]+)*\.cs$/i.test(body.file) ||
          body.file.split('/').some(segment => segment === '.' || segment === '..'))
        return reject(res, 400, 'Invalid conversation repository, title or C# file.');
    } else if (isConversationMessage) {
      if (Object.keys(body).length !== 2 || typeof body.clientMessageId !== 'string' ||
          !new RegExp(`^${guidPath}$`, 'i').test(body.clientMessageId) ||
          typeof body.content !== 'string' || body.content.length < 1 || body.content.length > 4000 ||
          /[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]/.test(body.content))
        return reject(res, 400, 'Invalid bounded conversation message.');
    } else if (Object.keys(body).length !== 1 || typeof body.intentDigestSha256 !== 'string' ||
        !/^[0-9a-f]{64}$/.test(body.intentDigestSha256)) {
      return reject(res, 400, 'Invalid conversation plan approval.');
    }
    forward(req, res, target, 'POST', path.slice(4), isConversationMessage
      ? timeouts.modelTimeoutMs : timeouts.upstreamTimeoutMs, JSON.stringify(body), authorization);
    return;
  }

  if (isGitPlan) {
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed) || received.body.length > 4096)
      return reject(res, 400, 'Invalid bounded Git plan.');
    forward(req, res, target, 'POST', path.slice(4), timeouts.upstreamTimeoutMs, JSON.stringify(parsed), authorization);
    return;
  }

  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed) ||
      Object.keys(parsed).length !== 1 ||
      typeof (parsed as Record<string, unknown>).intentDigestSha256 !== 'string' ||
      !/^[0-9a-f]{64}$/.test((parsed as Record<string, string>).intentDigestSha256))
    return reject(res, 400, 'Invalid approval intent.');
  forward(req, res, target, 'POST', path.slice(4), isGitHubApprove
    ? timeouts.githubTimeoutMs : timeouts.upstreamTimeoutMs,
    JSON.stringify({ intentDigestSha256: (parsed as Record<string, string>).intentDigestSha256 }), authorization);
}

export interface ProxyOptions {
  upstreamTimeoutMs?: number;
  bodyTimeoutMs?: number;
  modelTimeoutMs?: number;
  executionTimeoutMs?: number;
  githubTimeoutMs?: number;
}

export function localApiProxy(value?: string, options: ProxyOptions = {}): Plugin {
  const target = apiTarget(value);
  const timeouts = {
    upstreamTimeoutMs: options.upstreamTimeoutMs ?? UPSTREAM_TIMEOUT_MS,
    bodyTimeoutMs: options.bodyTimeoutMs ?? BODY_TIMEOUT_MS,
    modelTimeoutMs: options.modelTimeoutMs ?? MODEL_UPSTREAM_TIMEOUT_MS,
    executionTimeoutMs: options.executionTimeoutMs ?? 115000,
    githubTimeoutMs: options.githubTimeoutMs ?? 180000,
  };
  return {
    name: 'hic-local-api',
    configureServer(server) {
      server.middlewares.use('/api', (req, res) => {
        const path = `/api${req.url ?? ''}`;
        const port = req.socket.localPort;
        const origin = `http://127.0.0.1:${port}`;
        // Host check blocks DNS-rebinding; Origin checks block hostile pages from reading or writing.
        if (!port || header(req, 'host') !== `127.0.0.1:${port}`) return reject(res, 403, 'Unexpected host.');
        const requestOrigin = header(req, 'origin');
        if (req.method === 'GET' && readPath.test(path)) {
          if (requestOrigin !== undefined && requestOrigin !== origin) return reject(res, 403, 'Cross-origin requests are not accepted.');
          forward(req, res, target, 'GET', path.slice(4), timeouts.upstreamTimeoutMs);
          return;
        }
        if (localRunnerPath.test(path)) {
          void localRunnerRequest(req, res, target, origin, path, timeouts);
          return;
        }
        if (req.method === 'POST' && path === createPath) {
          void createTask(req, res, target, origin, timeouts);
          return;
        }
        reject(res, 403, 'Only permitted endpoints are available.', req.method !== 'GET' && req.method !== 'HEAD');
      });
    },
  };
}
