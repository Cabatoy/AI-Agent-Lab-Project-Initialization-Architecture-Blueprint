import { COMMAND_CENTER_HEADER, COMMAND_CENTER_HEADER_VALUE, type CreateTaskInput, validateCreateTask } from './limits';

export type { CreateTaskInput } from './limits';
export interface Health { status: string; service: string }
export interface NativeWorkspaceInspection {
  action: 'workspace.inspect';
  runId: string;
  checkedAtUtc: string;
  workspaceAvailable: boolean;
  gitCheckoutFound: boolean;
  solutionFound: boolean;
}

export interface LocalDiagnosticSnapshot {
  action: 'system.health';
  checkedAtUtc: string;
  controlPlane: 'ok';
  postgres: 'ok' | 'unavailable';
  redis: 'ok' | 'unavailable';
  worker: 'not-checked';
}
export interface LocalRunnerSession {
  accessToken: string;
  expiresInSeconds: number;
}
export interface LocalRunnerCapability {
  actorId: string;
  action: 'system.health';
  scope: 'control-plane:health:read';
  worker: 'not-used';
  provider: 'not-used';
}
export interface OperatorSession {
  accessToken: string;
  expiresAt: number;
  capability: LocalRunnerCapability;
}
export type TaskApproval = { id: string; status: 'Approved'; queued: true } |
  { id: string; status: 'Approved'; queued: false; pendingRecovery: true };
export interface LocalHealthIntent {
  taskId: string;
  taskTitle: string;
  action: 'system.health';
  scope: 'control-plane:health:read';
  taskUpdatedAtUtc: string;
  intentDigestSha256: string;
}
export interface LocalHealthRunEvent {
  eventCode: string;
  recordedAtUtc: string;
}
export interface LocalHealthRunApproval {
  runId: string;
  taskId: string;
  action: 'system.health';
  scope: 'control-plane:health:read';
  intentDigestSha256: string;
  status: 'queued' | 'running' | 'completed' | 'expired' | 'invalidated';
}
export interface LocalHealthRunSnapshot {
  runId: string;
  taskId: string;
  action: 'system.health';
  scope: 'control-plane:health:read';
  intentDigestSha256: string;
  status: 'queued' | 'running' | 'completed' | 'expired' | 'invalidated';
  createdAtUtc: string;
  approvedAtUtc: string;
  expiresAtUtc: string;
  startedAtUtc: string | null;
  completedAtUtc: string | null;
  result: LocalDiagnosticSnapshot | null;
  events: LocalHealthRunEvent[];
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
const nativeWorkspace: Guard<NativeWorkspaceInspection> = (v): v is NativeWorkspaceInspection =>
  object(v) && v.action === 'workspace.inspect' && guid(v.runId) && date(v.checkedAtUtc) &&
  typeof v.workspaceAvailable === 'boolean' && typeof v.gitCheckoutFound === 'boolean' &&
  typeof v.solutionFound === 'boolean';
const health: Guard<Health> = (v): v is Health => object(v) && str(v.status) && str(v.service);
const localDiagnostics: Guard<LocalDiagnosticSnapshot> = (v): v is LocalDiagnosticSnapshot =>
  object(v) && v.action === 'system.health' && date(v.checkedAtUtc) &&
  v.controlPlane === 'ok' && (v.postgres === 'ok' || v.postgres === 'unavailable') &&
  (v.redis === 'ok' || v.redis === 'unavailable') && v.worker === 'not-checked';
const localRunnerSession: Guard<LocalRunnerSession> = (v): v is LocalRunnerSession =>
  object(v) && str(v.accessToken) && v.accessToken.length >= 40 && v.accessToken.length <= 64 &&
  Number.isSafeInteger(v.expiresInSeconds) && Number(v.expiresInSeconds) > 0;
const localRunnerCapability: Guard<LocalRunnerCapability> = (v): v is LocalRunnerCapability =>
  object(v) && guid(v.actorId) && v.action === 'system.health' &&
  v.scope === 'control-plane:health:read' && v.worker === 'not-used' && v.provider === 'not-used';
const localHealthIntent: Guard<LocalHealthIntent> = (v): v is LocalHealthIntent =>
  object(v) && guid(v.taskId) && str(v.taskTitle) && v.action === 'system.health' &&
  v.scope === 'control-plane:health:read' && date(v.taskUpdatedAtUtc) &&
  str(v.intentDigestSha256) && /^[0-9a-f]{64}$/.test(v.intentDigestSha256);
const localHealthEvent: Guard<LocalHealthRunEvent> = (v): v is LocalHealthRunEvent =>
  object(v) && str(v.eventCode) && date(v.recordedAtUtc);
const localHealthRunApproval: Guard<LocalHealthRunApproval> = (v): v is LocalHealthRunApproval =>
  object(v) && guid(v.runId) && guid(v.taskId) && v.action === 'system.health' &&
  v.scope === 'control-plane:health:read' && str(v.intentDigestSha256) &&
  /^[0-9a-f]{64}$/.test(v.intentDigestSha256) &&
  ['queued', 'running', 'completed', 'expired', 'invalidated'].includes(String(v.status));
const localHealthRun: Guard<LocalHealthRunSnapshot> = (v): v is LocalHealthRunSnapshot =>
  object(v) && guid(v.runId) && guid(v.taskId) && v.action === 'system.health' &&
  v.scope === 'control-plane:health:read' && str(v.intentDigestSha256) &&
  /^[0-9a-f]{64}$/.test(v.intentDigestSha256) &&
  ['queued', 'running', 'completed', 'expired', 'invalidated'].includes(String(v.status)) &&
  date(v.createdAtUtc) && date(v.approvedAtUtc) && date(v.expiresAtUtc) &&
  (v.startedAtUtc === null || date(v.startedAtUtc)) &&
  (v.completedAtUtc === null || date(v.completedAtUtc)) &&
  (v.result === null || localDiagnostics(v.result)) &&
  list(localHealthEvent)(v.events);
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

async function localRunnerRequest<T>(
  path: string, guard: Guard<T>, token: string, method: 'GET' | 'POST',
  body?: unknown, expectedStatus = 200, timeoutMs = 15000,
): Promise<T> {
  if (!token || token.length > 128) throw new ApiError('invalid', 'Local runner session is unavailable.');
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  try {
    let response: Response;
    try {
      response = await fetch(`/api/local-runner${path}`, {
        method, signal: controller.signal, cache: 'no-store', credentials: 'omit',
        headers: {
          Accept: 'application/json',
          'X-HIC-Request': 'local-runner',
          Authorization: `Bearer ${token}`,
          ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
        },
        ...(body === undefined ? {} : { body: JSON.stringify(body) }),
      });
    } catch {
      throw new ApiError(controller.signal.aborted ? 'timeout' : 'network',
        'The local runner did not confirm the request.', undefined, method === 'POST');
    }
    if (response.status !== expectedStatus) {
      const message = response.status === 401 ? 'The local runner session is invalid or expired.'
        : response.status === 503 ? 'The local runner is not registered or its approved schema is unavailable.'
          : response.status === 409 ? 'The task or approved run is no longer eligible.'
            : `The local runner returned HTTP ${response.status}.`;
      throw new ApiError('http', message, response.status, method === 'POST' && response.status >= 500);
    }
    let data: unknown;
    try { data = await response.json(); }
    catch { throw new ApiError('invalid', 'The local runner response was not valid JSON.', response.status, method === 'POST'); }
    if (!guard(data)) throw new ApiError('invalid', 'The local runner response did not match its contract.', response.status, method === 'POST');
    return data;
  } finally {
    clearTimeout(timer);
  }
}

async function createLocalRunnerSession(bootstrapToken: string): Promise<LocalRunnerSession> {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), 8000);
  try {
    const response = await fetch('/api/local-runner/session', {
      method: 'POST', signal: controller.signal, cache: 'no-store', credentials: 'omit',
      headers: { 'Content-Type': 'application/json', Accept: 'application/json', 'X-HIC-Request': 'local-runner' },
      body: JSON.stringify({ bootstrapToken }),
    }).catch(() => { throw new ApiError(controller.signal.aborted ? 'timeout' : 'network', 'Local runner registration is unavailable.'); });
    if (response.status !== 200) {
      throw new ApiError(response.status === 401 ? 'http' : 'network',
        response.status === 503 ? 'Local runner registration or schema is unavailable.' : 'Local runner registration was rejected.',
        response.status);
    }
    const data: unknown = await response.json().catch(() => null);
    if (!localRunnerSession(data)) throw new ApiError('invalid', 'Local runner session response did not match its contract.');
    return data;
  } finally {
    clearTimeout(timer);
  }
}

async function approveTask(id: string, token: string): Promise<TaskApproval> {
  if (!guid(id) || !/^[A-Za-z0-9+/_=-]{40,128}$/.test(token))
    throw new ApiError('invalid', 'Invalid task identifier or operator session.');
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), 15000);
  const unknown = 'Approval may have been saved. Check the task status; do not approve it again.';
  try {
    let response: Response;
    try {
      response = await fetch(`/api/tasks/${id}/approve`, {
        method: 'POST', signal: controller.signal, cache: 'no-store', credentials: 'omit',
        headers: { Accept: 'application/json', 'X-HIC-Request': 'task-approval', Authorization: `Bearer ${token}` },
      });
    } catch {
      throw new ApiError(controller.signal.aborted ? 'timeout' : 'network', unknown, undefined, true);
    }
    if (response.status !== 200 && response.status !== 202) {
      const proxyError = response.headers.get('X-HIC-Proxy-Error');
      const uncertain = proxyError !== 'rejected' && proxyError !== 'unreachable' && response.status >= 500;
      const detail = await serverMessage(response);
      throw new ApiError('http', `Task approval returned HTTP ${response.status}${detail ? `: ${detail}` : '.'}${uncertain ? ` ${unknown}` : ''}`,
        response.status, uncertain);
    }
    let data: unknown;
    try { data = await response.json(); }
    catch { throw new ApiError('invalid', `Invalid approval response. ${unknown}`, response.status, true); }
    if (!object(data) || data.id !== id || data.status !== 'Approved' ||
        (response.status === 200 ? data.queued !== true : data.queued !== false || data.pendingRecovery !== true))
      throw new ApiError('invalid', `Unexpected approval response. ${unknown}`, response.status, true);
    return response.status === 200
      ? { id, status: 'Approved', queued: true }
      : { id, status: 'Approved', queued: false, pendingRecovery: true };
  } finally { clearTimeout(timer); }
}

async function inspectNativeWorkspace(timeoutMs = 8000): Promise<NativeWorkspaceInspection> {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  try {
    let response: Response;
    try {
      response = await fetch('/api/native/actions', {
        method: 'POST', signal: controller.signal, cache: 'no-store', credentials: 'omit',
        headers: { 'Content-Type': 'application/json', Accept: 'application/json', 'X-HIC-Request': 'native-action' },
        body: JSON.stringify({ action: 'workspace.inspect' }),
      });
    } catch {
      throw new ApiError(controller.signal.aborted ? 'timeout' : 'network', 'Native host is unavailable.');
    }
    if (!response.ok) throw new ApiError('http', 'Native host rejected the read-only action.', response.status);
    let body: unknown;
    try { body = await response.json(); }
    catch { throw new ApiError('invalid', 'Native host response was not valid JSON.'); }
    if (!nativeWorkspace(body)) throw new ApiError('invalid', 'Native host response did not match the expected contract.');
    return body;
  } finally { clearTimeout(timer); }
}

export const api = {
  inspectNativeWorkspace,
  approveTask,
  createLocalRunnerSession,
  localRunnerCapabilities: (token: string) =>
    localRunnerRequest('/capabilities', localRunnerCapability, token, 'GET'),
  localHealthIntent: (taskId: string, token: string) => {
    if (!guid(taskId)) return Promise.reject(new ApiError('invalid', 'Invalid task identifier.'));
    return localRunnerRequest(`/tasks/${taskId}/intent`, localHealthIntent, token, 'GET');
  },
  approveLocalHealthRun: (taskId: string, intentDigestSha256: string, token: string) => {
    if (!guid(taskId) || !/^[0-9a-f]{64}$/.test(intentDigestSha256))
      return Promise.reject(new ApiError('invalid', 'Invalid local run approval intent.'));
    return localRunnerRequest(`/tasks/${taskId}/approve`, localHealthRunApproval, token, 'POST',
      { intentDigestSha256 }, 202);
  },
  executeLocalHealthRun: (runId: string, token: string) => {
    if (!guid(runId)) return Promise.reject(new ApiError('invalid', 'Invalid local run identifier.'));
    return localRunnerRequest(`/runs/${runId}/execute`, object, token, 'POST');
  },
  localHealthRun: (runId: string, token: string) => {
    if (!guid(runId)) return Promise.reject(new ApiError('invalid', 'Invalid local run identifier.'));
    return localRunnerRequest(`/runs/${runId}`, localHealthRun, token, 'GET');
  },
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
