import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import App from './App';
import { api, ApiError } from './api';

vi.mock('./api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('./api')>();
  return {
    ...actual,
    api: {
      ...actual.api,
      currentUser: vi.fn(),
      login: vi.fn(),
      logout: vi.fn(),
    },
  };
});

const users = [
  { id: '01234567-89ab-cdef-0123-456789abcdef', email: 'alex@example.test', username: 'alex' },
  { id: '11111111-2222-3333-4444-555555555555', email: 'blair@example.test', username: 'blair' },
  { id: 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee', email: 'casey@example.test', username: 'casey' },
];

beforeEach(() => {
  vi.resetAllMocks();
  window.localStorage.clear();
  window.sessionStorage.clear();
  vi.mocked(api.currentUser).mockResolvedValue(null);
  vi.mocked(api.login).mockResolvedValue(undefined);
  vi.mocked(api.logout).mockResolvedValue(undefined);
});
afterEach(() => {
  cleanup();
  window.localStorage.clear();
  window.sessionStorage.clear();
  window.history.replaceState(null, '', '/');
});

describe('HIC browser sign-in and account session', () => {
  it.each(users)('renders the server-verified account for $username', async (user) => {
    vi.mocked(api.currentUser).mockResolvedValue(user);
    render(<App />);

    expect(await screen.findByText(user.email)).toBeInTheDocument();
    expect(screen.getByText(user.username)).toBeInTheDocument();
    expect(screen.getByText(user.id)).toBeInTheDocument();
    expect(api.currentUser).toHaveBeenCalledTimes(1);
  });

  it('starts on sign-in, verifies the session, and does not load legacy workspace data', async () => {
    render(<App />);

    expect(screen.getByRole('status')).toHaveTextContent('Verifying your current account with the server');
    expect(await screen.findByRole('heading', { name: 'Sign in to HIC' })).toBeInTheDocument();
    expect(screen.getByRole('textbox', { name: 'Email or username' })).toBeInTheDocument();
    expect(screen.getByLabelText('Password')).toHaveAttribute('type', 'password');
    expect(api.currentUser).toHaveBeenCalledTimes(1);
    expect(window.localStorage.length).toBe(0);
    expect(window.sessionStorage.length).toBe(0);
  });

  it('submits with the keyboard form flow, clears the password, and displays the newly verified user', async () => {
    vi.mocked(api.currentUser).mockResolvedValueOnce(null).mockResolvedValueOnce(users[0]);
    render(<App />);
    const form = await screen.findByRole('form');
    fireEvent.change(screen.getByRole('textbox', { name: 'Email or username' }), { target: { value: 'alex' } });
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'entered-only-for-this-test' } });
    fireEvent.submit(form);

    expect(await screen.findByText(users[0].email)).toBeInTheDocument();
    expect(api.login).toHaveBeenCalledWith('alex', 'entered-only-for-this-test');
    expect(api.currentUser).toHaveBeenCalledTimes(2);
    expect(window.localStorage.length).toBe(0);
    expect(window.sessionStorage.length).toBe(0);
  });

  it('shows invalid credentials without echoing or persisting the password', async () => {
    vi.mocked(api.login).mockRejectedValue(new ApiError('http', 'Rejected.', 401));
    render(<App />);
    await screen.findByRole('heading', { name: 'Sign in to HIC' });
    fireEvent.change(screen.getByRole('textbox', { name: 'Email or username' }), { target: { value: 'unknown' } });
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'never-echo-this-value' } });
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Email/username or password is incorrect.');
    expect(screen.getByLabelText('Password')).toHaveValue('');
    expect(screen.queryByText('never-echo-this-value')).not.toBeInTheDocument();
    expect(window.localStorage.length).toBe(0);
    expect(window.sessionStorage.length).toBe(0);
  });

  it.each([
    [400, 'CSRF check failed'],
    [429, 'Too many sign-in attempts'],
    [503, 'authentication is not configured'],
  ])('explains authentication failure status %i', async (status, message) => {
    vi.mocked(api.login).mockRejectedValue(new ApiError('http', 'Rejected.', status));
    render(<App />);
    await screen.findByRole('heading', { name: 'Sign in to HIC' });
    fireEvent.change(screen.getByRole('textbox', { name: 'Email or username' }), { target: { value: 'alex' } });
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'private-test-input' } });
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(message);
  });

  it('reports a backend network failure without leaking request content', async () => {
    vi.mocked(api.currentUser).mockRejectedValue(new ApiError('network', 'Network failure.'));
    render(<App />);

    expect(await screen.findByRole('alert')).toHaveTextContent('Cannot reach the authentication service.');
    expect(screen.getByRole('heading', { name: 'Sign in to HIC' })).toBeInTheDocument();
  });

  it('clears a revoked or expired session after server refresh', async () => {
    vi.mocked(api.currentUser).mockResolvedValueOnce(users[1])
      .mockRejectedValueOnce(new ApiError('http', 'Unauthorized.', 401));
    render(<App />);
    expect(await screen.findByText(users[1].email)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Refresh session' }));

    expect(await screen.findByRole('heading', { name: 'Sign in to HIC' })).toBeInTheDocument();
    expect(await screen.findByRole('alert')).toHaveTextContent('session expired or is no longer active');
    expect(screen.queryByText(users[1].email)).not.toBeInTheDocument();
  });

  it('revokes the server session through logout and clears the displayed identity', async () => {
    vi.mocked(api.currentUser).mockResolvedValue(users[2]);
    render(<App />);
    expect(await screen.findByText(users[2].email)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Sign out' }));

    expect(await screen.findByText('You have signed out.')).toBeInTheDocument();
    expect(api.logout).toHaveBeenCalledTimes(1);
    expect(screen.queryByText(users[2].email)).not.toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Sign in to HIC' })).toBeInTheDocument();
  });

  it('clears the account view if logout cannot be confirmed', async () => {
    vi.mocked(api.currentUser).mockResolvedValue(users[0]);
    vi.mocked(api.logout).mockRejectedValue(new ApiError('network', 'Offline.'));
    render(<App />);
    expect(await screen.findByText(users[0].email)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Sign out' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('server session may still be active');
    expect(screen.queryByText(users[0].email)).not.toBeInTheDocument();
  });

  it('keeps developer preview visibly separate and ignores a stale session response after leaving sign-in', async () => {
    let resolveSession!: (user: typeof users[number] | null) => void;
    vi.mocked(api.currentUser).mockImplementationOnce(() => new Promise(resolve => { resolveSession = resolve; }));
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'Open developer preview' }));
    expect(await screen.findByRole('note', { name: 'Developer preview boundary' })).toHaveTextContent('not a private user workspace');

    await act(async () => { resolveSession(users[0]); });
    expect(screen.queryByText(users[0].email)).not.toBeInTheDocument();
    vi.mocked(api.currentUser).mockResolvedValueOnce(null);
    fireEvent.click(screen.getByRole('button', { name: 'Return to sign in or account' }));
    expect(await screen.findByRole('heading', { name: 'Sign in to HIC' })).toBeInTheDocument();
    await waitFor(() => expect(api.currentUser).toHaveBeenCalledTimes(2));
  });

  it('opens recovery without pretending an email was sent', async () => {
    render(<App />);
    await screen.findByRole('heading', { name: 'Sign in to HIC' });
    fireEvent.click(screen.getByRole('button', { name: 'Forgot password?' }));
    expect(await screen.findByRole('heading', { name: 'Reset your password' })).toBeInTheDocument();
    expect(screen.queryByText(/If an account exists/)).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Back to sign in' }));
    expect(await screen.findByRole('heading', { name: 'Sign in to HIC' })).toBeInTheDocument();
  });
  it('scrubs the reset token before requesting the current session', async () => {
    const token = 'a'.repeat(43) + '=';
    window.history.replaceState(null, '', '/?reset_token=' + encodeURIComponent(token));
    vi.mocked(api.currentUser).mockImplementation(async () => {
      expect(window.location.search).not.toContain('reset_token');
      return null;
    });
    render(<App />);

    expect(await screen.findByRole('heading', { name: 'Reset your password' })).toBeInTheDocument();
    expect(window.location.search).toBe('');
    expect(screen.getByLabelText('New password')).toBeInTheDocument();
  });

});
