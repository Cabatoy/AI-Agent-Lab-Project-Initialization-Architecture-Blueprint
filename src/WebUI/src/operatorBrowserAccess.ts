export interface OperatorBrowserAccess { sessionUrl: string; expiresAtUtc: string }

export async function operatorBrowserAccess(): Promise<OperatorBrowserAccess | null> {
  const response = await fetch('/operator-browser-access.json', { cache: 'no-store' });
  if (response.status === 404) return null;
  if (!response.ok) throw new Error('Local operator browser access configuration is unavailable.');
  // Vite serves its HTML fallback when no optional public configuration exists.
  if (!response.headers.get('content-type')?.includes('application/json')) return null;
  const value: unknown = await response.json();
  if (!value || typeof value !== 'object' || !('sessionUrl' in value) ||
      typeof value.sessionUrl !== 'string' || !('expiresAtUtc' in value) ||
      typeof value.expiresAtUtc !== 'string') throw new Error('Invalid local operator browser access configuration.');
  const url = new URL(value.sessionUrl);
  if (url.protocol !== 'http:' || url.hostname !== 'localhost' || !url.port ||
      url.pathname !== '/session' || url.search || url.hash || url.username || url.password ||
      !Number.isFinite(Date.parse(value.expiresAtUtc))) {
    throw new Error('Operator browser access requires a credential-free Windows-authenticated localhost endpoint.');
  }
  return { sessionUrl: url.href, expiresAtUtc: value.expiresAtUtc };
}

export async function openOperatorBrowserSession(access: OperatorBrowserAccess): Promise<string> {
  if (Date.parse(access.expiresAtUtc) <= Date.now()) throw new Error('Temporary Windows operator access expired.');
  const response = await fetch(access.sessionUrl, {
    method: 'POST', credentials: 'include', cache: 'no-store', redirect: 'error',
    headers: { 'X-HIC-Operator-Access': 'windows-session' },
  });
  if (!response.ok) throw new Error(`Windows operator authentication failed (${response.status}). No approval was granted.`);
  const value: unknown = await response.json();
  if (!value || typeof value !== 'object' || !('accessToken' in value) ||
      typeof value.accessToken !== 'string' || !/^[A-Za-z0-9+/]{43}=$/.test(value.accessToken) ||
      !('expiresInSeconds' in value) || value.expiresInSeconds !== 900) {
    throw new Error('Invalid operator session response.');
  }
  return value.accessToken;
}
