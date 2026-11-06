import { afterEach, describe, expect, it, vi } from 'vitest';
import { api, ApiError } from './api';

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
