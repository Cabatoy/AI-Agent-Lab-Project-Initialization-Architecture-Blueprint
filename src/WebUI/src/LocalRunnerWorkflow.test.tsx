import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { api } from './api';
import { LocalRunnerWorkflow } from './LocalRunnerWorkflow';

vi.mock('./api', () => ({
  api: {
    createLocalRunnerSession: vi.fn(),
    localRunnerCapabilities: vi.fn(),
    tasks: vi.fn(),
    localHealthIntent: vi.fn(),
    approveLocalHealthRun: vi.fn(),
    executeLocalHealthRun: vi.fn(),
    localHealthRun: vi.fn(),
  },
}));

const task = {
  id: '01234567-89ab-cdef-0123-456789abcdef',
  title: 'Check local runtime',
  description: 'Report local dependency health without changing state.',
  status: 'Draft',
  attemptCount: 0,
  result: null,
  errorMessage: null,
  createdAtUtc: '2026-11-08T10:00:00Z',
  updatedAtUtc: '2026-11-08T10:00:00Z',
};
const intent = {
  taskId: task.id,
  taskTitle: task.title,
  action: 'system.health' as const,
  scope: 'control-plane:health:read' as const,
  taskUpdatedAtUtc: task.updatedAtUtc,
  intentDigestSha256: 'a'.repeat(64),
};
const queued = {
  runId: '11234567-89ab-cdef-0123-456789abcdef',
  taskId: task.id,
  action: 'system.health' as const,
  scope: 'control-plane:health:read' as const,
  intentDigestSha256: intent.intentDigestSha256,
  status: 'queued' as const,
  createdAtUtc: task.createdAtUtc,
  approvedAtUtc: task.createdAtUtc,
  expiresAtUtc: '2026-11-08T10:05:00Z',
  startedAtUtc: null,
  completedAtUtc: null,
  result: null,
  events: [
    { eventCode: 'approved', recordedAtUtc: task.createdAtUtc },
    { eventCode: 'queued', recordedAtUtc: task.createdAtUtc },
  ],
};
const completed = {
  ...queued,
  status: 'completed' as const,
  startedAtUtc: '2026-11-08T10:00:01Z',
  completedAtUtc: '2026-11-08T10:00:02Z',
  result: {
    action: 'system.health' as const,
    checkedAtUtc: '2026-11-08T10:00:02Z',
    controlPlane: 'ok' as const,
    postgres: 'ok' as const,
    redis: 'unavailable' as const,
    worker: 'not-checked' as const,
  },
  events: [
    ...queued.events,
    { eventCode: 'claimed', recordedAtUtc: '2026-11-08T10:00:01Z' },
    { eventCode: 'completed', recordedAtUtc: '2026-11-08T10:00:02Z' },
  ],
};
const invalidated = { ...queued, status: 'invalidated' as const, completedAtUtc: '2026-11-08T10:00:01Z' };

beforeEach(() => {
  vi.mocked(api.createLocalRunnerSession).mockResolvedValue({ accessToken: 'S'.repeat(44), expiresInSeconds: 900 });
  vi.mocked(api.localRunnerCapabilities).mockResolvedValue({
    actorId: '21234567-89ab-cdef-0123-456789abcdef',
    action: 'system.health',
    scope: 'control-plane:health:read',
    worker: 'not-used',
    provider: 'not-used',
  });
  vi.mocked(api.tasks).mockResolvedValue([task, { ...task, id: '31234567-89ab-cdef-0123-456789abcdef', status: 'Completed' }]);
  vi.mocked(api.localHealthIntent).mockResolvedValue(intent);
  vi.mocked(api.approveLocalHealthRun).mockResolvedValue({
    runId: queued.runId,
    taskId: task.id,
    action: queued.action,
    scope: queued.scope,
    intentDigestSha256: intent.intentDigestSha256,
    status: 'queued',
  });
  vi.mocked(api.executeLocalHealthRun).mockResolvedValue({ runId: queued.runId, status: 'completed' });
  vi.mocked(api.localHealthRun).mockResolvedValueOnce(queued).mockResolvedValueOnce(completed).mockResolvedValueOnce(completed);
});

afterEach(() => { cleanup(); vi.clearAllMocks(); });

describe('Local Runner workflow', () => {
  it('does not execute before authentication, task selection and explicit intent approval', async () => {
    render(<LocalRunnerWorkflow />);
    expect(api.executeLocalHealthRun).not.toHaveBeenCalled();

    fireEvent.change(screen.getByLabelText('Local operator bootstrap token'), {
      target: { value: 'R'.repeat(44) },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Authenticate local operator' }));
    await screen.findByText(/Authenticated local actor/);
    expect(api.createLocalRunnerSession).toHaveBeenCalledWith('R'.repeat(44));

    fireEvent.change(screen.getByLabelText('Draft task'), { target: { value: task.id } });
    expect(await screen.findByText('control-plane:health:read')).toBeInTheDocument();
    expect(screen.getByText('system.health')).toBeInTheDocument();
    expect(screen.getByText(intent.intentDigestSha256)).toBeInTheDocument();
    expect(api.executeLocalHealthRun).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('button', { name: 'Approve and run system.health' }));
    expect(await screen.findByText('Run status: completed')).toBeInTheDocument();
    expect(api.approveLocalHealthRun).toHaveBeenCalledWith(task.id, intent.intentDigestSha256, 'S'.repeat(44));
    expect(api.executeLocalHealthRun).toHaveBeenCalledWith(queued.runId, 'S'.repeat(44));
    expect(screen.getByText('unavailable')).toBeInTheDocument();
    expect(screen.getByText('claimed')).toBeInTheDocument();
    expect(screen.getByText('completed')).toBeInTheDocument();
    expect(screen.getAllByText(/Agent Worker/).length).toBeGreaterThan(0);
  });

  it('keeps task creation and execution unavailable until a local session is authenticated', () => {
    render(<LocalRunnerWorkflow />);
    expect(screen.queryByRole('button', { name: 'Approve and run system.health' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Authenticate local operator' })).toBeDisabled();
    expect(api.tasks).not.toHaveBeenCalled();
    expect(api.approveLocalHealthRun).not.toHaveBeenCalled();
    expect(api.executeLocalHealthRun).not.toHaveBeenCalled();
  });

  it('shows a real conflict instead of retrying an already claimed run', async () => {
    vi.mocked(api.localHealthRun).mockReset().mockResolvedValue(completed);
    render(<LocalRunnerWorkflow />);
    fireEvent.change(screen.getByLabelText('Local operator bootstrap token'), {
      target: { value: 'R'.repeat(44) },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Authenticate local operator' }));
    await screen.findByText(/Authenticated local actor/);
    fireEvent.change(screen.getByLabelText('Draft task'), { target: { value: task.id } });
    await screen.findByText(intent.intentDigestSha256);
    fireEvent.click(screen.getByRole('button', { name: 'Approve and run system.health' }));
    await waitFor(() => expect(api.localHealthRun).toHaveBeenCalledWith(queued.runId, 'S'.repeat(44)));
    expect(api.executeLocalHealthRun).not.toHaveBeenCalled();
    expect(screen.getByText(/already has a completed run/)).toBeInTheDocument();
  });

  it('reports a changed task as invalidated rather than expired', async () => {
    vi.mocked(api.localHealthRun).mockReset()
      .mockResolvedValueOnce(queued)
      .mockResolvedValueOnce(invalidated)
      .mockResolvedValueOnce(invalidated);
    vi.mocked(api.executeLocalHealthRun).mockRejectedValueOnce(new Error('The task or approved run is no longer eligible.'));
    render(<LocalRunnerWorkflow />);
    fireEvent.change(screen.getByLabelText('Local operator bootstrap token'), {
      target: { value: 'R'.repeat(44) },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Authenticate local operator' }));
    await screen.findByText(/Authenticated local actor/);
    fireEvent.change(screen.getByLabelText('Draft task'), { target: { value: task.id } });
    await screen.findByText(intent.intentDigestSha256);

    fireEvent.click(screen.getByRole('button', { name: 'Approve and run system.health' }));
    expect(await screen.findByText('Run status: invalidated')).toBeInTheDocument();
    expect(screen.queryByText(/approval expired/)).not.toBeInTheDocument();
  });
});
