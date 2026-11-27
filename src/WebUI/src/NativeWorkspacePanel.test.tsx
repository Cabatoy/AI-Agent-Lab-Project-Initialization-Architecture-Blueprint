import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { api } from './api';
import { NativeWorkspacePanel } from './NativeWorkspacePanel';

vi.mock('./api', () => ({ api: { inspectNativeWorkspace: vi.fn() } }));

afterEach(() => {
  cleanup();
  vi.resetAllMocks();
});

describe('native workspace announcements', () => {
  it('updates the same live region as an inspection starts and completes', async () => {
    let finish!: (value: Awaited<ReturnType<typeof api.inspectNativeWorkspace>>) => void;
    vi.mocked(api.inspectNativeWorkspace).mockImplementation(() => new Promise(resolve => { finish = resolve; }));
    render(<NativeWorkspacePanel />);

    const status = screen.getByRole('status');
    expect(status).toBeEmptyDOMElement();
    expect(api.inspectNativeWorkspace).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Inspect local workspace' }));
    expect(screen.getByRole('status')).toBe(status);
    expect(status).toHaveTextContent('Contacting local native host');

    await act(async () => finish({
      action: 'workspace.inspect', runId: '01234567-89ab-cdef-0123-456789abcdef',
      checkedAtUtc: '2026-11-07T00:00:00Z',
      workspaceAvailable: true, gitCheckoutFound: true, solutionFound: true,
    }));

    expect(screen.getByRole('status')).toBe(status);
    expect(status).toHaveTextContent('Native action completed');
    expect(screen.getByText('Configured workspace')).toBeInTheDocument();
  });

  it('reports a failure without announcing a successful result', async () => {
    vi.mocked(api.inspectNativeWorkspace).mockRejectedValue(new Error('offline'));
    render(<NativeWorkspacePanel />);
    const status = screen.getByRole('status');
    fireEvent.click(screen.getByRole('button', { name: 'Inspect local workspace' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('No action result was confirmed');
    expect(screen.getByRole('status')).toBe(status);
    expect(status).toBeEmptyDOMElement();
    expect(screen.queryByText(/Native action completed/)).not.toBeInTheDocument();
  });
});
