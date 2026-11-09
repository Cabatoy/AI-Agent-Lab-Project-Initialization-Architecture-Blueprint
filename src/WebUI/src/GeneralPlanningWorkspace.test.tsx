import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { api, ApiError } from './api';
import { GeneralPlanningWorkspace } from './GeneralPlanningWorkspace';

vi.mock('./api', async importOriginal => ({
  ...await importOriginal<typeof import('./api')>(),
  api: { restoreLocalRunnerSession: vi.fn(), generalConversations: vi.fn(),
    generalConversation: vi.fn(), generalPlan: vi.fn() },
}));
const id = '11234567-89ab-cdef-0123-456789abcdef';
const now = new Date().toISOString();
beforeEach(() => {
  vi.mocked(api.restoreLocalRunnerSession).mockResolvedValue({
    actorId: id, expiresAtUtc: new Date(Date.now() + 60000).toISOString(), expiresInSeconds: 60,
  });
  vi.mocked(api.generalConversations).mockResolvedValue([{
    conversationId: id, title: 'Saved ideas', createdAtUtc: now, updatedAtUtc: now,
  }]);
  vi.mocked(api.generalConversation).mockResolvedValue({
    conversationId: id, title: 'Saved ideas', createdAtUtc: now, updatedAtUtc: now,
    messages: [{ messageId: id, role: 'user', content: 'Plan', provider: null,
      modelName: null, status: 'completed', proposal: null, createdAtUtc: now }],
  });
  vi.mocked(api.generalPlan).mockRejectedValue(new ApiError('http', 'Not found', 404));
});
afterEach(() => { cleanup(); vi.resetAllMocks(); });

it('opens an existing conversation plan from Planning using the operator session', async () => {
  render(<GeneralPlanningWorkspace openConversations={vi.fn()} onTasksCreated={vi.fn()} onViewTask={vi.fn()} />);
  const selector = await screen.findByLabelText('Saved general conversation');
  fireEvent.change(selector, { target: { value: id } });
  expect(await screen.findByText('No model plan yet.')).toBeInTheDocument();
  expect(api.generalConversation).toHaveBeenCalledWith(id, 'cookie-session');
  expect(screen.getByRole('button', { name: 'Generate model plan' })).toBeEnabled();
});
