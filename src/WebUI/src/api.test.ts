import { afterEach, describe, expect, it, vi } from 'vitest';
import { api, ApiError, OPERATOR_SESSION_ENDED } from './api';
import { deriveTitle } from './limits';

const task = {
  id: '01234567-89ab-cdef-0123-456789abcdef', title: 'Task', description: '',
  status: 'Draft', attemptCount: 0, result: null, errorMessage: null,
  createdAtUtc: '2026-11-06T12:00:00Z', updatedAtUtc: '2026-11-06T12:00:00Z',
};
const plan = {
  id: task.id, projectKey: null, requestTitle: 'Plan', requestDescription: null,
  status: 'Proposed', planSummary: 'Summary', createdAtUtc: task.createdAtUtc, updatedAtUtc: task.updatedAtUtc,
};
afterEach(() => { vi.unstubAllGlobals(); vi.useRealTimers(); });

describe('persistent operator session client', () => {
  it('uses same-origin HttpOnly cookie transport without a bearer secret in JavaScript', async () => {
    const restored = { actorId: task.id, expiresInSeconds: 28800,
      expiresAtUtc: new Date(Date.now() + 28800000).toISOString() };
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(Response.json({ accessToken: 'cookie-session', ...restored }))
      .mockResolvedValueOnce(Response.json(restored))
      .mockResolvedValueOnce(Response.json({ revoked: true }));
    vi.stubGlobal('fetch', fetchMock);
    const session = await api.createLocalRunnerSession('synthetic-bootstrap');
    expect(session.accessToken).toBe('cookie-session');
    expect(await api.restoreLocalRunnerSession()).toEqual(restored);
    await api.revokeLocalRunnerSession();
    expect(fetchMock.mock.calls.map(call => [call[0], call[1].method])).toEqual([
      ['/api/local-runner/session', 'POST'],
      ['/api/local-runner/session', 'GET'],
      ['/api/local-runner/session/revoke', 'POST'],
    ]);
    for (const [, options] of fetchMock.mock.calls) {
      expect(options.credentials).toBe('same-origin');
      expect(options.headers.Authorization).toBeUndefined();
      expect(options.headers['X-HIC-Request']).toBe('local-runner');
      expect(options.cache).toBe('no-store');
    }
    expect(fetchMock.mock.calls[1][1].body).toBeUndefined();
    expect(fetchMock.mock.calls[2][1].body).toBeUndefined();
  });

  it('notifies mounted workspaces about protected 401, without retry or automatic bootstrap', async () => {
    const ended = vi.fn();
    window.addEventListener(OPERATOR_SESSION_ENDED, ended);
    try {
      const fetchMock = vi.fn().mockResolvedValue(new Response('', { status: 401 }));
      vi.stubGlobal('fetch', fetchMock);
      await expect(api.restoreLocalRunnerSession()).rejects.toMatchObject({ status: 401, sessionEnded: true });
      expect(ended).toHaveBeenCalledTimes(1);
      expect(fetchMock).toHaveBeenCalledTimes(1);
      await expect(api.createLocalRunnerSession('invalid-bootstrap')).rejects.toMatchObject({ status: 401, sessionEnded: false });
      expect(ended).toHaveBeenCalledTimes(1);
    } finally { window.removeEventListener(OPERATOR_SESSION_ENDED, ended); }
  });

  it('rejects malformed restore metadata and cookie session contracts', async () => {
    vi.stubGlobal('fetch', vi.fn()
      .mockResolvedValueOnce(Response.json({ actorId: task.id, expiresInSeconds: 0, expiresAtUtc: 'bad' }))
      .mockResolvedValueOnce(Response.json({ accessToken: 'cookie-session', expiresInSeconds: 28800, expiresAtUtc: 'bad' })));
    await expect(api.restoreLocalRunnerSession()).rejects.toMatchObject({ kind: 'invalid' });
    await expect(api.createLocalRunnerSession('synthetic')).rejects.toMatchObject({ kind: 'invalid' });
  });
});

describe('native host read-only action', () => {
  const result = {
    action: 'workspace.inspect', runId: '01234567-89ab-cdef-0123-456789abcdef',
    checkedAtUtc: '2026-11-07T00:00:00Z',
    workspaceAvailable: true, gitCheckoutFound: true, solutionFound: true,
  };

  it('submits only a fixed action to the native bridge', async () => {
    const mock = vi.fn().mockResolvedValue(Response.json(result));
    vi.stubGlobal('fetch', mock);
    expect(await api.inspectNativeWorkspace()).toEqual(result);
    const [url, options] = mock.mock.calls[0];
    expect(url).toBe('/api/native/actions');
    expect(options.method).toBe('POST');
    expect(options.credentials).toBe('omit');
    expect(options.headers['X-HIC-Request']).toBe('native-action');
    expect(JSON.parse(options.body)).toEqual({ action: 'workspace.inspect' });
  });

  it('refuses a malformed native response rather than inventing execution', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(Response.json({ ...result, runId: '../bad' })));
    await expect(api.inspectNativeWorkspace()).rejects.toMatchObject({ kind: 'invalid' });
  });

  it('does not retry when native host is unreachable', async () => {
    const mock = vi.fn().mockRejectedValue(new Error('offline'));
    vi.stubGlobal('fetch', mock);
    await expect(api.inspectNativeWorkspace()).rejects.toMatchObject({ kind: 'network' });
    expect(mock).toHaveBeenCalledTimes(1);
  });
});

describe('Control Plane API contract', () => {
  it('reads actual health, task list, task detail and plan shapes using GET', async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(Response.json({ status: 'ok', service: 'control-plane' }))
      .mockResolvedValueOnce(Response.json([task]))
      .mockResolvedValueOnce(Response.json(task))
      .mockResolvedValueOnce(Response.json([plan]));
    vi.stubGlobal('fetch', fetchMock);
    expect(await api.health()).toEqual({ status: 'ok', service: 'control-plane' });
    expect(await api.tasks()).toEqual([task]);
    expect(await api.task(task.id)).toEqual(task);
    expect(await api.plans()).toEqual([plan]);
    expect(fetchMock.mock.calls.map(call => call[0])).toEqual(['/api/health', '/api/tasks', `/api/tasks/${task.id}`, '/api/plans']);
    expect(fetchMock.mock.calls.every(call => call[1].method === 'GET')).toBe(true);
  });
  it('accepts only a structured local health report and uses GET without side effects', async () => {
    const snapshot = { action: 'system.health', checkedAtUtc: '2026-11-07T00:00:00Z',
      controlPlane: 'ok', postgres: 'ok', redis: 'unavailable', worker: 'not-checked' };
    const mock = vi.fn().mockResolvedValueOnce(Response.json(snapshot))
      .mockResolvedValueOnce(Response.json({ ...snapshot, worker: 'running' }));
    vi.stubGlobal('fetch', mock);
    expect(await api.localDiagnostics()).toEqual(snapshot);
    await expect(api.localDiagnostics()).rejects.toMatchObject({ kind: 'invalid' });
    expect(mock.mock.calls.map(call => call[0])).toEqual(['/api/diagnostics/local', '/api/diagnostics/local']);
    expect(mock.mock.calls.every(call => call[1].method === 'GET')).toBe(true);
  });

  it('accepts empty lists', async () => {
    vi.stubGlobal('fetch', vi.fn().mockImplementation(() => Promise.resolve(Response.json([]))));
    expect(await api.tasks()).toEqual([]);
    expect(await api.plans()).toEqual([]);
  });
  it('rejects invalid shapes and malformed JSON', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValueOnce(Response.json([{ ...task, attemptCount: '0' }]))
      .mockResolvedValueOnce(new Response('{', { status: 200 })));
    await expect(api.tasks()).rejects.toMatchObject({ kind: 'invalid' });
    await expect(api.plans()).rejects.toMatchObject({ kind: 'invalid' });
  });
  it('reports HTTP and network errors without treating them as empty data', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValueOnce(new Response('', { status: 503 }))
      .mockRejectedValueOnce(new TypeError('network'))
      .mockResolvedValueOnce(new Response('', { status: 502, headers: { 'X-HIC-Proxy-Error': 'unreachable' } }))
      .mockResolvedValueOnce(new Response('', { status: 502, headers: { 'X-HIC-Proxy-Error': 'timeout' } })));
    await expect(api.tasks()).rejects.toMatchObject({ kind: 'http', status: 503 });
    await expect(api.plans()).rejects.toMatchObject({ kind: 'network' });
    await expect(api.health()).rejects.toMatchObject({ kind: 'network' });
    await expect(api.health()).rejects.toMatchObject({ kind: 'timeout' });
  });
  it('rejects invalid identifiers before making a request', async () => {
    const fetchMock = vi.fn(); vi.stubGlobal('fetch', fetchMock);
    await expect(api.task('../plans')).rejects.toBeInstanceOf(ApiError);
    expect(fetchMock).not.toHaveBeenCalled();
  });
  it('times out a stalled request', async () => {
    vi.useFakeTimers();
    vi.stubGlobal('fetch', vi.fn((_url, options: RequestInit) => new Promise((_resolve, reject) => {
      options.signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')));
    })));
    const request = api.health();
    const assertion = expect(request).rejects.toMatchObject({ kind: 'timeout' });
    await vi.advanceTimersByTimeAsync(8000);
    await assertion;
  });
});

describe('Draft task creation', () => {
  const input = { title: 'Synthetic', description: 'Synthetic private request text' };
  const created = { ...task, title: input.title, description: input.description };

  it('sends exactly one JSON POST with the CSRF header and validates the Draft response', async () => {
    const fetchMock = vi.fn().mockResolvedValue(Response.json(created, { status: 201 }));
    vi.stubGlobal('fetch', fetchMock);
    expect(await api.createTask(input)).toEqual(created);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, options] = fetchMock.mock.calls[0];
    expect(url).toBe('/api/tasks');
    expect(options.method).toBe('POST');
    expect(options.credentials).toBe('omit');
    expect(options.headers).toMatchObject({ 'Content-Type': 'application/json', 'X-HIC-Request': 'command-center' });
    expect(JSON.parse(options.body)).toEqual(input);
  });

  describe('authenticated local runner API', () => {
    const sessionToken = 'S'.repeat(44);
    const healthRun = {
      runId: '11234567-89ab-cdef-0123-456789abcdef',
      taskId: task.id,
      action: 'system.health',
      scope: 'control-plane:health:read',
      intentDigestSha256: 'a'.repeat(64),
      status: 'queued',
      createdAtUtc: task.createdAtUtc,
      approvedAtUtc: task.createdAtUtc,
      expiresAtUtc: '2026-11-06T12:05:00Z',
      startedAtUtc: null,
      completedAtUtc: null,
      result: null,
      events: [{ eventCode: 'approved', recordedAtUtc: task.createdAtUtc }],
    };

    it('uses a short-lived authenticated session and only fixed local-runner intents', async () => {
      const fetchMock = vi.fn()
        .mockResolvedValueOnce(Response.json({ accessToken: sessionToken, expiresInSeconds: 900 }))
        .mockResolvedValueOnce(Response.json({
          taskId: task.id, taskTitle: task.title, action: 'system.health',
          scope: 'control-plane:health:read', taskUpdatedAtUtc: task.updatedAtUtc,
          intentDigestSha256: 'a'.repeat(64),
        }))
        .mockResolvedValueOnce(Response.json({
          runId: healthRun.runId, taskId: task.id, action: 'system.health',
          scope: 'control-plane:health:read', intentDigestSha256: 'a'.repeat(64), status: 'queued',
        }, { status: 202 }))
        .mockResolvedValueOnce(Response.json({ runId: healthRun.runId, status: 'completed' }))
        .mockResolvedValueOnce(Response.json(healthRun));
      vi.stubGlobal('fetch', fetchMock);

      expect(await api.createLocalRunnerSession('R'.repeat(44))).toEqual({ accessToken: sessionToken, expiresInSeconds: 900 });
      expect((await api.localHealthIntent(task.id, sessionToken)).action).toBe('system.health');
      expect((await api.approveLocalHealthRun(task.id, 'a'.repeat(64), sessionToken)).runId).toBe(healthRun.runId);
      await api.executeLocalHealthRun(healthRun.runId, sessionToken);
      expect((await api.localHealthRun(healthRun.runId, sessionToken)).status).toBe('queued');

      expect(fetchMock.mock.calls.map(call => call[0])).toEqual([
        '/api/local-runner/session',
        `/api/local-runner/tasks/${task.id}/intent`,
        `/api/local-runner/tasks/${task.id}/approve`,
        `/api/local-runner/runs/${healthRun.runId}/execute`,
        `/api/local-runner/runs/${healthRun.runId}`,
      ]);
      expect(JSON.parse(fetchMock.mock.calls[0][1].body)).toEqual({ bootstrapToken: 'R'.repeat(44) });
      expect(fetchMock.mock.calls[0][1].headers.Authorization).toBeUndefined();
      expect(fetchMock.mock.calls[1][1].headers.Authorization).toBe(`Bearer ${sessionToken}`);
      expect(JSON.parse(fetchMock.mock.calls[2][1].body)).toEqual({ intentDigestSha256: 'a'.repeat(64) });
      expect(fetchMock.mock.calls[3][1].body).toBeUndefined();
      expect(fetchMock.mock.calls.every(call => call[1].credentials === 'same-origin' &&
        call[1].headers['X-HIC-Request'] === 'local-runner')).toBe(true);
    });

    it('rejects malformed or replay-shaped runner responses and invalid identifiers locally', async () => {
      const fetchMock = vi.fn()
        .mockResolvedValueOnce(Response.json({ accessToken: 'short', expiresInSeconds: 900 }))
        .mockResolvedValueOnce(Response.json({ ...healthRun, action: 'shell.run' }));
      vi.stubGlobal('fetch', fetchMock);
      await expect(api.createLocalRunnerSession('R'.repeat(44))).rejects.toMatchObject({ kind: 'invalid' });
      await expect(api.localHealthRun('../tasks', sessionToken)).rejects.toMatchObject({ kind: 'invalid' });
      await expect(api.localHealthRun(healthRun.runId, sessionToken)).rejects.toMatchObject({ kind: 'invalid' });
      expect(fetchMock).toHaveBeenCalledTimes(2);
    });
  });

  it('validates input before making a request', async () => {
    const fetchMock = vi.fn(); vi.stubGlobal('fetch', fetchMock);
    await expect(api.createTask({ title: ' ', description: 'x' })).rejects.toMatchObject({ kind: 'invalid' });
    await expect(api.createTask({ title: 'x', description: '' })).rejects.toMatchObject({ kind: 'invalid' });
    await expect(api.createTask({ title: 'x', description: 'y'.repeat(8001) })).rejects.toMatchObject({ kind: 'invalid' });
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('reports rejected requests as definitely not created, with a bounded server message', async () => {
    vi.stubGlobal('fetch', vi.fn()
      .mockResolvedValueOnce(Response.json({ error: 'Title is required.' }, { status: 400 }))
      .mockResolvedValueOnce(new Response('Requests must originate from the HIC dashboard.', { status: 403, headers: { 'X-HIC-Proxy-Error': 'rejected' } }))
      .mockResolvedValueOnce(new Response('', { status: 502, headers: { 'X-HIC-Proxy-Error': 'unreachable' } })));
    await expect(api.createTask(input)).rejects.toMatchObject({ kind: 'http', status: 400, outcomeUnknown: false,
      message: 'Control Plane returned HTTP 400: Title is required. No task was created.' });
    await expect(api.createTask(input)).rejects.toMatchObject({ kind: 'http', status: 403, outcomeUnknown: false,
      message: expect.stringContaining('The local proxy rejected the request (HTTP 403)') });
    await expect(api.createTask(input)).rejects.toMatchObject({ kind: 'network', outcomeUnknown: false,
      message: 'Control Plane is unreachable. No task was created.' });
  });

  it('treats timeouts, interruptions, server errors and malformed responses as uncertain', async () => {
    vi.stubGlobal('fetch', vi.fn()
      .mockRejectedValueOnce(new TypeError('network'))
      .mockResolvedValueOnce(new Response('', { status: 504, headers: { 'X-HIC-Proxy-Error': 'timeout' } }))
      .mockResolvedValueOnce(new Response('', { status: 502, headers: { 'X-HIC-Proxy-Error': 'interrupted' } }))
      .mockResolvedValueOnce(Response.json({ error: 'boom' }, { status: 500 }))
      .mockResolvedValueOnce(new Response('{', { status: 201 }))
      .mockResolvedValueOnce(Response.json({ ...created, status: 'Approved' }, { status: 201 }))
      .mockResolvedValueOnce(Response.json(created, { status: 200 })));
    for (let i = 0; i < 7; i++) {
      const error = await api.createTask(input).catch((reason: unknown) => reason);
      expect(error).toBeInstanceOf(ApiError);
      expect(error).toMatchObject({ outcomeUnknown: true });
      expect((error as Error).message).toMatch(/may or may not have been created/);
      expect((error as Error).message).not.toContain(input.description);
    }
  });

  it('ignores multi-line or long server messages that could leak request content', async () => {
    vi.stubGlobal('fetch', vi.fn()
      .mockResolvedValueOnce(Response.json({ error: `bad\n${input.description}` }, { status: 400 }))
      .mockResolvedValueOnce(Response.json({ error: 'x'.repeat(201) }, { status: 400 })));
    for (let i = 0; i < 2; i++) {
      const error = await api.createTask(input).then(() => null, (reason: unknown) => reason as Error);
      expect(error?.message).toBe('Control Plane returned HTTP 400. No task was created.');
    }
  });

  it('times out a stalled create without retrying', async () => {
    vi.useFakeTimers();
    const fetchMock = vi.fn((_url, options: RequestInit) => new Promise((_resolve, reject) => {
      options.signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')));
    }));
    vi.stubGlobal('fetch', fetchMock);
    const assertion = expect(api.createTask(input)).rejects.toMatchObject({ kind: 'timeout', outcomeUnknown: true });
    await vi.advanceTimersByTimeAsync(15000);
    await assertion;
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });
});

describe('title derivation', () => {
  it('uses the first non-empty line, collapses whitespace and shortens at a word boundary', () => {
    expect(deriveTitle('\n\n   Fix   the login   bug  \nmore details')).toBe('Fix the login bug');
    const long = deriveTitle(`${'word '.repeat(40)}end`);
    expect(long.length).toBeLessThanOrEqual(80);
    expect(long.endsWith('…')).toBe(true);
    expect(long).not.toMatch(/ …$/);
    expect(deriveTitle('x'.repeat(200))).toHaveLength(80);
    expect(deriveTitle('   ')).toBe('');
  });
});
