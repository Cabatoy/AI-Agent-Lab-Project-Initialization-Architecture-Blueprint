import { useLayoutEffect, useRef, useState, type FormEvent } from 'react';
import { api, ApiError } from './api';

function errorFor(reason: unknown): string {
  if (reason instanceof ApiError) {
    if (reason.status === 404 || reason.status === 503)
      return 'Password recovery is not available on this server yet. No email delivery was confirmed.';
    if (reason.status === 429) return 'Too many requests. Try again later.';
    if (reason.status === 400 || reason.status === 401 || reason.status === 403 || reason.kind === 'invalid')
      return 'The recovery request or link was rejected. Check the details and try again.';
    if (reason.kind === 'network' || reason.kind === 'timeout')
      return 'The server did not confirm the request. Check the connection before trying again.';
  }
  return 'Password recovery was not confirmed. Try again.';
}

export function PasswordRecovery({ onBack, initialToken }: { onBack: () => void; initialToken?: string }) {
  const [token, setToken] = useState(() => initialToken ?? new URLSearchParams(window.location.search).get('reset_token') ?? '');
  const [identifier, setIdentifier] = useState('');
  const [password, setPassword] = useState('');
  const [confirmation, setConfirmation] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const pending = useRef(false);
  const mounted = useRef(true);

  useLayoutEffect(() => {
    mounted.current = true;
    // A reset token is a bearer secret: remove it from history before showing the form.
    const url = new URL(window.location.href);
    if (url.searchParams.has('reset_token')) {
      url.searchParams.delete('reset_token');
      window.history.replaceState(window.history.state, '', url.pathname + url.search + url.hash);
    }
    return () => { mounted.current = false; };
  }, []);

  const submit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (pending.current || notice) return;
    if (token && (password !== confirmation || password.length < 12 || password.length > 1024)) {
      setError('The passwords must match and contain at least 12 characters.');
      return;
    }
    const submittedPassword = password;
    setPassword('');
    setConfirmation('');
    pending.current = true;
    setBusy(true);
    setError(null);
    try {
      if (token) {
        await api.completePasswordReset(token, submittedPassword);
        if (mounted.current) {
          setToken('');
          setNotice('Password changed. Sign in with your new password.');
        }
      } else {
        await api.requestPasswordReset(identifier.trim());
        if (mounted.current)
          setNotice('If an account exists, follow the instructions sent to its registered email address.');
      }
    } catch (reason) {
      if (mounted.current) setError(errorFor(reason));
    } finally {
      pending.current = false;
      if (mounted.current) setBusy(false);
    }
  };

  return <>
    <p className="eyebrow">ACCOUNT RECOVERY</p>
    <h1 id="auth-title">Reset your password</h1>
    <p className="auth-subtitle">{token
      ? 'Choose a new password using the one-time recovery link.'
      : 'Request a recovery link for your HIC account.'}</p>
    {!notice && <form className="auth-form" aria-label="Password recovery form" autoComplete="off" onSubmit={submit}>
      {token ? <>
        <label htmlFor="recovery-password">New password</label>
        <input id="recovery-password" type="password" autoComplete="new-password" minLength={12} maxLength={1024}
          value={password} onChange={event => setPassword(event.target.value)} disabled={busy} required />
        <label htmlFor="recovery-confirm">Confirm new password</label>
        <input id="recovery-confirm" type="password" autoComplete="new-password" minLength={12} maxLength={1024}
          value={confirmation} onChange={event => setConfirmation(event.target.value)} disabled={busy} required />
      </> : <>
        <label htmlFor="recovery-identifier">Account email or username</label>
        <input id="recovery-identifier" type="text" autoComplete="username" maxLength={320}
          value={identifier} onChange={event => setIdentifier(event.target.value)} disabled={busy} required />
      </>}
      {error && <p className="auth-error" role="alert">{error}</p>}
      <button className="primary auth-submit" type="submit" disabled={busy}>
        {busy ? 'Contacting server…' : token ? 'Change password' : 'Request recovery link'}
      </button>
    </form>}
    {notice && <p className="auth-notice" role="status">{notice}</p>}
    <p className="auth-subtitle">Recovery requests must be confirmed by the server. An unavailable API never counts as an email sent.</p>
    <button className="text-button" type="button" onClick={onBack} disabled={busy}>Back to sign in</button>
  </>;
}
