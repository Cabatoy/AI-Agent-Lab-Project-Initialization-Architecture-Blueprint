import http from 'node:http';
import type { Plugin } from 'vite';

const allowedPath = /^\/api\/(?:health|tasks|plans|tasks\/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$/i;

export function apiTarget(value = process.env.HIC_API_URL ?? 'http://127.0.0.1:8080'): URL {
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

export function localApiProxy(value?: string): Plugin {
  const target = apiTarget(value);
  return {
    name: 'hic-read-only-local-api',
    configureServer(server) {
      server.middlewares.use('/api', (req, res) => {
        const path = `/api${req.url ?? ''}`;
        if (req.method !== 'GET' || !allowedPath.test(path)) {
          res.writeHead(403, { 'Content-Type': 'text/plain' }).end('Only permitted read endpoints are available.');
          return;
        }
        let timedOut = false;
        const upstream = http.get({
          hostname: target.hostname,
          port: target.port || 80,
          path: path.slice(4),
          headers: { accept: 'application/json' },
        }, (response) => {
          res.writeHead(response.statusCode ?? 502, {
            'Content-Type': response.headers['content-type'] ?? 'application/octet-stream',
            'Cache-Control': 'no-store',
            'X-Content-Type-Options': 'nosniff',
          });
          response.pipe(res);
        });
        upstream.setTimeout(8000, () => {
          timedOut = true;
          upstream.destroy(new Error('Control Plane request timed out'));
        });
        upstream.on('error', () => {
          if (!res.headersSent) res.writeHead(502, {
            'Content-Type': 'text/plain',
            'X-HIC-Proxy-Error': timedOut ? 'timeout' : 'unreachable',
          });
          res.end('Control Plane is unavailable.');
        });
        req.on('aborted', () => upstream.destroy());
      });
    },
  };
}
