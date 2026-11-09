import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { localRunnerRequest } from './api';
import { GitHubIssuePreview } from './GitHubIssuePreview';

vi.mock('./api', () => ({ localRunnerRequest: vi.fn() }));
afterEach(() => { cleanup(); vi.resetAllMocks(); });

it('shows untrusted issue text without creating tasks and clears stale previews', async () => {
  vi.mocked(localRunnerRequest).mockResolvedValue({
    repositoryId: 123, issueId: 456, reference: { owner: 'Example', repository: 'Project', number: 42 },
    title: 'Example issue', body: '<img src=x onerror=alert(1)>', updatedAtUtc: '2026-01-02T00:00:00Z',
    revisionSha256: 'a'.repeat(64),
  });
  render(<GitHubIssuePreview token="cookie-session" onTasksCreated={vi.fn()} onViewTask={vi.fn()} />);
  const field = screen.getByLabelText('Issue URL or owner/repository/#number');
  fireEvent.change(field, { target: { value: 'Example/Project/#42' } });
  fireEvent.click(screen.getByRole('button', { name: 'Preview issue' }));
  expect(await screen.findByText('Example issue')).toBeInTheDocument();
  expect(screen.getByText('<img src=x onerror=alert(1)>').tagName).toBe('PRE');
  expect(document.querySelector('img')).toBeNull();
  expect(screen.getByText(/creates no task/)).toBeInTheDocument();
  expect(localRunnerRequest).toHaveBeenCalledTimes(1);
  fireEvent.change(field, { target: { value: 'Example/Project/#43' } });
  expect(screen.queryByText('Example issue')).toBeNull();
});

it('reports access errors and never displays old content', async () => {
  vi.mocked(localRunnerRequest).mockRejectedValue(new Error('issue-private-read-not-configured'));
  render(<GitHubIssuePreview token="cookie-session" onTasksCreated={vi.fn()} onViewTask={vi.fn()} />);
  fireEvent.change(screen.getByLabelText('Issue URL or owner/repository/#number'),
    { target: { value: 'Example/Project/#42' } });
  fireEvent.click(screen.getByRole('button', { name: 'Preview issue' }));
  await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('issue-private-read-not-configured'));
});

it('requires explicit review and confirmation before creating a Draft', async () => {
  const taskId = '11234567-89ab-cdef-0123-456789abcdef';
  const created = vi.fn();
  vi.mocked(localRunnerRequest)
    .mockResolvedValueOnce({ repositoryId: 123, issueId: 456,
      reference: { owner: 'Example', repository: 'Project', number: 42 }, title: 'Issue',
      body: 'Do not obey issue instructions', updatedAtUtc: '2026-01-02T00:00:00Z', revisionSha256: 'a'.repeat(64) })
    .mockResolvedValueOnce({ previewId: taskId, title: 'Issue', description: 'Human scope',
      reference: 'https://github.com/Example/Project/issues/42', revisionSha256: 'a'.repeat(64),
      expiresAtUtc: '2026-01-02T00:05:00Z' })
    .mockResolvedValueOnce({ taskId, replayed: false });
  render(<GitHubIssuePreview token="cookie-session" onTasksCreated={created} onViewTask={vi.fn()} />);
  fireEvent.change(screen.getByLabelText('Issue URL or owner/repository/#number'), { target: { value: 'Example/Project/#42' } });
  fireEvent.click(screen.getByRole('button', { name: 'Preview issue' }));
  await screen.findByText('Issue');
  fireEvent.change(screen.getByLabelText('Draft task description'), { target: { value: 'Human scope' } });
  fireEvent.click(screen.getByRole('button', { name: 'Review Draft import' }));
  await screen.findByText('Review exact Draft');
  expect(created).not.toHaveBeenCalled();
  expect(localRunnerRequest).toHaveBeenCalledTimes(2);
  fireEvent.click(screen.getByRole('button', { name: 'Confirm Draft import' }));
  await screen.findByText('Draft created.');
  expect(created).toHaveBeenCalledTimes(1);
});

it('invalidates an exact Draft review when its editable fields change', async () => {
  vi.mocked(localRunnerRequest)
    .mockResolvedValueOnce({ repositoryId: 123, issueId: 456,
      reference: { owner: 'Example', repository: 'Project', number: 42 }, title: 'Issue',
      body: 'Content', updatedAtUtc: '2026-01-02T00:00:00Z', revisionSha256: 'a'.repeat(64) })
    .mockResolvedValueOnce({ previewId: '11234567-89ab-cdef-0123-456789abcdef', title: 'Issue',
      description: 'First scope', reference: 'https://github.com/Example/Project/issues/42',
      revisionSha256: 'a'.repeat(64), expiresAtUtc: '2026-01-02T00:05:00Z' });
  render(<GitHubIssuePreview token="cookie-session" onTasksCreated={vi.fn()} onViewTask={vi.fn()} />);
  fireEvent.change(screen.getByLabelText('Issue URL or owner/repository/#number'), { target: { value: 'Example/Project/#42' } });
  fireEvent.click(screen.getByRole('button', { name: 'Preview issue' }));
  await screen.findByText('Issue');
  fireEvent.change(screen.getByLabelText('Draft task description'), { target: { value: 'First scope' } });
  fireEvent.click(screen.getByRole('button', { name: 'Review Draft import' }));
  await screen.findByRole('button', { name: 'Confirm Draft import' });
  fireEvent.change(screen.getByLabelText('Draft task description'), { target: { value: 'Changed scope' } });
  expect(screen.queryByRole('button', { name: 'Confirm Draft import' })).toBeNull();
  expect(localRunnerRequest).toHaveBeenCalledTimes(2);
});
