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
  render(<GitHubIssuePreview token="cookie-session" />);
  const field = screen.getByLabelText('Issue URL or owner/repository/#number');
  fireEvent.change(field, { target: { value: 'Example/Project/#42' } });
  fireEvent.click(screen.getByRole('button', { name: 'Preview issue' }));
  expect(await screen.findByText('Example issue')).toBeInTheDocument();
  expect(screen.getByText('<img src=x onerror=alert(1)>').tagName).toBe('PRE');
  expect(document.querySelector('img')).toBeNull();
  expect(screen.getByText(/This preview creates no task/)).toBeInTheDocument();
  expect(localRunnerRequest).toHaveBeenCalledTimes(1);
  fireEvent.change(field, { target: { value: 'Example/Project/#43' } });
  expect(screen.queryByText('Example issue')).toBeNull();
});

it('reports access errors and never displays old content', async () => {
  vi.mocked(localRunnerRequest).mockRejectedValue(new Error('issue-private-read-not-configured'));
  render(<GitHubIssuePreview token="cookie-session" />);
  fireEvent.change(screen.getByLabelText('Issue URL or owner/repository/#number'),
    { target: { value: 'Example/Project/#42' } });
  fireEvent.click(screen.getByRole('button', { name: 'Preview issue' }));
  await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('issue-private-read-not-configured'));
});
