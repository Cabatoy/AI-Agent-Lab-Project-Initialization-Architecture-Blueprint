import { act, cleanup, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { readState, useDashboard } from './useDashboard';

const diagnostic = {
  action: 'system.health', checkedAtUtc: '2026-11-10T12:00:00Z',
  controlPlane: 'ok', postgres: 'ok', redis: 'unavailable', worker: 'not-checked',
};
const task = {
  id: '01234567-89ab-cdef-0123-456789abcdef', title: 'Synthetic task', description: 'Not a real task',
  status: 'Executing', attemptCount: 0, result: null, errorMessage: null,
  createdAtUtc: diagnostic.checkedAtUtc, updatedAtUtc: diagnostic.checkedAtUtc,
};
const payload = (url: string) => url === '/api/health' ? { status: 'ok', service: 'control-plane' }
  : url === '/api/diagnostics/local' ? diagnostic : [];
const flush = async () => { await act(async () => { await Promise.resolve(); }); };
const success = () => vi.fn((url: string) => Promise.resolve(Response.json(payload(url))));

beforeEach(() => {
  vi.useFakeTimers();
  vi.setSystemTime(new Date('2026-11-10T12:00:00Z'));
  vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('visible');
  vi.spyOn(navigator, 'onLine', 'get').mockReturnValue(true);
});
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); vi.useRealTimers(); });

describe('Dashboard bounded HTTP reads', () => {
  it('issues only four credential-free GETs, at most two at once, and coalesces refresh', async () => {
    let active = 0;
    let peak = 0;
    const pending: (() => void)[] = [];
    const fetch = vi.fn((url: string, options: RequestInit) => {
      expect(options.method).toBe('GET');
      expect(options.credentials).toBe('omit');
      expect(options.body).toBeUndefined();
      active++; peak = Math.max(peak, active);
      return new Promise<Response>(resolve => pending.push(() => { active--; resolve(Response.json(payload(url))); }));
    });
    vi.stubGlobal('fetch', fetch);
    const { result } = renderHook(() => useDashboard());
    expect(fetch).toHaveBeenCalledTimes(2);
    const first = result.current.refresh();
    expect(result.current.refresh()).toBe(first);
    await act(async () => { pending.shift()?.(); });
    expect(fetch).toHaveBeenCalledTimes(3);
    await act(async () => { pending.shift()?.(); });
    expect(fetch).toHaveBeenCalledTimes(4);
    await act(async () => { pending.splice(0).forEach(resolve => resolve()); await first; });
    expect(peak).toBe(2);
    expect(fetch.mock.calls.map(call => call[0])).toEqual(['/api/health', '/api/tasks', '/api/plans', '/api/diagnostics/local']);
    expect(result.current.tasks.data).toEqual([]);
    expect(result.current.loading).toBe(false);
  });

  it('polls once per 30s with no within-cycle retries, retaining timestamped stale data on failure', async () => {
    const fetch = success(); vi.stubGlobal('fetch', fetch);
    const { result } = renderHook(() => useDashboard());
    await flush();
    const fetchedAt = result.current.tasks.fetchedAt;
    expect(fetch).toHaveBeenCalledTimes(4);
    fetch.mockRejectedValue(new TypeError('offline'));
    await act(async () => { await vi.advanceTimersByTimeAsync(29999); });
    expect(fetch).toHaveBeenCalledTimes(4);
    await act(async () => { await vi.advanceTimersByTimeAsync(1); });
    expect(fetch).toHaveBeenCalledTimes(8);
    expect(result.current.tasks.data).toEqual([]);
    expect(result.current.tasks.fetchedAt).toBe(fetchedAt);
    expect(readState(result.current.tasks, result.current.now, true)).toBe('stale');
    await act(async () => { await vi.advanceTimersByTimeAsync(30000); });
    expect(fetch).toHaveBeenCalledTimes(12);
  });

  it('does not use a pre-write in-flight task read to unlock an ambiguous Draft submission', async () => {
    const pending: (() => void)[] = [];
    const fetch = vi.fn((url: string) => new Promise<Response>(resolve => pending.push(() => resolve(Response.json(payload(url))))));
    vi.stubGlobal('fetch', fetch);
    const { result } = renderHook(() => useDashboard());
    const check = result.current.refreshAfterWrite();
    expect(fetch).toHaveBeenCalledTimes(2);
    await act(async () => { pending.splice(0).forEach(resolve => resolve()); });
    expect(fetch).toHaveBeenCalledTimes(4);
    await act(async () => { pending.splice(0).forEach(resolve => resolve()); });
    expect(fetch).toHaveBeenCalledTimes(6);
    await act(async () => { pending.splice(0).forEach(resolve => resolve()); });
    expect(fetch).toHaveBeenCalledTimes(8);
    await act(async () => { pending.splice(0).forEach(resolve => resolve()); });
    expect(await check).toBe(true);
  });

  it.each([
    [401, 'unauthenticated'], [403, 'forbidden'], [503, 'unavailable'],
  ] as const)('reports HTTP %s as %s, never empty/healthy', async (status, state) => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(new Response('', { status }))));
    const { result } = renderHook(() => useDashboard());
    await flush();
    expect(result.current.tasks.data).toBeNull();
    expect(result.current.health.data).toBeNull();
    expect(readState(result.current.tasks, result.current.now, true)).toBe(state);
    expect(result.current.tasks.fetchedAt).toBeNull();
    expect(result.current.loading).toBe(false);
  });

  it('keeps successful panels when another has malformed JSON or a false health response', async () => {
    vi.stubGlobal('fetch', vi.fn((url: string) => Promise.resolve(url === '/api/tasks' ? new Response('{')
      : url === '/api/health' ? Response.json({ status: 'ok', service: 'not-the-control-plane' }) : Response.json(payload(url)))));
    const { result } = renderHook(() => useDashboard());
    await flush();
    expect(result.current.tasks.failure).toBe('invalid-contract');
    expect(result.current.health.failure).toBe('invalid-contract');
    expect(result.current.plans.data).toEqual([]);
    expect(result.current.diagnostics.data).toEqual(diagnostic);
  });

  it('cancels a stalled cycle at 8s per read without retries', async () => {
    const fetch = vi.fn((_url: string, options: RequestInit) => new Promise<Response>((_resolve, reject) => {
      options.signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')));
    }));
    vi.stubGlobal('fetch', fetch);
    const { result } = renderHook(() => useDashboard());
    await act(async () => { await vi.advanceTimersByTimeAsync(8000); });
    expect(fetch).toHaveBeenCalledTimes(4);
    expect(result.current.tasks.failure).toBe('unavailable');
    expect(result.current.tasks.error).toContain('timed out');
    await act(async () => { await vi.advanceTimersByTimeAsync(8000); });
    expect(fetch).toHaveBeenCalledTimes(4);
    expect(result.current.loading).toBe(false);
  });

  it('aborts on hidden/offline and ignores late responses even when the transport ignores abort', async () => {
    const pending: (() => void)[] = [];
    const fetch = vi.fn((url: string) => new Promise<Response>(resolve => {
      pending.push(() => resolve(Response.json(url === '/api/tasks' ? [task] : payload(url))));
    }));
    vi.stubGlobal('fetch', fetch);
    const { result } = renderHook(() => useDashboard());
    vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('hidden');
    act(() => document.dispatchEvent(new Event('visibilitychange')));
    await act(async () => { pending.splice(0).forEach(resolve => resolve()); await vi.advanceTimersByTimeAsync(60000); });
    expect(fetch).toHaveBeenCalledTimes(2);
    expect(result.current.tasks.data).toBeNull();
    vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('visible');
    vi.spyOn(navigator, 'onLine', 'get').mockReturnValue(false);
    act(() => window.dispatchEvent(new Event('offline')));
    expect(readState(result.current.tasks, result.current.now, result.current.online)).toBe('offline');
    vi.spyOn(navigator, 'onLine', 'get').mockReturnValue(true);
    act(() => { window.dispatchEvent(new Event('online')); document.dispatchEvent(new Event('visibilitychange')); });
    expect(fetch).toHaveBeenCalledTimes(4);
  });

  it('clears data on context change and never accepts a previous generation response', async () => {
    const fetch = success(); vi.stubGlobal('fetch', fetch);
    const { result, rerender } = renderHook(({ context }) => useDashboard(context), { initialProps: { context: 'a' } });
    await flush();
    expect(result.current.tasks.data).toEqual([]);
    const pending: (() => void)[] = [];
    fetch.mockImplementation(url => new Promise<Response>(resolve => pending.push(() => resolve(Response.json(payload(url))))));
    act(() => { void result.current.refresh(); });
    rerender({ context: 'b' });
    expect(result.current.tasks.data).toBeNull();
    await act(async () => { pending.splice(0, 2).forEach(resolve => resolve()); });
    expect(result.current.tasks.data).toBeNull();
  });

  it.each(['hic-operator-session-ended', 'hic-dashboard-context-changed'])('clears and suspends the snapshot on %s', async event => {
    const fetch = success(); vi.stubGlobal('fetch', fetch);
    const { result } = renderHook(() => useDashboard());
    await flush();
    act(() => window.dispatchEvent(new Event(event)));
    expect(result.current.tasks.data).toBeNull();
    expect(result.current.tasks.failure).toBe('unauthenticated');
    await act(async () => { await vi.advanceTimersByTimeAsync(60000); await result.current.refresh(); });
    expect(fetch).toHaveBeenCalledTimes(4);
  });

  it('erases old successful values on a permission failure and rejects late concurrent success', async () => {
    const fetch = success(); vi.stubGlobal('fetch', fetch);
    const { result } = renderHook(() => useDashboard());
    await flush();
    let finishTasks: (() => void) | undefined;
    fetch.mockImplementation(url => url === '/api/health' ? Promise.resolve(new Response('', { status: 401 }))
      : new Promise<Response>(resolve => { finishTasks = () => resolve(Response.json([task])); }));
    act(() => { void result.current.refresh(); });
    await flush();
    await act(async () => { finishTasks?.(); });
    expect(result.current.tasks.data).toBeNull();
    expect(result.current.health.data).toBeNull();
    expect(result.current.tasks.failure).toBe('unauthenticated');
  });

  it('aborts active HTTP reads and releases timers/listeners on unmount', async () => {
    const signals: AbortSignal[] = [];
    const fetch = vi.fn((_url: string, options: RequestInit) => {
      if (options.signal) signals.push(options.signal);
      return new Promise<Response>(() => {});
    });
    vi.stubGlobal('fetch', fetch);
    const { unmount } = renderHook(() => useDashboard());
    unmount();
    expect(signals.every(signal => signal.aborted)).toBe(true);
    await act(async () => { await vi.advanceTimersByTimeAsync(60000); });
    expect(fetch).toHaveBeenCalledTimes(2);
    expect(vi.getTimerCount()).toBe(0);
  });

  it('ages successful data to stale at exactly 60s, distinct from successful empty', () => {
    const resource = { data: [], error: null, failure: null, fetchedAt: 0 };
    expect(readState(resource, 59999, true)).toBe('empty');
    expect(readState(resource, 60000, true)).toBe('stale');
    expect(readState({ ...resource, data: null, fetchedAt: null }, 60000, true)).toBe('loading');
    expect(readState(resource, 60000, false)).toBe('offline');
  });
});
