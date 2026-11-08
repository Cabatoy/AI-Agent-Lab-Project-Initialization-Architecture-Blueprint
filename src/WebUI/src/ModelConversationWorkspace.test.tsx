import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { api, type ModelConversation } from './api';
import { ModelConversationWorkspace } from './ModelConversationWorkspace';
import { githubApi } from './githubApi';
import { operatorBrowserAccess, openOperatorBrowserSession } from './operatorBrowserAccess';
vi.mock('./operatorBrowserAccess', () => ({ operatorBrowserAccess: vi.fn(), openOperatorBrowserSession: vi.fn() }));

vi.mock('./githubApi', () => ({ githubApi: { status: vi.fn() } }));

vi.mock('./api', () => ({ api: {
  createLocalRunnerSession: vi.fn(), localCodeModel: vi.fn(), modelRepositories: vi.fn(),
  modelConversations: vi.fn(), modelConversation: vi.fn(), createModelConversation: vi.fn(),
  sendModelMessage: vi.fn(), approveModelCodeEdit: vi.fn(), executeLocalHealthRun: vi.fn(),
} }));

const id = '11234567-89ab-cdef-0123-456789abcdef';
const digest = 'a'.repeat(64);
const now = new Date().toISOString();
const emptyConversation: ModelConversation = {
  conversationId: id, taskId: id, title: 'Update value', repositoryId: 'fixture',
  repositoryPath: 'C:\\fixture', baseCommit: 'b'.repeat(40), file: 'src/Value.cs',
  createdAtUtc: now, updatedAtUtc: now, messages: [], runs: [],
};
const proposal = {
  intentDigestSha256: digest,
  plan: {
    revision: 1, taskId: id, actorId: id, conversationId: id, taskDigest: digest,
    repositoryId: 'fixture', repositoryPath: 'C:\\fixture', baseCommit: 'b'.repeat(40),
    file: 'src/Value.cs', content: 'namespace Fixture;\npublic static class Value {}\n',
    provider: 'podman-ai-lab', model: 'mistral-local',
    expiresAtUtc: new Date(Date.now() + 240000).toISOString(),
  },
};
const generated: ModelConversation = {
  ...emptyConversation,
  messages: [
    { messageId: '21234567-89ab-cdef-0123-456789abcdef', role: 'user', content: 'Update the value.',
      provider: null, modelName: null, status: 'completed', proposal: null, createdAtUtc: now },
    { messageId: '31234567-89ab-cdef-0123-456789abcdef', role: 'assistant', content: 'I can make that change.',
      provider: 'podman-ai-lab', modelName: 'mistral-local', status: 'completed', proposal, createdAtUtc: now },
  ],
};

beforeEach(() => {
  vi.mocked(operatorBrowserAccess).mockResolvedValue(null);
  vi.mocked(githubApi.status).mockResolvedValue({ configured: false, repository: '', missing: ['HIC_GITHUB_APP_ID'] });
  vi.stubGlobal('crypto', { randomUUID: () => '41234567-89ab-cdef-0123-456789abcdef' });
  vi.mocked(api.createLocalRunnerSession).mockResolvedValue({ accessToken: 's'.repeat(44), expiresInSeconds: 900 });
  vi.mocked(api.localCodeModel).mockResolvedValue({
    configured: true, provider: 'podman-ai-lab', model: 'mistral-local', timeoutSeconds: 60,
  });
  vi.mocked(api.modelRepositories).mockResolvedValue([{
    id: 'fixture', path: 'C:\\fixture', baseCommit: 'b'.repeat(40),
    files: ['src/Value.cs'], available: true,
  }]);
  vi.mocked(api.modelConversations).mockResolvedValue([]);
  vi.mocked(api.modelConversation).mockResolvedValue(emptyConversation);
  vi.mocked(api.createModelConversation).mockResolvedValue(emptyConversation);
  vi.mocked(api.sendModelMessage).mockResolvedValue(generated);
  vi.mocked(api.approveModelCodeEdit).mockResolvedValue({
    runId: id, taskId: id, conversationId: id, action: 'repository.model-code-edit',
    scope: 'disposable-worktree:one-allowlisted-csharp-file:write', intentDigestSha256: digest, status: 'queued',
  });
  vi.mocked(api.executeLocalHealthRun).mockResolvedValue({ status: 'completed' });
});
afterEach(() => { cleanup(); vi.resetAllMocks(); vi.unstubAllGlobals(); window.history.replaceState(null, '', '/'); });

it('opens a Windows-authenticated browser workspace without a bootstrap secret or an approval', async () => {
  window.history.replaceState(null, '', '/#operator-access');
  vi.mocked(operatorBrowserAccess).mockResolvedValue({
    sessionUrl: 'http://localhost:19556/session', expiresAtUtc: new Date(Date.now() + 60000).toISOString(),
  });
  vi.mocked(openOperatorBrowserSession).mockResolvedValue('w'.repeat(43) + '=');
  render(<ModelConversationWorkspace />);
  await screen.findByText('Start a conversation');
  expect(api.createLocalRunnerSession).not.toHaveBeenCalled();
  expect(api.localCodeModel).toHaveBeenCalledWith('w'.repeat(43) + '=');
  expect(api.approveModelCodeEdit).not.toHaveBeenCalled();
  expect(api.executeLocalHealthRun).not.toHaveBeenCalled();
  expect(screen.queryByLabelText('Local operator bootstrap token')).not.toBeInTheDocument();
});

it('creates a durable conversation, displays a model proposal, requires approval and reports execution results', async () => {
  render(<ModelConversationWorkspace />);
  fireEvent.change(screen.getByLabelText('Local operator bootstrap token'), { target: { value: 'bootstrap' } });
  fireEvent.click(screen.getByText('Open conversation workspace'));
  await screen.findByText('Start a conversation');

  fireEvent.change(screen.getByLabelText('Conversation title'), { target: { value: 'Update value' } });
  fireEvent.change(screen.getByLabelText('Authorized repository'), { target: { value: 'fixture' } });
  await waitFor(() => expect(screen.getByLabelText('One permitted C# source file')).toHaveValue('src/Value.cs'));
  fireEvent.click(screen.getByText('Create conversation'));
  await screen.findByText('Start with architecture questions, requirements or a bounded change request.');
  fireEvent.change(screen.getByLabelText('Message the local model'), { target: { value: 'Update the value.' } });
  fireEvent.click(screen.getByText('Send message'));
  await screen.findByText('Proposed code plan — revision 1');
  expect(api.sendModelMessage).toHaveBeenCalledWith(
    id, '41234567-89ab-cdef-0123-456789abcdef', 'Update the value.', 's'.repeat(44));
  expect(api.approveModelCodeEdit).not.toHaveBeenCalled();

  const queued: ModelConversation = { ...generated, runs: [{
    runId: id, taskId: id, status: 'queued', action: 'repository.model-code-edit',
    scope: 'disposable-worktree:one-allowlisted-csharp-file:write', intentDigestSha256: digest,
    plan: proposal.plan, result: null, events: [{ eventCode: 'approved', recordedAtUtc: now }],
  }] };
  vi.mocked(api.modelConversation).mockResolvedValueOnce(queued);
  fireEvent.click(screen.getByText('Approve exact plan'));
  await screen.findByText('Code edit · queued');
  expect(api.approveModelCodeEdit).toHaveBeenCalledWith(
    id, generated.messages[1].messageId, digest, 's'.repeat(44));
  expect(api.executeLocalHealthRun).not.toHaveBeenCalled();

  const completed: ModelConversation = {
    ...queued,
    runs: [{ ...queued.runs[0], status: 'completed', result: {
      action: 'repository.model-code-edit', runId: id, conversationId: id, repositoryId: 'fixture',
      baseCommit: 'b'.repeat(40), file: 'src/Value.cs', worktreePath: 'C:\\runs\\fixture',
      branch: 'hic-model-test', diff: '+public static class Value', testCode: 'passed:isolated-csharp-compile-and-scope',
      sourceUnchanged: true, checkedAtUtc: now,
    }, events: [{ eventCode: 'approved', recordedAtUtc: now }, { eventCode: 'completed', recordedAtUtc: now }] }],
  };
  vi.mocked(api.modelConversation).mockResolvedValueOnce(completed);
  fireEvent.click(screen.getByText('Execute approved isolated edit'));
  await screen.findByText('Bounded validation: passed:isolated-csharp-compile-and-scope');
  expect(api.executeLocalHealthRun).toHaveBeenCalledWith(id, 's'.repeat(44), 120000);
  expect(screen.getByText('+public static class Value')).toBeInTheDocument();
  expect(screen.getByText(/completed ·/)).toBeInTheDocument();
});
