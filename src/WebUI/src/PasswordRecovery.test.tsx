import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { PasswordRecovery } from './PasswordRecovery';
import { api, ApiError } from './api';

vi.mock('./api', async importOriginal => {
  const original = await importOriginal<typeof import('./api')>();
  return { ...original, api: { ...original.api, requestPasswordReset: vi.fn(), completePasswordReset: vi.fn() } };
});
beforeEach(() => {
  vi.resetAllMocks();
  window.history.replaceState(null, '', '/');
  vi.mocked(api.requestPasswordReset).mockResolvedValue(undefined);
  vi.mocked(api.completePasswordReset).mockResolvedValue(undefined);
});
afterEach(() => { cleanup(); window.history.replaceState(null, '', '/'); });

describe('Password recovery UI', () => {
  it('only reports a generic outcome after the server accepts the request', async () => {
    render(<PasswordRecovery onBack={vi.fn()} />);
    expect(screen.queryByText(/If an account exists/)).not.toBeInTheDocument();
    fireEvent.change(screen.getByRole('textbox', { name: 'Account email or username' }),
      { target: { value: ' user@example.test ' } });
    fireEvent.click(screen.getByRole('button', { name: 'Request recovery link' }));
    await waitFor(() => expect(api.requestPasswordReset).toHaveBeenCalledWith('user@example.test'));
    expect(await screen.findByRole('status')).toHaveTextContent('If an account exists');
  });

  it('does not invent mail delivery when the endpoint is missing', async () => {
    vi.mocked(api.requestPasswordReset).mockRejectedValue(new ApiError('http', 'No route', 404));
    render(<PasswordRecovery onBack={vi.fn()} />);
    fireEvent.change(screen.getByRole('textbox', { name: 'Account email or username' }),
      { target: { value: 'user@example.test' } });
    fireEvent.click(screen.getByRole('button', { name: 'Request recovery link' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('not available on this server');
    expect(screen.queryByText(/If an account exists/)).not.toBeInTheDocument();
  });

  it('scrubs bearer tokens from history and clears password input after a confirmed reset', async () => {
    const token = 'a'.repeat(43) + '=';
    window.history.replaceState(null, '', '/?reset_token=' + encodeURIComponent(token));
    render(<PasswordRecovery onBack={vi.fn()} />);
    expect(window.location.search).toBe('');
    fireEvent.change(screen.getByLabelText('New password'), { target: { value: 'new-password-1234' } });
    fireEvent.change(screen.getByLabelText('Confirm new password'), { target: { value: 'new-password-1234' } });
    fireEvent.click(screen.getByRole('button', { name: 'Change password' }));
    await waitFor(() => expect(api.completePasswordReset).toHaveBeenCalledWith(token, 'new-password-1234'));
    expect(await screen.findByRole('status')).toHaveTextContent('Password changed');
    expect(screen.queryByDisplayValue('new-password-1234')).not.toBeInTheDocument();
  });

  it('rejects a mismatched password before making any API call', async () => {
    window.history.replaceState(null, '', '/?reset_token=' + 'a'.repeat(44));
    render(<PasswordRecovery onBack={vi.fn()} />);
    fireEvent.change(screen.getByLabelText('New password'), { target: { value: 'new-password-1234' } });
    fireEvent.change(screen.getByLabelText('Confirm new password'), { target: { value: 'not-the-same-password' } });
    fireEvent.click(screen.getByRole('button', { name: 'Change password' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('must match');
    expect(api.completePasswordReset).not.toHaveBeenCalled();
  });
  it.each([
    [429, 'Too many requests'],
    [503, 'not available on this server'],
    [400, 'was rejected'],
    [401, 'was rejected'],
    [403, 'was rejected'],
  ])('handles recovery HTTP %i without inventing a successful request', async (status, message) => {
    vi.mocked(api.requestPasswordReset).mockRejectedValue(new ApiError('http', 'private upstream response', status));
    render(<PasswordRecovery onBack={vi.fn()} />);
    fireEvent.change(screen.getByRole('textbox', { name: 'Account email or username' }),
      { target: { value: 'hidden@example.test' } });
    fireEvent.click(screen.getByRole('button', { name: 'Request recovery link' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(message);
    expect(screen.queryByText(/If an account exists/)).not.toBeInTheDocument();
    expect(screen.queryByText('private upstream response')).not.toBeInTheDocument();
  });

  it('guards double submissions while the first server response is pending', async () => {
    let finish!: () => void;
    vi.mocked(api.requestPasswordReset).mockImplementation(() => new Promise<void>(resolve => { finish = resolve; }));
    render(<PasswordRecovery onBack={vi.fn()} />);
    fireEvent.change(screen.getByRole('textbox', { name: 'Account email or username' }),
      { target: { value: 'user@example.test' } });
    fireEvent.submit(screen.getByRole('form', { name: 'Password recovery form' }));
    fireEvent.submit(screen.getByRole('form', { name: 'Password recovery form' }));
    expect(api.requestPasswordReset).toHaveBeenCalledTimes(1);
    finish();
    expect(await screen.findByRole('status')).toHaveTextContent('If an account exists');
  });

  it('rejects an expired reset response and never exposes the bearer token', async () => {
    const token = 'b'.repeat(43) + '=';
    window.history.replaceState(null, '', '/?reset_token=' + encodeURIComponent(token));
    vi.mocked(api.completePasswordReset).mockRejectedValue(new ApiError('http', 'sensitive server detail', 400));
    render(<PasswordRecovery onBack={vi.fn()} />);
    fireEvent.change(screen.getByLabelText('New password'), { target: { value: 'new-password-1234' } });
    fireEvent.change(screen.getByLabelText('Confirm new password'), { target: { value: 'new-password-1234' } });
    fireEvent.click(screen.getByRole('button', { name: 'Change password' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('was rejected');
    expect(document.body.textContent).not.toContain(token);
    expect(window.location.search).not.toContain('reset_token');
  });

});
