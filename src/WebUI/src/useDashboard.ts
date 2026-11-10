import { useCallback, useEffect, useRef, useState } from 'react';
import { api, ApiError, type Health, type LocalDiagnosticSnapshot, type Plan, type Task } from './api';

export type ReadState = 'loading' | 'empty' | 'ready' | 'stale' | 'offline' |
  'unauthenticated' | 'forbidden' | 'invalid-contract' | 'unavailable';
export interface Resource<T> {
  data: T | null;
  error: string | null;
  failure: ReadState | null;
  fetchedAt: number | null;
}
const initial = <T,>(): Resource<T> => ({ data: null, error: null, failure: null, fetchedAt: null });
export const initialDashboard = () => ({
  health: initial<Health>(), tasks: initial<Task[]>(), plans: initial<Plan[]>(),
  diagnostics: initial<LocalDiagnosticSnapshot>(),
});
type Snapshot = ReturnType<typeof initialDashboard>;

export function readState<T>(resource: Resource<T>, now: number, online: boolean): ReadState {
  if (!online) return 'offline';
  if (resource.data !== null && (resource.failure || (resource.fetchedAt !== null && now - resource.fetchedAt >= 60000))) return 'stale';
  if (resource.failure) return resource.failure;
  if (resource.data === null) return 'loading';
  return Array.isArray(resource.data) && resource.data.length === 0 ? 'empty' : 'ready';
}

function failure(reason: unknown): { failure: ReadState; error: string } {
  if (reason instanceof ApiError) {
    if (reason.status === 401) return { failure: 'unauthenticated', error: 'Access requires authentication. Reload after correcting access; local data is not a tenant login.' };
    if (reason.status === 403) return { failure: 'forbidden', error: 'Access denied. Reload only after access is corrected.' };
    if (reason.kind === 'invalid') return { failure: 'invalid-contract', error: 'Invalid API contract. Data is unknown.' };
    if (reason.kind === 'network') return { failure: 'offline', error: reason.message };
    return { failure: 'unavailable', error: reason.message };
  }
  return { failure: 'unavailable', error: 'Control Plane data is unavailable.' };
}

export function useDashboard(contextKey = 'local-loopback') {
  const [snapshot, setSnapshot] = useState(initialDashboard);
  const [loading, setLoading] = useState(false);
  const [online, setOnline] = useState(navigator.onLine);
  const [now, setNow] = useState(Date.now);
  const epoch = useRef(0);
  const active = useRef(false);
  const suspended = useRef(false);
  const cycle = useRef<{ controller: AbortController; promise: Promise<boolean> } | null>(null);

  const cancel = useCallback((clear = false) => {
    epoch.current++;
    cycle.current?.controller.abort();
    cycle.current = null;
    setLoading(false);
    if (clear) setSnapshot(initialDashboard());
  }, []);

  const refresh = useCallback((): Promise<boolean> => {
    if (!active.current || suspended.current || document.visibilityState === 'hidden' || !navigator.onLine) return Promise.resolve(false);
    if (cycle.current) return cycle.current.promise;
    const generation = epoch.current;
    const controller = new AbortController();
    const current = () => active.current && epoch.current === generation && !controller.signal.aborted;
    setLoading(true);
    let taskSuccess = false;
    const read = async <K extends keyof Snapshot>(key: K, request: () => Promise<NonNullable<Snapshot[K]['data']>>) => {
      try {
        const data = await request();
        if (!current()) return;
        if (key === 'tasks') taskSuccess = true;
        setSnapshot(previous => ({ ...previous, [key]: { data, error: null, failure: null, fetchedAt: Date.now() } }));
      } catch (reason) {
        if (!current()) return;
        const error = failure(reason);
        if (error.failure === 'unauthenticated' || error.failure === 'forbidden') {
          // Permission failures invalidate the whole local snapshot, including concurrent reads.
          controller.abort();
          suspended.current = true;
          setSnapshot({
            health: { ...initial(), ...error }, tasks: { ...initial(), ...error },
            plans: { ...initial(), ...error }, diagnostics: { ...initial(), ...error },
          });
        } else {
          setSnapshot(previous => ({ ...previous, [key]: { ...previous[key], ...error } }));
        }
      }
    };
    const jobs = [
      () => read('health', () => api.health(controller.signal)),
      () => read('tasks', () => api.tasks(controller.signal)),
      () => read('plans', () => api.plans(controller.signal)),
      () => read('diagnostics', () => api.localDiagnostics(controller.signal)),
    ];
    let next = 0;
    const worker = async () => {
      while (current() && next < jobs.length) await jobs[next++]();
    };
    const promise = Promise.all([worker(), worker()]).then(() => current() && taskSuccess).finally(() => {
      if (active.current && epoch.current === generation) {
        cycle.current = null;
        setLoading(false);
        setNow(Date.now());
      }
    });
    cycle.current = { controller, promise };
    return promise;
  }, []);

  useEffect(() => {
    active.current = true;
    suspended.current = false;
    setSnapshot(initialDashboard());
    const availability = () => {
      setOnline(navigator.onLine);
      setNow(Date.now());
      if (document.visibilityState === 'hidden' || !navigator.onLine) cancel();
      else void refresh();
    };
    const endSession = () => {
      suspended.current = true;
      cancel(true);
      const error = { failure: 'unauthenticated' as const, error: 'Session/context ended. Reload for the new local context.' };
      setSnapshot({
        health: { ...initial(), ...error }, tasks: { ...initial(), ...error },
        plans: { ...initial(), ...error }, diagnostics: { ...initial(), ...error },
      });
    };
    document.addEventListener('visibilitychange', availability);
    window.addEventListener('online', availability);
    window.addEventListener('offline', availability);
    window.addEventListener('hic-operator-session-ended', endSession);
    window.addEventListener('hic-dashboard-context-changed', endSession);
    const interval = setInterval(() => { void refresh(); }, 30000);
    const clock = setInterval(() => setNow(Date.now()), 1000);
    void refresh();
    return () => {
      active.current = false;
      epoch.current++;
      cycle.current?.controller.abort();
      cycle.current = null;
      clearInterval(interval);
      clearInterval(clock);
      document.removeEventListener('visibilitychange', availability);
      window.removeEventListener('online', availability);
      window.removeEventListener('offline', availability);
      window.removeEventListener('hic-operator-session-ended', endSession);
      window.removeEventListener('hic-dashboard-context-changed', endSession);
    };
  }, [contextKey, cancel, refresh]);

  const refreshAfterWrite = useCallback(async () => {
    // An earlier in-flight read cannot resolve an ambiguous Draft submission.
    if (cycle.current) await cycle.current.promise;
    return refresh();
  }, [refresh]);

  return { ...snapshot, loading, online, now, refresh, refreshAfterWrite };
}
