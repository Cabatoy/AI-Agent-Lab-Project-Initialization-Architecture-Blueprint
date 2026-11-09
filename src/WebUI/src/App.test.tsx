import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import App from './App';
import { api, ApiError } from './api';
import { UNSENT_DRAFT_KEY } from './unsentDraft';

vi.mock('./api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('./api')>();
  return { ...actual, api: {
    health: vi.fn(), tasks: vi.fn(), task: vi.fn(), plans: vi.fn(), createTask: vi.fn(),
    localDiagnostics: vi.fn(), inspectNativeWorkspace: vi.fn(),
    restoreLocalRunnerSession: vi.fn(), revokeLocalRunnerSession: vi.fn(),
  } };
});
const item = {
  id: '01234567-89ab-cdef-0123-456789abcdef', title: 'Actual task', description: 'Actual description',
  status: 'Executing', attemptCount: 2, result: 'Actual result', errorMessage: null,
  createdAtUtc: '2026-11-06T12:00:00Z', updatedAtUtc: '2026-11-06T12:00:00Z',
};
beforeEach(() => {
  window.localStorage.clear();
  vi.mocked(api.restoreLocalRunnerSession).mockRejectedValue({ status: 401 });
  vi.mocked(api.revokeLocalRunnerSession).mockResolvedValue({ revoked: true });
  vi.mocked(api.health).mockResolvedValue({ status: 'ok', service: 'control-plane' });
  vi.mocked(api.tasks).mockResolvedValue([]);
  vi.mocked(api.plans).mockResolvedValue([]);
  vi.mocked(api.task).mockResolvedValue(item);
});
afterEach(() => { cleanup(); window.localStorage.clear(); vi.clearAllMocks(); });

describe('dashboard', () => {
  it('shows loading, then real empty states and a successful refresh timestamp', async () => {
    render(<App />);
    expect(screen.getByText('Refreshing Control Plane data…')).toBeInTheDocument();
    expect(await screen.findByText('No tasks yet.')).toBeInTheDocument();
    expect(screen.getByText('Connected')).toBeInTheDocument();
    expect(screen.queryByText('Not yet available')).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Plans' }));
    expect(screen.getByText('No plans yet.')).toBeInTheDocument();
    expect(screen.getByText(/Task relationships and dependency graphs are not exposed/)).toBeInTheDocument();
  });
  it('shows failures instead of presenting them as empty results', async () => {
    vi.mocked(api.tasks).mockRejectedValue(new Error('Control Plane is unreachable.'));
    render(<App />);
    await waitFor(() => expect(screen.getAllByText('Control Plane is unreachable.').length).toBeGreaterThan(0));
    expect(screen.queryByText('No tasks yet.')).not.toBeInTheDocument();
    expect(screen.getByText('Not yet available')).toBeInTheDocument();
  });
  it('opens actual task detail and does not invent execution history', async () => {
    vi.mocked(api.tasks).mockResolvedValue([item]);
    render(<App />);
    fireEvent.click(await screen.findByRole('button', { name: 'Actual task' }));
    expect(await screen.findByText('Actual result')).toBeInTheDocument();
    expect(api.task).toHaveBeenCalledWith(item.id);
    expect(screen.getByText(/Execution history and audit events are not exposed/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Prepare bounded Git plan' })).not.toBeInTheDocument();
  });
  it('navigates a Draft into the bounded Git workflow without approving or executing', async () => {
    const draft = { ...item, status: 'Draft' };
    vi.mocked(api.tasks).mockResolvedValue([draft]);
    vi.mocked(api.task).mockResolvedValue(draft);
    render(<App />);
    fireEvent.click(await screen.findByRole('button', { name: 'Actual task' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Prepare bounded Git plan' }));
    expect(await screen.findByRole('heading', { name: 'Approve an isolated Git edit' })).toBeInTheDocument();
    expect(screen.getByText(/Repository execution is disabled|does not offer repository execution/i)).toBeInTheDocument();
    expect(api.createTask).not.toHaveBeenCalled();
  });
  it('shows actual plans and read-only placeholders without action buttons', async () => {
    vi.mocked(api.plans).mockResolvedValue([{ id: item.id, projectKey: null, requestTitle: 'Actual plan',
      requestDescription: null, status: 'Proposed', planSummary: 'Actual summary',
      createdAtUtc: item.createdAtUtc, updatedAtUtc: item.updatedAtUtc }]);
    render(<App />);
    await screen.findByText('Connected');
    fireEvent.click(screen.getByRole('button', { name: 'Plans' }));
    expect(screen.getByText('Actual plan')).toBeInTheDocument();
    expect(screen.getByText('Actual summary')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /Approvals/ }));
    expect(screen.getByText('Approvals are read-only')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /approve|reject/i })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /Models & Providers/ }));
    expect(await screen.findByText(/Authenticate in Conversations to inspect/)).toBeInTheDocument();
    expect(screen.getByText(/token or budget visibility are not implemented/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /Agents/ }));
    expect(screen.getByText('No persisted agent definitions are available.')).toBeInTheDocument();
  });
  it('opens the local runner without authenticating or starting an action', async () => {
    render(<App />);
    await screen.findByText('Connected');
    fireEvent.click(screen.getByRole('button', { name: 'Local Runner' }));
    expect(screen.getByRole('heading', { name: 'Run a read-only local task' })).toBeInTheDocument();
    expect(screen.getByText('HUMAN-APPROVED LOCAL RUN')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Authenticate local operator' })).toBeDisabled();
    expect(api.createTask).not.toHaveBeenCalled();
    expect(api.localDiagnostics).not.toHaveBeenCalled();
  });
  it('opens the human-controlled local planning workflow', async () => {
    render(<App />);
    await screen.findByText('Connected');
    fireEvent.click(screen.getByRole('button', { name: 'Planning' }));
    expect(screen.getByRole('heading', { name: 'Prepare a task plan' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Discover configured repository' })).toBeInTheDocument();
    expect(api.inspectNativeWorkspace).not.toHaveBeenCalled();
    expect(api.createTask).not.toHaveBeenCalled();
  });
  it('retains the last successful refresh time when a later refresh fails', async () => {
    render(<App />);
    await screen.findByText('Connected');
    expect(screen.queryByText('Not yet available')).not.toBeInTheDocument();
    vi.mocked(api.health).mockRejectedValue(new Error('Offline'));
    fireEvent.click(screen.getByRole('button', { name: /Refresh/ }));
    expect((await screen.findAllByText('Unavailable')).length).toBeGreaterThan(0);
    expect(screen.queryByText('Not yet available')).not.toBeInTheDocument();
  });
});


describe('local diagnostics', () => {
  const snapshot = {
    action: 'system.health' as const,
    checkedAtUtc: '2026-11-07T00:00:00Z',
    controlPlane: 'ok' as const,
    postgres: 'ok' as const,
    redis: 'unavailable' as const,
    worker: 'not-checked' as const,
  };

  it('runs a fixed read-only check only after the user clicks, without creating a task', async () => {
    vi.mocked(api.localDiagnostics).mockResolvedValue(snapshot);
    render(<App />);
    await screen.findByText('Connected');
    expect(api.localDiagnostics).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Command Center' }));
    fireEvent.click(screen.getByRole('button', { name: 'Run local diagnostics' }));
    expect(await screen.findByText('Not checked')).toBeInTheDocument();
    expect(screen.getByText('Unavailable')).toBeInTheDocument();
    expect(api.localDiagnostics).toHaveBeenCalledTimes(1);
    expect(api.createTask).not.toHaveBeenCalled();
  });

  it('reports an unavailable diagnostics endpoint instead of inventing successful checks', async () => {
    vi.mocked(api.localDiagnostics).mockRejectedValue(new Error('unavailable'));
    render(<App />);
    await screen.findByText('Connected');
    fireEvent.click(screen.getByRole('button', { name: 'Command Center' }));
    fireEvent.click(screen.getByRole('button', { name: 'Run local diagnostics' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Local diagnostics unavailable.');
    expect(api.createTask).not.toHaveBeenCalled();
  });
});


describe('native host workspace action', () => {
  it('runs only after a click and never creates or approves a Control Plane task', async () => {
    vi.mocked(api.inspectNativeWorkspace).mockResolvedValue({
      action: 'workspace.inspect', runId: '01234567-89ab-cdef-0123-456789abcdef',
      checkedAtUtc: '2026-11-07T00:00:00Z',
      workspaceAvailable: true, gitCheckoutFound: true, solutionFound: true,
    });
    render(<App />);
    await screen.findByText('Connected');
    fireEvent.click(screen.getByRole('button', { name: 'Command Center' }));
    expect(api.inspectNativeWorkspace).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Inspect local workspace' }));
    expect(await screen.findByText(/Native action completed/)).toBeInTheDocument();
    expect(api.inspectNativeWorkspace).toHaveBeenCalledTimes(1);
    expect(api.createTask).not.toHaveBeenCalled();
  });

  it('does not fake a successful native action when the host is unavailable', async () => {
    vi.mocked(api.inspectNativeWorkspace).mockRejectedValue(new Error('offline'));
    render(<App />);
    await screen.findByText('Connected');
    fireEvent.click(screen.getByRole('button', { name: 'Command Center' }));
    fireEvent.click(screen.getByRole('button', { name: 'Inspect local workspace' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Native host is unavailable or rejected the request.');
    expect(api.createTask).not.toHaveBeenCalled();
  });
});

describe('command center', () => {
  const draft = { ...item, id: '11111111-2222-3333-4444-555555555555', title: 'Fix the login bug',
    description: 'Fix the login bug\nwith details', status: 'Draft', attemptCount: 0, result: null };
  const openCommandCenter = async () => {
    render(<App />);
    await screen.findByText('Connected');
    fireEvent.click(screen.getByRole('button', { name: 'Command Center' }));
    return {
      request: screen.getByRole('textbox', { name: /Request/ }),
      title: screen.getByRole('textbox', { name: /Title/ }),
      submit: screen.getByRole('button', { name: 'Create Draft task' }),
    };
  };

  it('keeps Overview as the landing page and never submits on load or while typing', async () => {
    render(<App />);
    await screen.findByText('Connected');
    expect(screen.getByRole('heading', { level: 1, name: 'Overview' })).toBeInTheDocument();
    expect(screen.queryByRole('textbox', { name: /Request/ })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /New task/ }));
    const request = screen.getByRole('textbox', { name: /Request/ });
    fireEvent.change(request, { target: { value: 'Draft text' } });
    fireEvent.keyDown(request, { key: 'Enter' });
    expect(screen.getByText(/Nothing is approved, dispatched or executed/)).toBeInTheDocument();
    expect(api.createTask).not.toHaveBeenCalled();
  });

  it('validates required, oversized and over-long title input without sending', async () => {
    const { request, title, submit } = await openCommandCenter();
    fireEvent.click(submit);
    expect(screen.getByText('Describe the request before submitting.')).toBeInTheDocument();
    expect(request).toHaveAttribute('aria-invalid', 'true');
    fireEvent.change(request, { target: { value: 'x'.repeat(8001) } });
    expect(screen.getByText('Request must be at most 8000 characters.')).toBeInTheDocument();
    fireEvent.click(submit);
    fireEvent.change(request, { target: { value: 'valid' } });
    fireEvent.change(title, { target: { value: 't'.repeat(201) } });
    expect(screen.getByText('Title must be at most 200 characters.')).toBeInTheDocument();
    fireEvent.click(submit);
    expect(api.createTask).not.toHaveBeenCalled();
  });

  it('derives a title when blank and keeps the exact request as the description', async () => {
    vi.mocked(api.createTask).mockResolvedValue(draft);
    const { request, submit } = await openCommandCenter();
    const text = '  Fix the   login bug  \n\nKeep   exact   spacing\n';
    fireEvent.change(request, { target: { value: text } });
    expect(screen.getByText('Leave blank to use: “Fix the login bug”')).toBeInTheDocument();
    fireEvent.click(submit);
    await screen.findByText('Created as Draft — not executed');
    expect(api.createTask).toHaveBeenCalledWith({ title: 'Fix the login bug', description: text });
  });

  it('uses an explicit title and supports Ctrl+Enter but not plain Enter', async () => {
    vi.mocked(api.createTask).mockResolvedValue(draft);
    const { request, title } = await openCommandCenter();
    fireEvent.change(request, { target: { value: 'Body' } });
    fireEvent.change(title, { target: { value: '  Custom title ' } });
    fireEvent.keyDown(title, { key: 'Enter' });
    fireEvent.keyDown(request, { key: 'Enter' });
    expect(api.createTask).not.toHaveBeenCalled();
    fireEvent.keyDown(request, { key: 'Enter', ctrlKey: true });
    await screen.findByText('Created as Draft — not executed');
    expect(api.createTask).toHaveBeenCalledWith({ title: 'Custom title', description: 'Body' });
  });

  it('submits once while pending even with repeated clicks and shortcuts', async () => {
    let resolve: (task: typeof draft) => void = () => undefined;
    vi.mocked(api.createTask).mockReturnValue(new Promise(r => { resolve = r; }));
    const { request, submit } = await openCommandCenter();
    fireEvent.change(request, { target: { value: 'Once only' } });
    fireEvent.click(submit);
    fireEvent.click(submit);
    fireEvent.keyDown(request, { key: 'Enter', ctrlKey: true });
    expect(screen.getByRole('button', { name: 'Submitting…' })).toBeDisabled();
    expect(api.createTask).toHaveBeenCalledTimes(1);
    await act(async () => resolve(draft));
    await screen.findByText('Created as Draft — not executed');
    expect(api.createTask).toHaveBeenCalledTimes(1);
  });

  it('shows the created Draft, refreshes the real task list and opens the created task', async () => {
    vi.mocked(api.createTask).mockResolvedValue(draft);
    const { request, submit } = await openCommandCenter();
    const tasksCallsBefore = vi.mocked(api.tasks).mock.calls.length;
    vi.mocked(api.tasks).mockResolvedValue([draft]);
    fireEvent.change(request, { target: { value: 'Fix the login bug' } });
    fireEvent.click(submit);
    const created = (await screen.findByText('Created as Draft — not executed')).closest('section');
    if (!created) throw new Error('No created panel');
    expect(within(created).getByText(draft.id)).toBeInTheDocument();
    expect(within(created).getAllByText('Draft').length).toBeGreaterThan(0);
    expect(within(created).getByText(/No approval, dispatch or agent execution was triggered/)).toBeInTheDocument();
    await waitFor(() => expect(vi.mocked(api.tasks).mock.calls.length).toBeGreaterThan(tasksCallsBefore));
    expect(request).toHaveValue('');
    vi.mocked(api.task).mockResolvedValue(draft);
    fireEvent.click(within(created).getByRole('button', { name: 'View task' }));
    expect(await screen.findByRole('heading', { level: 1, name: 'Tasks' })).toBeInTheDocument();
    await waitFor(() => expect(api.task).toHaveBeenCalledWith(draft.id));
    expect(await screen.findByText('Fix the login bug with details', { normalizer: s => s.replace(/\s+/g, ' ').trim() })).toBeInTheDocument();
  });

  it('keeps the input after a definite rejection and allows correcting it', async () => {
    vi.mocked(api.createTask).mockRejectedValue(new ApiError('http', 'Control Plane returned HTTP 400: Title is required. No task was created.', 400));
    const { request, submit } = await openCommandCenter();
    fireEvent.change(request, { target: { value: 'Keep me' } });
    fireEvent.click(submit);
    expect(await screen.findByRole('alert')).toHaveTextContent('No task was created.');
    expect(request).toHaveValue('Keep me');
    expect(screen.getByRole('button', { name: 'Create Draft task' })).toBeEnabled();
  });

  it('blocks resubmission after an uncertain outcome until the task list is checked', async () => {
    vi.mocked(api.createTask).mockRejectedValue(new ApiError('timeout',
      'The Control Plane did not confirm the request. The task may or may not have been created — check the Tasks list before submitting again.', 504, true));
    const { request, submit } = await openCommandCenter();
    fireEvent.change(request, { target: { value: 'Maybe created' } });
    fireEvent.click(submit);
    expect(await screen.findByRole('alert')).toHaveTextContent('may or may not have been created');
    expect(screen.getByRole('button', { name: 'Create Draft task' })).toBeDisabled();
    fireEvent.keyDown(request, { key: 'Enter', ctrlKey: true });
    expect(api.createTask).toHaveBeenCalledTimes(1);
    const tasksCallsBefore = vi.mocked(api.tasks).mock.calls.length;
    fireEvent.click(screen.getByRole('button', { name: 'Check Tasks list' }));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Create Draft task' })).toBeEnabled());
    expect(vi.mocked(api.tasks).mock.calls.length).toBeGreaterThan(tasksCallsBefore);
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('keeps the uncertain lock when the task list cannot be refreshed', async () => {
    vi.mocked(api.createTask).mockRejectedValue(new ApiError('network', 'uncertain', undefined, true));
    const { request, submit } = await openCommandCenter();
    fireEvent.change(request, { target: { value: 'Maybe created' } });
    fireEvent.click(submit);
    await screen.findByRole('button', { name: 'Check Tasks list' });
    vi.mocked(api.tasks).mockRejectedValue(new Error('Control Plane is unreachable.'));
    fireEvent.click(screen.getByRole('button', { name: 'Check Tasks list' }));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Check Tasks list' })).toBeEnabled());
    expect(screen.getByRole('button', { name: 'Create Draft task' })).toBeDisabled();
  });

  it('preserves typed input across navigation and keeps other pages working', async () => {
    const { request } = await openCommandCenter();
    fireEvent.change(request, { target: { value: 'Unsent draft' } });
    fireEvent.click(screen.getByRole('button', { name: 'Plans' }));
    expect(screen.getByText('No plans yet.')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Overview' }));
    expect(screen.getByText('Connected')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Command Center' }));
    expect(screen.getByRole('textbox', { name: /Request/ })).toHaveValue('Unsent draft');
    expect(api.createTask).not.toHaveBeenCalled();
  });
});


describe('unsent Command Center input during a WSL/Control Plane outage', () => {
  it('retains local text across a page remount while Control Plane is unavailable, without posting', async () => {
    const { unmount } = render(<App />);
    await screen.findByText('Connected');
    fireEvent.click(screen.getByRole('button', { name: 'Command Center' }));
    fireEvent.change(screen.getByRole('textbox', { name: /Request/ }), {
      target: { value: 'Build an auth flow without a running API' },
    });
    fireEvent.change(screen.getByRole('textbox', { name: /Title/ }), {
      target: { value: 'New agent request' },
    });
    await waitFor(() => expect(window.localStorage.getItem(UNSENT_DRAFT_KEY)).toContain('New agent request'));
    expect(api.createTask).not.toHaveBeenCalled();
    unmount();

    vi.mocked(api.health).mockRejectedValue(new Error('WSL unavailable'));
    vi.mocked(api.tasks).mockRejectedValue(new Error('WSL unavailable'));
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'Command Center' }));
    expect(screen.getByRole('textbox', { name: /Request/ })).toHaveValue('Build an auth flow without a running API');
    expect(screen.getByRole('textbox', { name: /Title/ })).toHaveValue('New agent request');
    expect(screen.getByText(/Unsent input stored locally/)).toBeInTheDocument();
    expect(api.createTask).not.toHaveBeenCalled();
  });

  it('clears only the unsent buffer after a verified Draft creation, never dispatches', async () => {
    const created = { ...item, id: '11111111-2222-3333-4444-555555555555', title: 'Saved',
      description: 'Something to implement', status: 'Draft', attemptCount: 0 };
    vi.mocked(api.createTask).mockResolvedValue(created);
    render(<App />);
    await screen.findByText('Connected');
    fireEvent.click(screen.getByRole('button', { name: 'Command Center' }));
    fireEvent.change(screen.getByRole('textbox', { name: /Request/ }), {
      target: { value: 'Something to implement' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Create Draft task' }));
    await screen.findByText('Created as Draft — not executed');
    await waitFor(() => expect(window.localStorage.getItem(UNSENT_DRAFT_KEY)).toBeNull());
    expect(api.createTask).toHaveBeenCalledTimes(1);
  });

  it('persists the uncertain submission lock across reload and never silently resubmits', async () => {
    vi.mocked(api.createTask).mockRejectedValue(new ApiError('timeout', 'Outcome unknown', 504, true));
    const { unmount } = render(<App />);
    await screen.findByText('Connected');
    fireEvent.click(screen.getByRole('button', { name: 'Command Center' }));
    fireEvent.change(screen.getByRole('textbox', { name: /Request/ }), {
      target: { value: 'Maybe the first request arrived' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Create Draft task' }));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Create Draft task' })).toBeDisabled());
    await waitFor(() => expect(JSON.parse(window.localStorage.getItem(UNSENT_DRAFT_KEY) ?? '{}').uncertain).toBe(true));
    unmount();

    render(<App />);
    await screen.findByText('Connected');
    fireEvent.click(screen.getByRole('button', { name: 'Command Center' }));
    expect(screen.getByRole('textbox', { name: /Request/ })).toHaveValue('Maybe the first request arrived');
    expect(screen.getByRole('button', { name: 'Create Draft task' })).toBeDisabled();
    expect(screen.getByRole('alert')).toHaveTextContent('A previous Draft submission may have reached the Control Plane.');
    expect(screen.getByRole('button', { name: 'Check Tasks list' })).toBeEnabled();
    expect(api.createTask).toHaveBeenCalledTimes(1);
  });
});
