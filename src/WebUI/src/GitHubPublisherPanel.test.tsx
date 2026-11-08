import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { GitHubPublisherPanel } from './GitHubPublisherPanel';
import { githubApi, type GitHubPublication } from './githubApi';

vi.mock('./githubApi', () => ({ githubApi: {
  status: vi.fn(), bootstraps: vi.fn(), preview: vi.fn(), bootstrapPreview: vi.fn(),
  approve: vi.fn(), reject: vi.fn(), execute: vi.fn(), reconcile: vi.fn(), completeDraft: vi.fn(),
} }));
const id = '11234567-89ab-cdef-0123-456789abcdef';
const now = new Date().toISOString();
const preview: GitHubPublication = {
  publicationId: id, runId: id, state: 'preview', intentDigestSha256: 'a'.repeat(64),
  commitSha: null, remoteSha: null, prNumber: null, prUrl: null, errorCode: null, updatedAtUtc: now, events: [],
  plan: {
    publicationId: id, actorId: id, taskId: id, runId: id, conversationId: id, kind: 'task',
    destination: { id: 123, owner: 'Cabatoy', name: 'hic-agent-sandbox', baseBranch: 'SaasMain' },
    baseCommit: 'b'.repeat(40), baseTree: 'c'.repeat(40), taskBranch: `hic/task-${id.replaceAll('-', '')}`,
    file: 'src/Value.cs', content: 'public static class Value {}\n', contentDigest: 'd'.repeat(64),
    codePlanDigest: 'e'.repeat(64), credentialScopeDigest: 'f'.repeat(64),
    expiresAtUtc: new Date(Date.now() + 240000).toISOString(),
  },
};
const succeeded: GitHubPublication = { ...preview, state: 'succeeded', commitSha: 'f'.repeat(40),
  remoteSha: 'f'.repeat(40), prNumber: 52, prUrl: 'https://github.com/Cabatoy/hic-agent-sandbox/pull/52',
  events: [{ eventCode: 'succeeded', recordedAtUtc: now }] };
beforeEach(() => {
  vi.mocked(githubApi.status).mockResolvedValue({ configured: true, repository: 'Cabatoy/hic-agent-sandbox', missing: [],
    appId: '1234', installationId: '5678', runtimeRevision: 'a'.repeat(40) });
  vi.mocked(githubApi.preview).mockResolvedValue(preview);
  vi.mocked(githubApi.approve).mockResolvedValue({ ...preview, state: 'pending' });
  vi.mocked(githubApi.execute).mockResolvedValue(succeeded);
  vi.mocked(githubApi.reconcile).mockResolvedValue(succeeded);
  vi.mocked(githubApi.bootstraps).mockResolvedValue([]);
});
afterEach(() => { cleanup(); vi.resetAllMocks(); });

it('requires separate exact publication approval and displays actual commit/ref/draft after reload', async () => {
  const mounted = render(<GitHubPublisherPanel token="session-only" runId={id} />);
  fireEvent.click(await screen.findByText('Preview GitHub task publication'));
  fireEvent.click(await screen.findByText('Approve exact GitHub write scope'));
  expect(githubApi.execute).not.toHaveBeenCalled();
  fireEvent.click(await screen.findByText('Execute approved GitHub publication'));
  expect(await screen.findByRole('link', { name: 'Draft PR #52' })).toHaveAttribute('href', succeeded.prUrl);
  expect(screen.getByText(/Remote branch SHA:/)).toHaveTextContent('f'.repeat(40));
  expect(githubApi.approve).toHaveBeenCalledWith(preview, 'session-only');
  mounted.unmount();
  render(<GitHubPublisherPanel token="new-session" runId={id} saved={[succeeded]} />);
  expect(await screen.findByRole('link', { name: 'Draft PR #52' })).toBeInTheDocument();
  expect(screen.queryByText('Execute approved GitHub publication')).not.toBeInTheDocument();
});

it('shows unknown external outcome without retrying a write and provides read-only reconciliation', async () => {
  const unknown = { ...preview, state: 'unknown', errorCode: 'github-response-unavailable' };
  render(<GitHubPublisherPanel token="session-only" runId={id} saved={[unknown]} />);
  expect(await screen.findByText(/External write outcome is unknown/)).toBeInTheDocument();
  expect(githubApi.execute).not.toHaveBeenCalled();
  fireEvent.click(screen.getByText('Read-only remote reconciliation'));
  expect(await screen.findByRole('link', { name: 'Draft PR #52' })).toBeInTheDocument();
  expect(githubApi.reconcile).toHaveBeenCalledWith(id, 'session-only');
});

it('keeps empty-repository bootstrap approval separate from task publishing and restores its saved state', async () => {
  const bootstrap = { ...preview, runId: null, state: 'pending', plan: { ...preview.plan, kind: 'bootstrap' as const,
    runId: null, taskId: null, conversationId: null, baseCommit: null, baseTree: null, taskBranch: 'SaasMain' } };
  vi.mocked(githubApi.bootstraps).mockResolvedValue([bootstrap]);
  render(<GitHubPublisherPanel token="session-only" />);
  expect(await screen.findByText(/This separate one-time approval/)).toBeInTheDocument();
  expect(screen.getByText(/EMPTY — bootstrap only/)).toBeInTheDocument();
  expect(githubApi.execute).not.toHaveBeenCalled();
});

it('reports missing backend configuration without accepting private keys in chat', async () => {
  vi.mocked(githubApi.status).mockResolvedValue({
    configured: false, repository: '', missing: ['HIC_GITHUB_APP_ID', 'HIC_GITHUB_PRIVATE_KEY_PATH'],
  });
  render(<GitHubPublisherPanel token="session-only" runId={id} />);
  expect(await screen.findByText(/HIC_GITHUB_APP_ID, HIC_GITHUB_PRIVATE_KEY_PATH/)).toBeInTheDocument();
  expect(screen.queryByText('Preview GitHub task publication')).not.toBeInTheDocument();
  expect(screen.queryByRole('textbox')).not.toBeInTheDocument();
});

it('shows the immutable repository, credential identity and exact bootstrap diff before a human decision', async () => {
  const bootstrap = { ...preview, runId: null, plan: { ...preview.plan, kind: 'bootstrap' as const,
    runId: null, taskId: null, baseCommit: null, baseTree: null, taskBranch: 'SaasMain' } };
  vi.mocked(githubApi.bootstraps).mockResolvedValue([bootstrap]);
  render(<GitHubPublisherPanel token="session-only" />);
  await screen.findByText('Exact initial file diff');
  expect(screen.getByText(/Immutable repository ID:/)).toHaveTextContent('123');
  expect(screen.getByText(/GitHub App:/)).toHaveTextContent('1234');
  expect(screen.getByText(/GitHub App:/)).toHaveTextContent('5678');
  expect(screen.getByText(/--- \/dev\/null/)).toHaveTextContent('+++ b/src/Value.cs');
  expect(githubApi.approve).not.toHaveBeenCalled();
  expect(githubApi.execute).not.toHaveBeenCalled();
});

it('records rejection without approval, execution or reconciliation', async () => {
  vi.mocked(githubApi.reject).mockResolvedValue({ ...preview, state: 'blocked', errorCode: 'human-rejected' });
  render(<GitHubPublisherPanel token="session-only" runId={id} saved={[preview]} />);
  fireEvent.click(await screen.findByText('Reject exact GitHub write'));
  expect(await screen.findByText(/Human rejected this write/)).toBeInTheDocument();
  expect(githubApi.reject).toHaveBeenCalledWith(preview, 'session-only');
  expect(githubApi.approve).not.toHaveBeenCalled();
  expect(githubApi.execute).not.toHaveBeenCalled();
  expect(screen.queryByText('Read-only remote reconciliation')).not.toBeInTheDocument();
});
