import http from 'node:http';
import type { Plugin } from 'vite';
import { COMMAND_CENTER_HEADER, COMMAND_CENTER_HEADER_VALUE, MAX_REQUEST_BYTES, validateCreateTask } from './src/limits';

const readPath = /^\/api\/(?:health|tasks|plans|diagnostics\/local|tasks\/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$/i;
const createPath = '/api/tasks';
const UPSTREAM_TIMEOUT_MS = 8000;
const BODY_TIMEOUT_MS = 8000;

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

function forward(req: http.IncomingMessage, res: http.ServerResponse, target: URL, method: 'GET' | 'POST', path: string, timeoutMs: number, body?: string) {
  let timedOut = false;
  let connected = false;
  const headers: http.OutgoingHttpHeaders = { accept: 'application/json' };
  if (body !== undefined) {
    headers['content-type'] = 'application/json; charset=utf-8';
    headers['content-length'] = Buffer.byteLength(body);
  }
  const upstream = http.request({ hostname: target.hostname, port: target.port || 80, path, method, headers }, (response) => {
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

export interface ProxyOptions { upstreamTimeoutMs?: number; bodyTimeoutMs?: number }

export function localApiProxy(value?: string, options: ProxyOptions = {}): Plugin {
  const target = apiTarget(value);
  const timeouts = { upstreamTimeoutMs: options.upstreamTimeoutMs ?? UPSTREAM_TIMEOUT_MS, bodyTimeoutMs: options.bodyTimeoutMs ?? BODY_TIMEOUT_MS };
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
        if (req.method === 'POST' && path === createPath) {
          void createTask(req, res, target, origin, timeouts);
          return;
        }
        reject(res, 403, 'Only permitted endpoints are available.', req.method !== 'GET' && req.method !== 'HEAD');
      });
    },
  };
}
