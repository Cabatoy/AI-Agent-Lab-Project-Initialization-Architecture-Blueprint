export interface Health { status: string; service: string }
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
const task: Guard<Task> = (v): v is Task => object(v) && guid(v.id) && str(v.title) &&
  str(v.description) && str(v.status) && Number.isSafeInteger(v.attemptCount) && Number(v.attemptCount) >= 0 &&
  nullable(v.result) && nullable(v.errorMessage) && date(v.createdAtUtc) && date(v.updatedAtUtc);
const plan: Guard<Plan> = (v): v is Plan => object(v) && guid(v.id) && nullable(v.projectKey) &&
  str(v.requestTitle) && nullable(v.requestDescription) && str(v.status) && str(v.planSummary) &&
  date(v.createdAtUtc) && date(v.updatedAtUtc);
const list = <T>(guard: Guard<T>): Guard<T[]> => (v): v is T[] => Array.isArray(v) && v.every(guard);

export class ApiError extends Error {
  constructor(public kind: 'network' | 'timeout' | 'http' | 'invalid', message: string, public status?: number) {
    super(message);
    this.name = 'ApiError';
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
  tasks: () => get('/tasks', list(task)),
  task: (id: string) => {
    if (!guid(id)) return Promise.reject(new ApiError('invalid', 'Invalid task identifier.'));
    return get(`/tasks/${id}`, task);
  },
  plans: () => get('/plans', list(plan)),
};
