import { COMMAND_CENTER_HEADER, COMMAND_CENTER_HEADER_VALUE, type CreateTaskInput, validateCreateTask } from './limits';

export type { CreateTaskInput } from './limits';
export interface Health { status: string; service: string }
export interface LocalDiagnosticSnapshot {
  action: 'system.health';
  checkedAtUtc: string;
  controlPlane: 'ok';
  postgres: 'ok' | 'unavailable';
  redis: 'ok' | 'unavailable';
  worker: 'not-checked';
}
export interface Task {
  id: string;
  title: string;
  description: string;
  status: string;
  attemptCount: number;
  result: string | null;
  errorMessage: string | null;
  createdAtUtc: string;
  updatedAtUtc: string;
}
export interface Plan {
  id: string;
  projectKey: string | null;
  requestTitle: string;
  requestDescription: string | null;
  status: string;
  planSummary: string;
  createdAtUtc: string;
  updatedAtUtc: string;
}

type Guard<T> = (value: unknown) => value is T;
const object = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v);
const str = (v: unknown): v is string => typeof v === 'string';
const nullable = (v: unknown): v is string | null => v === null || str(v);
const date = (v: unknown): v is string => str(v) && !Number.isNaN(Date.parse(v));
const guid = (v: unknown): v is string => str(v) && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(v);
const health: Guard<Health> = (v): v is Health => object(v) && str(v.status) && str(v.service);
const localDiagnostics: Guard<LocalDiagnosticSnapshot> = (v): v is LocalDiagnosticSnapshot =>
  object(v) && v.action === 'system.health' && date(v.checkedAtUtc) &&
  v.controlPlane === 'ok' && (v.postgres === 'ok' || v.postgres === 'unavailable') &&
  (v.redis === 'ok' || v.redis === 'unavailable') && v.worker === 'not-checked';
const task: Guard<Task> = (v): v is Task => object(v) && guid(v.id) && str(v.title) &&
  str(v.description) && str(v.status) && Number.isSafeInteger(v.attemptCount) && Number(v.attemptCount) >= 0 &&
  nullable(v.result) && nullable(v.errorMessage) && date(v.createdAtUtc) && date(v.updatedAtUtc);
const plan: Guard<Plan> = (v): v is Plan => object(v) && guid(v.id) && nullable(v.projectKey) &&
  str(v.requestTitle) && nullable(v.requestDescription) && str(v.status) && str(v.planSummary) &&
  date(v.createdAtUtc) && date(v.updatedAtUtc);
const list = <T>(guard: Guard<T>): Guard<T[]> => (v): v is T[] => Array.isArray(v) && v.every(guard);

export class ApiError extends Error {
  constructor(public kind: 'network' | 'timeout' | 'http' | 'invalid', message: string, public status?: number,
    /** True when a write may have reached the Control Plane even though no confirmation was received. */
    public outcomeUnknown = false) {
    super(message);
    this.name = 'ApiError';
  }
}

const UNKNOWN_OUTCOME = 'The Control Plane did not confirm the request. The task may or may not have been created — check the Tasks list before submitting again.';
const SERVER_MESSAGE_MAX = 200;

async function serverMessage(response: Response): Promise<string | null> {
  try {
    const text = (await response.text()).trim();
    if (!text) return null;
    let message: unknown = text;
    if ((response.headers.get('content-type') ?? '').includes('json')) {
      const body: unknown = JSON.parse(text);
      message = object(body) ? body.error ?? body.title ?? body.detail : null;
    }
    return str(message) && message.length <= SERVER_MESSAGE_MAX && !/[\r\n]/.test(message) ? message : null;
  } catch {
    return null;
  }
}

async function createTask(input: CreateTaskInput, timeoutMs = 15000): Promise<Task> {
  const validation = validateCreateTask(input);
  if (!validation.ok) throw new ApiError('invalid', validation.error);
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  try {
    let response: Response;
    try {
      response = await fetch('/api/tasks', {
        method: 'POST',
        signal: controller.signal,
        cache: 'no-store',
        credentials: 'omit',
        headers: { 'Content-Type': 'application/json', Accept: 'application/json', [COMMAND_CENTER_HEADER]: COMMAND_CENTER_HEADER_VALUE },
        body: JSON.stringify(validation.value),
      });
    } catch {
      throw new ApiError(controller.signal.aborted ? 'timeout' : 'network', UNKNOWN_OUTCOME, undefined, true);
    }
    if (!response.ok) {
      const proxyError = response.headers.get('X-HIC-Proxy-Error');
      if (proxyError === 'unreachable') throw new ApiError('network', 'Control Plane is unreachable. No task was created.');
      if (proxyError === 'timeout') throw new ApiError('timeout', UNKNOWN_OUTCOME, response.status, true);
      if (proxyError === 'interrupted') throw new ApiError('network', UNKNOWN_OUTCOME, response.status, true);
      const detail = await serverMessage(response);
      const source = proxyError === 'rejected' ? 'The local proxy rejected the request' : 'Control Plane returned';
      // A 5xx from the Control Plane may have occurred after the insert, so it is also treated as uncertain.
      const uncertain = proxyError !== 'rejected' && response.status >= 500;
      const summary = proxyError === 'rejected' ? `${source} (HTTP ${response.status})` : `${source} HTTP ${response.status}`;
      throw new ApiError('http', uncertain ? `${summary}. ${UNKNOWN_OUTCOME}` : `${summary}${detail ? `: ${detail}` : '.'} No task was created.`,
        response.status, uncertain);
    }
    let data: unknown;
    try {
      data = await response.json();
    } catch {
      throw new ApiError('invalid', `Control Plane returned an unreadable response. ${UNKNOWN_OUTCOME}`, response.status, true);
    }
    if (response.status !== 201 || !task(data) || data.status !== 'Draft') {
      throw new ApiError('invalid', `Control Plane response does not match the expected Draft task contract. ${UNKNOWN_OUTCOME}`,
        response.status, true);
    }
    return data;
  } finally {
    clearTimeout(timer);
  }
}

async function get<T>(path: string, guard: Guard<T>, timeoutMs = 8000): Promise<T> {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  try {
    let response: Response;
    try {
      response = await fetch(`/api${path}`, { method: 'GET', signal: controller.signal, cache: 'no-store' });
    } catch {
      throw new ApiError(controller.signal.aborted ? 'timeout' : 'network',
        controller.signal.aborted ? 'Control Plane request timed out.' : 'Control Plane is unreachable.');
    }
    if (!response.ok) {
      const proxyError = response.headers.get('X-HIC-Proxy-Error');
      if (proxyError === 'unreachable') throw new ApiError('network', 'Control Plane is unreachable.');
      if (proxyError === 'timeout') throw new ApiError('timeout', 'Control Plane request timed out.');
      throw new ApiError('http', `Control Plane returned HTTP ${response.status}.`, response.status);
    }
    let data: unknown;
    try {
      data = await response.json();
    } catch {
      if (controller.signal.aborted) throw new ApiError('timeout', 'Control Plane request timed out.');
      throw new ApiError('invalid', 'Control Plane returned invalid JSON.');
    }
    if (!guard(data)) throw new ApiError('invalid', 'Control Plane response does not match the expected contract.');
    return data;
  } finally {
    clearTimeout(timer);
  }
}

export const api = {
  health: () => get('/health', health),
  localDiagnostics: () => get('/diagnostics/local', localDiagnostics),
  tasks: () => get('/tasks', list(task)),
  task: (id: string) => {
    if (!guid(id)) return Promise.reject(new ApiError('invalid', 'Invalid task identifier.'));
    return get(`/tasks/${id}`, task);
  },
  plans: () => get('/plans', list(plan)),
  createTask,
};
