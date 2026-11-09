import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { api, type GeneralConversation } from './api';
import { GeneralConversationWorkspace } from './GeneralConversationWorkspace';

vi.mock('./api', async importOriginal => ({
  ...await importOriginal<typeof import('./api')>(),
  api: { generalConversations: vi.fn(), generalConversation: vi.fn(),
    createGeneralConversation: vi.fn(), sendGeneralMessage: vi.fn() },
}));
const id = '11234567-89ab-cdef-0123-456789abcdef';
const now = new Date().toISOString();
const empty: GeneralConversation = { conversationId: id, title: 'Ideas', createdAtUtc: now, updatedAtUtc: now, messages: [] };
const reply: GeneralConversation = { ...empty, messages: [{
  messageId: id, role: 'assistant', content: 'A response', provider: 'local', modelName: 'mistral',
  status: 'completed', proposal: null, createdAtUtc: now,
}] };

beforeEach(() => {
  vi.mocked(api.generalConversations).mockResolvedValue([]);
  vi.mocked(api.createGeneralConversation).mockResolvedValue(empty);
  vi.mocked(api.sendGeneralMessage).mockResolvedValue(reply);
  vi.mocked(api.generalConversation).mockResolvedValue(reply);
  vi.stubGlobal('crypto', { randomUUID: () => id });
});
afterEach(() => { cleanup(); vi.resetAllMocks(); vi.unstubAllGlobals(); });

it('creates, messages and reopens a general conversation without repository selection', async () => {
  render(<GeneralConversationWorkspace token="cookie-session" />);
  await screen.findByText('No general conversations yet.');
  fireEvent.change(screen.getByLabelText('General conversation title'), { target: { value: 'Ideas' } });
  fireEvent.click(screen.getByRole('button', { name: 'Create general conversation' }));
  await screen.findByText('No messages yet.');
  fireEvent.change(screen.getByLabelText('Message general conversation'), { target: { value: 'Hello' } });
  fireEvent.click(screen.getByRole('button', { name: 'Send general message' }));
  expect(await screen.findByText('A response')).toBeInTheDocument();
  expect(api.sendGeneralMessage).toHaveBeenCalledWith(id, id, 'Hello', 'cookie-session');
  fireEvent.click(screen.getByRole('button', { name: /Ideas/ }));
  await waitFor(() => expect(api.generalConversation).toHaveBeenCalledWith(id, 'cookie-session'));
});

it('reports missing schema without claiming a conversation was created', async () => {
  vi.mocked(api.generalConversations).mockRejectedValue(new Error('Schema unavailable'));
  render(<GeneralConversationWorkspace token="cookie-session" />);
  expect(await screen.findByRole('alert')).toHaveTextContent('Schema unavailable');
  expect(screen.queryByRole('button', { name: 'Create general conversation' })).not.toBeInTheDocument();
});
