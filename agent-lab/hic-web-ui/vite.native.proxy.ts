import http from 'node:http';
import type { Plugin } from 'vite';

const ACTION_PATH = '/actions';
const MAX_BODY_BYTES = 256;
const REQUEST_TIMEOUT_MS = 5000;

export function nativeTarget(value = process.env.HIC_NATIVE_URL ?? 'http://127.0.0.1:19481'): URL {
  let target: URL;
  try { target = new URL(value); }
  catch { throw new Error('HIC_NATIVE_URL must be an HTTP loopback origin.'); }
  if (target.protocol !== 'http:' || target.hostname !== '127.0.0.1' || target.username || target.password ||
      target.pathname !== '/' || target.search || target.hash || !target.port) {
    throw new Error('HIC_NATIVE_URL must be an HTTP loopback origin without a path or credentials.');
  }
  return target;
}

function header(req: http.IncomingMessage, name: string): string | undefined {
  const value = req.headers[name];
  return Array.isArray(value) ? value.join(',') : value;
}

function reject(res: http.ServerResponse, status: number, code: string) {
  if (res.headersSent) return;
  res.writeHead(status, {
    'Content-Type': 'application/json; charset=utf-8',
    'Cache-Control': 'no-store',
    'X-Content-Type-Options': 'nosniff',
  }).end(JSON.stringify({ error: code }));
}

async function readBody(req: http.IncomingMessage): Promise<Buffer | null> {
  return new Promise(resolve => {
    let size = 0;
    const parts: Buffer[] = [];
    let finished = false;
    const complete = (value: Buffer | null) => {
      if (finished) return;
      finished = true;
      clearTimeout(timeout);
      resolve(value);
    };
    const timeout = setTimeout(() => { complete(null); req.destroy(); }, 3000);
    req.on('data', (chunk: Buffer) => {
      if (finished) return;
      size += chunk.length;
      if (size > MAX_BODY_BYTES) { complete(null); req.destroy(); return; }
      parts.push(chunk);
    });
    req.on('end', () => complete(Buffer.concat(parts)));
    req.on('error', () => complete(null));
    req.on('aborted', () => complete(null));
  });
}

export function localNativeProxy(value?: string, configuredToken = process.env.HIC_NATIVE_TOKEN ?? ''): Plugin {
  const target = nativeTarget(value);
  const token = configuredToken;
  return {
    name: 'hic-native-host-proxy',
    configureServer(server) {
      server.middlewares.use('/api/native', (req, res) => {
        const port = req.socket.localPort;
        const origin = `http://127.0.0.1:${port}`;
        if (!port || header(req, 'host') !== `127.0.0.1:${port}`) {
          reject(res, 403, 'invalid-host');
          return;
        }
        if (req.method !== 'POST' || req.url !== ACTION_PATH) {
          reject(res, 403, 'action-not-allowlisted');
          return;
        }
        if (header(req, 'origin') !== origin || (header(req, 'sec-fetch-site') !== undefined &&
            header(req, 'sec-fetch-site') !== 'same-origin')) {
          reject(res, 403, 'invalid-origin');
          return;
        }
        if (header(req, 'x-hic-request') !== 'native-action' || header(req, 'authorization') !== undefined ||
            header(req, 'proxy-authorization') !== undefined) {
          reject(res, 403, 'invalid-request');
          return;
        }
        const [type, ...params] = (header(req, 'content-type') ?? '').split(';').map(x => x.trim().toLowerCase());
        if (type !== 'application/json' || params.some(x => x && x !== 'charset=utf-8') ||
            (header(req, 'content-encoding') !== undefined &&
             header(req, 'content-encoding') !== 'identity')) {
          reject(res, 415, 'invalid-content-type');
          return;
        }
        if (Number(header(req, 'content-length') ?? 0) > MAX_BODY_BYTES) {
          reject(res, 413, 'payload-too-large');
          return;
        }
        if (!token) { reject(res, 503, 'native-host-not-configured'); return; }
        void (async () => {
          const raw = await readBody(req);
          if (raw === null) { reject(res, 400, 'invalid-body'); return; }
          let payload: unknown;
          try { payload = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(raw)); }
          catch { reject(res, 400, 'invalid-json'); return; }
          if (!payload || typeof payload !== 'object' || Array.isArray(payload) ||
              Object.keys(payload).length !== 1 || (payload as Record<string, unknown>).action !== 'workspace.inspect') {
            reject(res, 400, 'action-not-allowlisted');
            return;
          }
          try {
            // Never forward user fields, cookies, credentials or original headers.
            const upstream = await fetch(new URL('/v1/actions/run', target), {
              method: 'POST',
              headers: { 'Content-Type': 'application/json', 'X-HIC-Native-Token': token },
              body: JSON.stringify({ action: 'workspace.inspect' }),
              redirect: 'error',
              signal: AbortSignal.timeout(REQUEST_TIMEOUT_MS),
            });
            if (!upstream.ok) { reject(res, 502, 'native-host-rejected'); return; }
            const response = await upstream.text();
            if (response.length > 4096) { reject(res, 502, 'native-host-invalid-response'); return; }
            res.writeHead(200, {
              'Content-Type': 'application/json; charset=utf-8',
              'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff',
            }).end(response);
          } catch { reject(res, 503, 'native-host-unavailable'); }
        })().catch(() => reject(res, 503, 'native-host-unavailable'));
      });
    },
  };
}
