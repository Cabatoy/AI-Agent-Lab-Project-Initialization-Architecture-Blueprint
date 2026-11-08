import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { api } from './api';
import { PlanningWorkflow } from './PlanningWorkflow';

vi.mock('./api', () => ({ api: { inspectNativeWorkspace: vi.fn() } }));

const inspectedRepository = {
  action: 'workspace.inspect' as const,
  runId: '01234567-89ab-cdef-0123-456789abcdef',
  checkedAtUtc: '2026-11-08T10:00:00Z',
  workspaceAvailable: true,
  gitCheckoutFound: true,
  solutionFound: true,
};

beforeEach(() => {
  vi.stubGlobal('crypto', {
    subtle: {
      digest: async (_algorithm: AlgorithmIdentifier, data: BufferSource) => {
        const source = data instanceof ArrayBuffer
          ? new Uint8Array(data)
          : new Uint8Array(data.buffer, data.byteOffset, data.byteLength);
        const digest = new Uint8Array(32);
        source.forEach((value, index) => {
          const slot = index % digest.length;
          digest[slot] = (digest[slot] * 33 + value + index) & 0xff;
        });
        return digest.buffer;
      },
    },
  });
  vi.mocked(api.inspectNativeWorkspace).mockResolvedValue(inspectedRepository);
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.clearAllMocks();
});

describe('human-controlled planning preview', () => {
  it('inspects the configured repository only on request and creates a local hashed plan', async () => {
    render(<PlanningWorkflow />);
    expect(api.inspectNativeWorkspace).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('button', { name: 'Discover configured repository' }));
    expect(await screen.findByRole('option', { name: 'Configured local workspace (read-only)' })).toBeInTheDocument();
    expect(api.inspectNativeWorkspace).toHaveBeenCalledTimes(1);

    fireEvent.change(screen.getByLabelText(/Task title/), { target: { value: 'Fix search validation' } });
    fireEvent.change(screen.getByLabelText(/Task description/), { target: { value: 'Validate search input before submission.' } });
    fireEvent.click(screen.getByRole('button', { name: 'Generate plan preview' }));

    expect(await screen.findByRole('heading', { name: 'Fix search validation' })).toBeInTheDocument();
    expect(screen.getByText('VERSION 1')).toBeInTheDocument();
    expect(screen.getAllByText(/sha256:[0-9a-f]{64}/).length).toBeGreaterThan(0);
    expect(screen.getByText(/Agent: Implementation agent/)).toBeInTheDocument();
    expect(screen.getByText(/Depends on: scope/)).toBeInTheDocument();
    expect(screen.getByText('Approval unavailable', { exact: true })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Approve plan' })).toBeDisabled();
    expect(screen.getByText(/preview.created/)).toBeInTheDocument();
    expect(screen.getAllByText(/No endpoint probe or external request is made/)).toHaveLength(3);
  });

  it('marks a preview stale when inputs change and gives the revised preview a new version and hash', async () => {
    render(<PlanningWorkflow />);
    fireEvent.click(screen.getByRole('button', { name: 'Discover configured repository' }));
    await screen.findByRole('option', { name: 'Configured local workspace (read-only)' });
    fireEvent.change(screen.getByLabelText(/Task title/), { target: { value: 'Plan one' } });
    fireEvent.change(screen.getByLabelText(/Task description/), { target: { value: 'First description' } });
    fireEvent.click(screen.getByRole('button', { name: 'Generate plan preview' }));
    await screen.findByText('VERSION 1');
    const firstHash = screen.getAllByText(/sha256:[0-9a-f]{64}/)[0].textContent;

    fireEvent.change(screen.getByLabelText(/Task description/), { target: { value: 'Changed description' } });
    expect(screen.getByText('VERSION 1 · OUTDATED')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Generate plan preview' }));

    await waitFor(() => expect(screen.getByText('VERSION 2')).toBeInTheDocument());
    const hashes = screen.getAllByText(/sha256:[0-9a-f]{64}/).map(element => element.textContent);
    expect(hashes.some(hash => hash !== firstHash)).toBe(true);
    expect(screen.getByText(/preview.revised/)).toBeInTheDocument();
  });

  it('does not select a repository or claim availability when Native Host inspection fails', async () => {
    vi.mocked(api.inspectNativeWorkspace).mockRejectedValue(new Error('offline'));
    render(<PlanningWorkflow />);

    fireEvent.click(screen.getByRole('button', { name: 'Discover configured repository' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('did not confirm an authorized repository');
    expect(screen.queryByLabelText('Repository')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Generate plan preview' })).toBeDisabled();
  });
});
