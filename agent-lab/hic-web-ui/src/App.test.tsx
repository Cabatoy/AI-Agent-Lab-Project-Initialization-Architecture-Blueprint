import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import App from './App';
import { api } from './api';

vi.mock('./api', () => ({ api: { health: vi.fn(), tasks: vi.fn(), task: vi.fn(), plans: vi.fn() } }));
const item = {
  id: '01234567-89ab-cdef-0123-456789abcdef', title: 'Actual task', description: 'Actual description',
  status: 'Executing', attemptCount: 2, result: 'Actual result', errorMessage: null,
  createdAtUtc: '2026-11-06T12:00:00Z', updatedAtUtc: '2026-11-06T12:00:00Z',
};
beforeEach(() => {
  vi.mocked(api.health).mockResolvedValue({ status: 'ok', service: 'control-plane' });
  vi.mocked(api.tasks).mockResolvedValue([]);
  vi.mocked(api.plans).mockResolvedValue([]);
  vi.mocked(api.task).mockResolvedValue(item);
});
afterEach(() => { cleanup(); vi.clearAllMocks(); });

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
    expect(screen.getByText('Token and budget visibility: not available')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /Agents/ }));
    expect(screen.getByText('No persisted agent definitions are available.')).toBeInTheDocument();
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
