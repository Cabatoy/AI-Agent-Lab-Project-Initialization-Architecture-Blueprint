import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { StagingObservationsPanel } from './StagingObservationsPanel';

const orderId = 'f86ff6cd-61cc-4252-bfe6-4831e2c9ddd7';
const snapshot = {
  version: 'hic.staging-ui-status/1',
  trust: 'untrusted-local-observation',
  executionAllowed: false,
  observations: [{ orderId, status: 'observed-untrusted', observedAt: '2026-11-14T23:11:28.887Z', executionAllowed: false }],
};
const session = { accessToken: 'S'.repeat(44), expiresInSeconds: 900 };
const response = (body: unknown, status = 200) => ({
  ok: status >= 200 && status < 300,
  status,
  json: async () => body,
}) as Response;
const mockedFetch = vi.fn();

beforeEach(() => {
  vi.stubGlobal('fetch', mockedFetch);
  mockedFetch.mockImplementation(async (url: string) =>
    url.endsWith('/session') ? response(session) : response(snapshot));
});
afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.clearAllMocks(); });

async function authenticate() {
  render(<StagingObservationsPanel />);
  fireEvent.change(screen.getByLabelText('Local operator bootstrap token'), { target: { value: 'B'.repeat(44) } });
  fireEvent.click(screen.getByRole('button', { name: 'Authenticate to view' }));
  await waitFor(() => expect(mockedFetch).toHaveBeenCalledTimes(2));
}

describe('staging observations safety boundary', () => {
  it('requires authentication, reads a trusted-shape untrusted record, and never offers execution', async () => {
    await authenticate();
    expect(await screen.findByText(orderId)).toBeInTheDocument();
    expect(screen.getByText('observed-untrusted')).toBeInTheDocument();
    expect(screen.getByText('Disabled')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /execute|approve|run agent/i })).not.toBeInTheDocument();
    const calls = mockedFetch.mock.calls;
    expect(calls[1][1].headers.authorization).toBe('Bearer ' + session.accessToken);
    expect(calls[1][1].headers['x-hic-request']).toBe('local-runner');
    expect(calls[1][1].method).toBe('GET');
    expect(calls[1][1].cache).toBe('no-store');
    expect(document.body.textContent).not.toContain(session.accessToken);
    expect(document.body.textContent).not.toContain('B'.repeat(44));
  });

  it('rejects executionAllowed true even if the rest of the response looks valid', async () => {
    mockedFetch.mockImplementation(async (url: string) =>
      url.endsWith('/session') ? response(session) : response({ ...snapshot, executionAllowed: true }));
    await authenticate();
    expect(await screen.findByRole('alert')).toHaveTextContent(/unavailable/i);
    expect(screen.queryByText(orderId)).not.toBeInTheDocument();
  });

  it('rejects an executable observation and malformed record', async () => {
    mockedFetch.mockImplementation(async (url: string) =>
      url.endsWith('/session') ? response(session) :
        response({ ...snapshot, observations: [{ ...snapshot.observations[0], executionAllowed: true }] }));
    await authenticate();
    expect(await screen.findByRole('alert')).toHaveTextContent(/unavailable/i);
    expect(screen.queryByText(orderId)).not.toBeInTheDocument();
  });

  it('shows empty state without inventing records', async () => {
    mockedFetch.mockImplementation(async (url: string) =>
      url.endsWith('/session') ? response(session) : response({ ...snapshot, observations: [] }));
    await authenticate();
    expect(await screen.findByText('No observations found.')).toBeInTheDocument();
  });

  it('does not show stale records when the endpoint fails', async () => {
    mockedFetch.mockImplementation(async (url: string) =>
      url.endsWith('/session') ? response(session) : response(snapshot, 503));
    await authenticate();
    expect(await screen.findByRole('alert')).toHaveTextContent(/unavailable/i);
    expect(screen.queryByText(orderId)).not.toBeInTheDocument();
  });

  it('rejects invalid operator bootstrap and does not query staging', async () => {
    mockedFetch.mockResolvedValue(response({ error: 'unauthorized' }, 401));
    render(<StagingObservationsPanel />);
    fireEvent.change(screen.getByLabelText('Local operator bootstrap token'), { target: { value: 'B'.repeat(44) } });
    fireEvent.click(screen.getByRole('button', { name: 'Authenticate to view' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(/authentication failed/i);
    expect(mockedFetch).toHaveBeenCalledTimes(1);
  });
});
