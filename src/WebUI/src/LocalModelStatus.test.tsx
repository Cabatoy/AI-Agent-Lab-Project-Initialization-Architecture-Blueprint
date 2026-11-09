import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { api, COOKIE_SESSION, OPERATOR_SESSION_ENDED } from './api';
import { LocalModelStatus } from './LocalModelStatus';

vi.mock('./api', async importOriginal => ({
  ...await importOriginal<typeof import('./api')>(),
  api: { restoreLocalRunnerSession: vi.fn(), localCodeModel: vi.fn() },
}));

const available = {
  configured: true, provider: 'podman-ai-lab', model: 'mistral-local',
  timeoutSeconds: 60, readiness: 'available' as const,
};

beforeEach(() => {
  vi.mocked(api.restoreLocalRunnerSession).mockResolvedValue({
    actorId: '11234567-89ab-cdef-0123-456789abcdef',
    expiresAtUtc: new Date(Date.now() + 60000).toISOString(), expiresInSeconds: 60,
  });
  vi.mocked(api.localCodeModel).mockResolvedValue(available);
});
afterEach(() => { cleanup(); vi.resetAllMocks(); });

it('restores the authenticated session and refreshes actual provider status', async () => {
  render(<LocalModelStatus openConversations={vi.fn()} />);
  expect(await screen.findByText(/Readiness: available/)).toBeInTheDocument();
  expect(api.localCodeModel).toHaveBeenCalledWith(COOKIE_SESSION);
  vi.mocked(api.localCodeModel).mockResolvedValue({ ...available, readiness: 'unavailable' });
  fireEvent.click(screen.getByRole('button', { name: 'Refresh model status' }));
  expect(await screen.findByRole('alert')).toHaveTextContent('Readiness: unavailable');
});

it('shows errors and allows retry without suggesting a successful connection', async () => {
  vi.mocked(api.localCodeModel).mockRejectedValueOnce(new Error('Diagnostics unavailable'));
  render(<LocalModelStatus openConversations={vi.fn()} />);
  expect(await screen.findByRole('alert')).toHaveTextContent('Diagnostics unavailable');
  fireEvent.click(await screen.findByRole('button', { name: 'Refresh model status' }));
  expect(await screen.findByText(/Readiness: available/)).toBeInTheDocument();
});

it('clears status when the existing operator session ends', async () => {
  render(<LocalModelStatus openConversations={vi.fn()} />);
  await screen.findByText(/Readiness: available/);
  fireEvent(window, new Event(OPERATOR_SESSION_ENDED));
  expect(screen.queryByText(/Readiness: available/)).not.toBeInTheDocument();
  expect(screen.getByText(/Authenticate in Conversations/)).toBeInTheDocument();
});
