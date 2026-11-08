import { afterEach, expect, it, vi } from 'vitest';
import { operatorBrowserAccess, openOperatorBrowserSession } from './operatorBrowserAccess';

const access = { sessionUrl: 'http://localhost:19556/session', expiresAtUtc: new Date(Date.now() + 60000).toISOString() };
afterEach(() => vi.unstubAllGlobals());

it('uses Windows credentials only with the explicitly configured localhost endpoint and keeps tokens out of URLs', async () => {
  const request = vi.fn().mockResolvedValue(new Response(JSON.stringify({
    accessToken: 'a'.repeat(43) + '=', expiresInSeconds: 900,
  }), { headers: { 'content-type': 'application/json' } }));
  vi.stubGlobal('fetch', request);
  expect(await openOperatorBrowserSession(access)).toBe('a'.repeat(43) + '=');
  expect(request).toHaveBeenCalledWith(access.sessionUrl, expect.objectContaining({
    method: 'POST', credentials: 'include', cache: 'no-store', redirect: 'error',
    headers: { 'X-HIC-Operator-Access': 'windows-session' },
  }));
});

it.each(['http://evil.test:19556/session', 'http://localhost:19556/session?token=secret',
  'http://user:secret@localhost:19556/session', 'http://localhost:19556/session#secret'])(
  'rejects unsafe operator endpoint %s', async sessionUrl => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({ ...access, sessionUrl }),
      { headers: { 'content-type': 'application/json' } })));
    await expect(operatorBrowserAccess()).rejects.toThrow('credential-free Windows-authenticated localhost');
  });

it('does not authenticate when the temporary flow has expired', async () => {
  const request = vi.fn();
  vi.stubGlobal('fetch', request);
  await expect(openOperatorBrowserSession({ ...access, expiresAtUtc: new Date(0).toISOString() })).rejects.toThrow('expired');
  expect(request).not.toHaveBeenCalled();
});

it('preserves manual bootstrap login when no temporary flow was configured', async () => {
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('<html></html>', {
    headers: { 'content-type': 'text/html' },
  })));
  expect(await operatorBrowserAccess()).toBeNull();
});
