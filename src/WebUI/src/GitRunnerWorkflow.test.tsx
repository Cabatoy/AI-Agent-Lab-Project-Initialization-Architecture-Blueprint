import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { api } from './api';
import { gitApi, type GitRun } from './gitApi';
import { GitRunnerWorkflow } from './GitRunnerWorkflow';

vi.mock('./api', async importOriginal => ({ ...await importOriginal<typeof import('./api')>(),
  api: { restoreLocalRunnerSession: vi.fn(), revokeLocalRunnerSession: vi.fn(),
    createLocalRunnerSession: vi.fn(), tasks: vi.fn(), executeLocalHealthRun: vi.fn() } }));
vi.mock('./gitApi', () => ({ gitApi: { repositories: vi.fn(), preview: vi.fn(), approve: vi.fn(), run: vi.fn() } }));
const taskId = '11234567-89ab-cdef-0123-456789abcdef';
const plan = { revision: 1, taskId, actorId: taskId, taskDigest: 'a'.repeat(64), repositoryId: 'fixture',
  repositoryPath: 'C:\\fixture', baseCommit: 'b'.repeat(40), file: 'message.txt' as const, content: 'approved\n',
  expiresAtUtc: new Date(Date.now() + 240000).toISOString() };
const preview = { plan, intentDigestSha256: 'c'.repeat(64) };
const queued: GitRun = { runId: taskId, taskId, action: 'repository.test-edit',
  scope: 'disposable-worktree:message.txt:write', intentDigestSha256: preview.intentDigestSha256,
  status: 'queued', plan, result: null, events: [] };

beforeEach(() => {
  vi.mocked(api.restoreLocalRunnerSession).mockRejectedValue({ status: 401 });
  vi.mocked(api.revokeLocalRunnerSession).mockResolvedValue({ revoked: true });
  vi.mocked(api.createLocalRunnerSession).mockResolvedValue({ accessToken: 's'.repeat(44), expiresInSeconds: 900 });
  vi.mocked(api.tasks).mockResolvedValue([{ id: taskId, title: 'Fixture edit', description: '', status: 'Draft',
    attemptCount: 0, result: null, errorMessage: null, createdAtUtc: plan.expiresAtUtc, updatedAtUtc: plan.expiresAtUtc }]);
  vi.mocked(gitApi.repositories).mockResolvedValue([{ id: 'fixture', path: plan.repositoryPath, baseCommit: plan.baseCommit, available: true }]);
  vi.mocked(gitApi.preview).mockResolvedValue(preview);
  vi.mocked(gitApi.approve).mockResolvedValue({ runId: taskId });
  vi.mocked(gitApi.run).mockResolvedValue(queued);
  vi.mocked(api.executeLocalHealthRun).mockResolvedValue({ status: 'completed' });
});
afterEach(() => { cleanup(); vi.resetAllMocks(); });
async function prepare() {
  render(<GitRunnerWorkflow />);
  await waitFor(() => expect(screen.getByLabelText('Local operator bootstrap token')).toBeEnabled());
  fireEvent.change(screen.getByLabelText('Local operator bootstrap token'), { target: { value: 'test' } });
  fireEvent.click(screen.getByText('Authenticate Git operator'));
  await screen.findByText('Repository, task and bounded scope');
  fireEvent.change(screen.getByLabelText('Draft task'), { target: { value: taskId } });
  fireEvent.change(screen.getByLabelText('Authorized disposable repository'), { target: { value: 'fixture' } });
  fireEvent.click(screen.getByText('Preview exact Git plan'));
  await screen.findByText('Plan revision 1');
}
it('restores the shared operator cookie without granting or replaying Git approval', async () => {
  vi.mocked(api.restoreLocalRunnerSession).mockResolvedValue({
    actorId: taskId, expiresInSeconds: 3600, expiresAtUtc: new Date(Date.now() + 3600000).toISOString(),
  });
  render(<GitRunnerWorkflow />);
  await screen.findByText('Repository, task and bounded scope');
  expect(gitApi.repositories).toHaveBeenCalledWith('cookie-session');
  expect(api.createLocalRunnerSession).not.toHaveBeenCalled();
  expect(gitApi.preview).not.toHaveBeenCalled();
  expect(gitApi.approve).not.toHaveBeenCalled();
  expect(api.executeLocalHealthRun).not.toHaveBeenCalled();
  fireEvent.click(screen.getByText('End Git session'));
  await screen.findByText(/Signed out/);
  expect(api.revokeLocalRunnerSession).toHaveBeenCalledWith('cookie-session');
});
it('requires exact approval before execution and displays durable provenance, diff and test outcome', async () => {
  await prepare();
  expect(api.executeLocalHealthRun).not.toHaveBeenCalled();
  fireEvent.click(screen.getByText('Approve exact Git plan'));
  await screen.findByText('Run status: queued');
  expect(gitApi.approve).toHaveBeenCalledWith(taskId, preview, 's'.repeat(44));
  vi.mocked(gitApi.run).mockResolvedValue({ ...queued, status: 'completed',
    result: { action: 'repository.test-edit', runId: taskId, repositoryId: 'fixture', baseCommit: plan.baseCommit,
      worktreePath: 'C:\\runs\\fixture', branch: 'hic-run-test', diff: '+approved', testCode: 'passed:exact-content-and-scope',
      sourceUnchanged: true, checkedAtUtc: plan.expiresAtUtc }, events: [{ eventCode: 'completed', recordedAtUtc: plan.expiresAtUtc }] });
  fireEvent.click(screen.getByText('Execute approved isolated edit'));
  await screen.findByText('Run status: completed');
  expect(screen.getByText('+approved')).toBeInTheDocument();
  expect(screen.queryByText('Execute approved isolated edit')).not.toBeInTheDocument();
});
it('invalidates preview on material edits without silently approving', async () => {
  await prepare();
  fireEvent.change(screen.getByLabelText(/message.txt replacement/), { target: { value: 'changed\n' } });
  expect(screen.queryByText('Approve exact Git plan')).not.toBeInTheDocument();
  expect(gitApi.approve).not.toHaveBeenCalled();
});
it('does not retry an interrupted execution and offers durable status recovery', async () => {
  await prepare();
  fireEvent.click(screen.getByText('Approve exact Git plan'));
  await screen.findByText('Run status: queued');
  vi.mocked(api.executeLocalHealthRun).mockRejectedValue(new Error('Interrupted'));
  fireEvent.click(screen.getByText('Execute approved isolated edit'));
  await screen.findByRole('alert');
  expect(api.executeLocalHealthRun).toHaveBeenCalledTimes(1);
  vi.mocked(gitApi.run).mockResolvedValue({ ...queued, status: 'running' });
  fireEvent.click(screen.getByText('Refresh Git run status'));
  await waitFor(() => expect(screen.getByText('Run status: running')).toBeInTheDocument());
  expect(screen.queryByText('Execute approved isolated edit')).not.toBeInTheDocument();
});
