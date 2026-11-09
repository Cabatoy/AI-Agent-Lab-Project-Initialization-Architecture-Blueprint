import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { api, OPERATOR_SESSION_ENDED, type ModelConversation } from './api';
import { ModelConversationWorkspace } from './ModelConversationWorkspace';
import { githubApi } from './githubApi';

vi.mock('./githubApi', () => ({ githubApi: { status: vi.fn() } }));

vi.mock('./api', async importOriginal => ({ ...await importOriginal<typeof import('./api')>(), api: {
  restoreLocalRunnerSession: vi.fn(), revokeLocalRunnerSession: vi.fn(),
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
  vi.mocked(api.restoreLocalRunnerSession).mockRejectedValue({ status: 401 });
  vi.mocked(api.revokeLocalRunnerSession).mockResolvedValue({ revoked: true });
  vi.mocked(githubApi.status).mockResolvedValue({ configured: false, repository: '', missing: ['HIC_GITHUB_APP_ID'] });
  vi.stubGlobal('crypto', { randomUUID: () => '41234567-89ab-cdef-0123-456789abcdef' });
  vi.mocked(api.createLocalRunnerSession).mockResolvedValue({ accessToken: 's'.repeat(44), expiresInSeconds: 900 });
  vi.mocked(api.localCodeModel).mockResolvedValue({
    configured: true, provider: 'podman-ai-lab', model: 'mistral-local', timeoutSeconds: 60, readiness: 'available',
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
afterEach(() => { cleanup(); vi.useRealTimers(); vi.resetAllMocks(); vi.unstubAllGlobals(); window.history.replaceState(null, '', '/'); });

it('distinguishes a configured but unreachable local model from a ready provider', async () => {
  vi.mocked(api.localCodeModel).mockResolvedValue({
    configured: true, provider: 'podman-ai-lab', model: 'mistral-local',
    timeoutSeconds: 60, readiness: 'unavailable',
  });
  render(<ModelConversationWorkspace />);
  await waitFor(() => expect(screen.getByLabelText('Local operator bootstrap token')).toBeEnabled());
  fireEvent.change(screen.getByLabelText('Local operator bootstrap token'), { target: { value: 'bootstrap' } });
  fireEvent.click(screen.getByText('Open conversation workspace'));
  expect(await screen.findByText(/Local model readiness: unavailable/)).toBeInTheDocument();
  expect(screen.queryByText(/Local provider is reachable/)).not.toBeInTheDocument();
  vi.mocked(api.localCodeModel).mockResolvedValue({
    configured: true, provider: 'podman-ai-lab', model: 'mistral-local',
    timeoutSeconds: 60, readiness: 'available',
  });
  fireEvent.click(screen.getByRole('button', { name: 'Refresh model status' }));
  expect(await screen.findByText(/Local provider is reachable/)).toBeInTheDocument();
  expect(api.localCodeModel).toHaveBeenCalledTimes(2);
});

it('creates a durable conversation, displays a proposal and preserves historical results without executing', async () => {
  render(<ModelConversationWorkspace />);
  await waitFor(() => expect(screen.getByLabelText('Local operator bootstrap token')).toBeEnabled());
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
  expect(screen.queryByText('Execute approved isolated edit')).not.toBeInTheDocument();
  fireEvent.click(screen.getByText('Refresh history and run status'));
  await screen.findByText('Bounded validation: passed:isolated-csharp-compile-and-scope');
  expect(api.executeLocalHealthRun).not.toHaveBeenCalled();
  expect(screen.getByText('+public static class Value')).toBeInTheDocument();
  expect(screen.getByText(/completed ·/)).toBeInTheDocument();
});

it('restores history after navigation/remount without bootstrap or any approval/write', async () => {
  vi.mocked(api.restoreLocalRunnerSession).mockResolvedValue({
    actorId: id, expiresAtUtc: new Date(Date.now() + 3600000).toISOString(), expiresInSeconds: 3600,
  });
  vi.mocked(api.modelConversations).mockResolvedValue([{
    conversationId: id, taskId: id, title: emptyConversation.title, repositoryId: 'fixture',
    baseCommit: 'b'.repeat(40), file: emptyConversation.file, updatedAtUtc: now,
  }]);
  const first = render(<ModelConversationWorkspace />);
  await screen.findByText('Start a conversation');
  expect(api.localCodeModel).toHaveBeenCalledWith('cookie-session');
  first.unmount();
  render(<ModelConversationWorkspace />);
  await screen.findByText('Start a conversation');
  expect(api.restoreLocalRunnerSession).toHaveBeenCalledTimes(2);
  expect(api.modelConversation).toHaveBeenCalledWith(id, 'cookie-session');
  expect(api.createLocalRunnerSession).not.toHaveBeenCalled();
  expect(api.approveModelCodeEdit).not.toHaveBeenCalled();
  expect(api.executeLocalHealthRun).not.toHaveBeenCalled();
  expect(api.createModelConversation).not.toHaveBeenCalled();
  expect(api.sendModelMessage).not.toHaveBeenCalled();
});

it('revokes on logout, clears private workspace/error and permits supported explicit reauthentication', async () => {
  vi.mocked(api.restoreLocalRunnerSession).mockResolvedValue({
    actorId: id, expiresAtUtc: new Date(Date.now() + 3600000).toISOString(), expiresInSeconds: 3600,
  });
  render(<ModelConversationWorkspace />);
  await screen.findByText('Start a conversation');
  vi.mocked(api.createModelConversation).mockRejectedValueOnce(new Error('Old write error'));
  fireEvent.change(screen.getByLabelText('Conversation title'), { target: { value: 'Update value' } });
  fireEvent.change(screen.getByLabelText('Authorized repository'), { target: { value: 'fixture' } });
  await waitFor(() => expect(screen.getByLabelText('One permitted C# source file')).toHaveValue('src/Value.cs'));
  fireEvent.click(screen.getByText('Create conversation'));
  await screen.findByText(/Old write error/);
  fireEvent.click(screen.getByText('End session'));
  await screen.findByText(/Signed out/);
  expect(api.revokeLocalRunnerSession).toHaveBeenCalledWith('cookie-session');
  expect(screen.queryByText(/Old write error/)).not.toBeInTheDocument();
  expect(screen.queryByText('Start a conversation')).not.toBeInTheDocument();
  expect(screen.getByLabelText('Local operator bootstrap token')).toBeEnabled();
  expect(screen.getByText(/hic-open-operator.ps1/)).toBeInTheDocument();
  fireEvent.change(screen.getByLabelText('Local operator bootstrap token'), { target: { value: 'bootstrap' } });
  fireEvent.click(screen.getByText('Open conversation workspace'));
  await screen.findByText('Start a conversation');
  expect(screen.queryByText(/Signed out/)).not.toBeInTheDocument();
  expect(api.createLocalRunnerSession).toHaveBeenCalledTimes(1);
});

it('clears stale workspace/errors on a protected endpoint 401 and never retries a write', async () => {
  vi.mocked(api.restoreLocalRunnerSession).mockResolvedValue({
    actorId: id, expiresAtUtc: new Date(Date.now() + 3600000).toISOString(), expiresInSeconds: 3600,
  });

  render(<ModelConversationWorkspace />);
  await screen.findByText('Start a conversation');
  fireEvent.change(screen.getByLabelText('Conversation title'), { target: { value: 'Update value' } });
  fireEvent.change(screen.getByLabelText('Authorized repository'), { target: { value: 'fixture' } });
  await waitFor(() => expect(screen.getByLabelText('One permitted C# source file')).toHaveValue('src/Value.cs'));
  vi.mocked(api.createModelConversation).mockImplementationOnce(async () => {
    window.dispatchEvent(new Event(OPERATOR_SESSION_ENDED));
    throw Object.assign(new Error('Stale unauthorized response'), { status: 401 });
  });
  fireEvent.click(screen.getByText('Create conversation'));
  await screen.findByText(/expired or was revoked/);
  expect(screen.queryByText(/Stale unauthorized response/)).not.toBeInTheDocument();
  expect(screen.queryByText('Start a conversation')).not.toBeInTheDocument();
  expect(screen.getByLabelText('Local operator bootstrap token')).toBeEnabled();
  expect(api.createModelConversation).toHaveBeenCalledTimes(1);
  expect(api.createLocalRunnerSession).not.toHaveBeenCalled();
  expect(api.approveModelCodeEdit).not.toHaveBeenCalled();
});

it('shows explicit reauthentication at absolute expiry even while idle', async () => {
  vi.useFakeTimers();
  vi.mocked(api.restoreLocalRunnerSession).mockResolvedValue({
    actorId: id, expiresAtUtc: new Date(Date.now() + 2000).toISOString(), expiresInSeconds: 2,
  });
  await act(async () => { render(<ModelConversationWorkspace />); });
  expect(screen.getByText('Start a conversation')).toBeInTheDocument();
  await act(async () => { vi.advanceTimersByTime(2000); });
  expect(screen.queryByText('Start a conversation')).not.toBeInTheDocument();
  expect(screen.getByText(/Operator session expired. Reauthenticate explicitly/)).toBeInTheDocument();
  expect(screen.getByLabelText('Local operator bootstrap token')).toBeEnabled();
  expect(api.createLocalRunnerSession).not.toHaveBeenCalled();
  expect(api.executeLocalHealthRun).not.toHaveBeenCalled();
});

it('does not resurrect a session when a 401 invalidates an in-flight restore', async () => {
  vi.mocked(api.restoreLocalRunnerSession).mockResolvedValue({
    actorId: id, expiresAtUtc: new Date(Date.now() + 3600000).toISOString(), expiresInSeconds: 3600,
  });
  let complete!: (value: Awaited<ReturnType<typeof api.localCodeModel>>) => void;
  vi.mocked(api.localCodeModel).mockImplementationOnce(() => new Promise(resolve => { complete = resolve; }));
  render(<ModelConversationWorkspace />);
  await waitFor(() => expect(api.localCodeModel).toHaveBeenCalled());
  await act(async () => {
    window.dispatchEvent(new Event(OPERATOR_SESSION_ENDED));
    complete({ configured: true, provider: 'podman-ai-lab', model: 'mistral-local', timeoutSeconds: 60, readiness: 'available' });
  });
  expect(screen.queryByText('Start a conversation')).not.toBeInTheDocument();
  expect(screen.getByLabelText('Local operator bootstrap token')).toBeEnabled();
  expect(api.createLocalRunnerSession).not.toHaveBeenCalled();
});
