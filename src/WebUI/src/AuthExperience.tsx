import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import { api, ApiError, type HicAccount } from './api';
import { PasswordRecovery } from './PasswordRecovery';

const signInLocales = {
  en: {
    language: 'Sign-in language',
    heading: 'Sign in to HIC',
    description: 'Use your HIC account credentials to verify your personal account session.',
    form: 'Sign-in form',
    identifier: 'Email or username',
    password: 'Password',
    signIn: 'Sign in',
    signingIn: 'Signing in…',
    forgot: 'Forgot password?',
  },
  tr: {
    language: 'Giriş dili',
    heading: 'HIC’e giriş yap',
    description: 'Hesabını doğrulamak için HIC giriş bilgilerini kullan.',
    form: 'Giriş formu',
    identifier: 'E-posta veya kullanıcı adı',
    password: 'Şifre',
    signIn: 'Giriş yap',
    signingIn: 'Giriş yapılıyor…',
    forgot: 'Şifremi unuttum',
  },
} as const;

type SignInLocale = keyof typeof signInLocales;
const SIGN_IN_LOCALE_KEY = 'hic.sign-in.locale';

function readSignInLocale(): SignInLocale {
  try {
    return window.localStorage.getItem(SIGN_IN_LOCALE_KEY) === 'tr' ? 'tr' : 'en';
  } catch {
    return 'en';
  }
}

type AuthStatus = 'checking' | 'signed-out' | 'signed-in';
type AuthOperation = 'checking' | 'login' | 'logout' | null;

function authError(reason: unknown, operation: 'login' | 'session'): string {
  if (reason instanceof ApiError) {
    if (reason.status === 401)
      return operation === 'login'
        ? 'Email/username or password is incorrect.'
        : 'Your session expired or is no longer active. Sign in again.';
    if (reason.status === 400) return 'The CSRF check failed. Refresh the page and try again.';
    if (reason.status === 403) return 'The authentication request was rejected. Refresh the page and try again.';
    if (reason.status === 429) return 'Too many sign-in attempts. Wait a little and try again.';
    if (reason.status === 503) return 'Sign-in is unavailable because authentication is not configured.';
    if (reason.kind === 'network') return 'Cannot reach the authentication service. Check your connection and try again.';
    if (reason.kind === 'timeout') return 'The authentication service did not respond in time. Try again.';
    if (reason.kind === 'invalid') return 'The authentication service returned an unexpected response.';
  }
  return 'Sign-in could not be completed. Try again.';
}

export function AuthExperience({ onOpenPreview }: { onOpenPreview: () => void }) {
  const [locale, setLocale] = useState(readSignInLocale);
  const copy = signInLocales[locale];

  const changeLocale = (value: string) => {
    const next = value === 'tr' ? 'tr' : 'en';
    setLocale(next);
    try {
      window.localStorage.setItem(SIGN_IN_LOCALE_KEY, next);
    } catch {
      // The current selection still works when browser storage is unavailable.
    }
  };

  const [status, setStatus] = useState<AuthStatus>('checking');
  const [resetToken, setResetToken] = useState(() => new URLSearchParams(window.location.search).get('reset_token') ?? '');
  const [recovering, setRecovering] = useState(() => Boolean(resetToken));
  // Scrub bearer token before passive effects can start authentication HTTP requests.
  useLayoutEffect(() => {
    if (!resetToken) return;
    const url = new URL(window.location.href);
    if (url.searchParams.has('reset_token')) {
      url.searchParams.delete('reset_token');
      window.history.replaceState(window.history.state, '', url.pathname + url.search + url.hash);
    }
  }, [resetToken]);
  const [operation, setOperation] = useState<AuthOperation>('checking');
  const [account, setAccount] = useState<HicAccount | null>(null);
  const [identifier, setIdentifier] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const requestSequence = useRef(0);

  const verifySession = useCallback(async (announceEnded = false) => {
    const requestId = ++requestSequence.current;
    setOperation('checking');
    setStatus('checking');
    setAccount(null);
    setError(null);
    setNotice(null);
    try {
      const current = await api.currentUser();
      if (requestId !== requestSequence.current) return;
      setAccount(current);
      setStatus(current ? 'signed-in' : 'signed-out');
      if (!current && announceEnded) setNotice('Your session is no longer active. Sign in again.');
    } catch (reason) {
      if (requestId !== requestSequence.current) return;
      setAccount(null);
      setStatus('signed-out');
      setError(authError(reason, 'session'));
    } finally {
      if (requestId === requestSequence.current) setOperation(null);
    }
  }, []);

  useEffect(() => {
    void verifySession();
    return () => { requestSequence.current += 1; };
  }, [verifySession]);

  const submitLogin = async (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    const requestId = ++requestSequence.current;
    const submittedPassword = password;
    setPassword('');
    setError(null);
    setNotice(null);
    setOperation('login');
    try {
      await api.login(identifier.trim(), submittedPassword);
      const current = await api.currentUser();
      if (!current) throw new ApiError('invalid', 'The new session could not be verified.');
      if (requestId !== requestSequence.current) return;
      setAccount(current);
      setStatus('signed-in');
    } catch (reason) {
      if (requestId !== requestSequence.current) return;
      setAccount(null);
      setStatus('signed-out');
      setError(authError(reason, 'login'));
    } finally {
      if (requestId === requestSequence.current) setOperation(null);
    }
  };

  const signOut = async () => {
    const requestId = ++requestSequence.current;
    setAccount(null);
    setStatus('checking');
    setError(null);
    setNotice(null);
    setOperation('logout');
    try {
      await api.logout();
      if (requestId !== requestSequence.current) return;
      setStatus('signed-out');
      setNotice('You have signed out.');
    } catch (reason) {
      if (requestId !== requestSequence.current) return;
      setStatus('signed-out');
      setError(`Sign-out could not be confirmed, so the server session may still be active. ${authError(reason, 'session')}`);
    } finally {
      if (requestId === requestSequence.current) setOperation(null);
    }
  };

  return <main className="auth-page">
    <section className="auth-panel" aria-labelledby="auth-title">
      <div className="auth-brand"><span className="brand-mark" aria-hidden="true">H</span><span>HIC · CONTROL PLANE</span></div>
      {status === 'checking' ? <>
        <h1 id="auth-title">Checking your session</h1>
        <p className="auth-subtitle" role="status">Verifying your current account with the server…</p>
      </> : recovering ? <PasswordRecovery initialToken={resetToken} onBack={() => { setRecovering(false); setResetToken(''); }} /> : status === 'signed-in' && account ? <>
        <p className="eyebrow">PERSONAL ACCOUNT</p>
        <h1 id="auth-title">You are signed in</h1>
        <p className="auth-subtitle">This identity is verified by the current server session.</p>
        <dl className="account-details">
          <div><dt>Username</dt><dd>{account.username}</dd></div>
          <div><dt>Email</dt><dd>{account.email}</dd></div>
          <div><dt>Account ID</dt><dd className="account-id">{account.id}</dd></div>
        </dl>
        {error && <p className="auth-error" role="alert" lang="en">{error}</p>}
        <div className="auth-actions">
          <button className="primary" type="button" onClick={signOut} disabled={operation !== null}>
            {operation === 'logout' ? 'Signing out…' : 'Sign out'}
          </button>
          <button className="secondary" type="button" onClick={() => void verifySession(true)} disabled={operation !== null}>
            {operation === 'checking' ? 'Checking…' : 'Refresh session'}
          </button>
        </div>
      </> : <div lang={locale}>
        <label htmlFor="hic-sign-in-language">{copy.language}</label>{' '}
        <select id="hic-sign-in-language" value={locale} onChange={event => changeLocale(event.target.value)}>
          <option value="en" lang="en">English</option>
          <option value="tr" lang="tr">Türkçe</option>
        </select>
        <p className="eyebrow" lang="en">HUMAN IN CONTROL</p>
        <h1 id="auth-title">{copy.heading}</h1>
        <p className="auth-subtitle">{copy.description}</p>
        <form className="auth-form" aria-label={copy.form} onSubmit={submitLogin} autoComplete="off">
          <label htmlFor="hic-identifier">{copy.identifier}</label>
          <input
            id="hic-identifier"
            name="identifier"
            type="text"
            autoComplete="off"
            maxLength={320}
            required
            value={identifier}
            onChange={event => setIdentifier(event.target.value)}
            disabled={operation !== null}
          />
          <label htmlFor="hic-password">{copy.password}</label>
          <input
            id="hic-password"
            name="password"
            type="password"
            autoComplete="new-password"
            maxLength={1024}
            required
            value={password}
            onChange={event => setPassword(event.target.value)}
            disabled={operation !== null}
          />
          {error && <p className="auth-error" role="alert" lang="en">{error}</p>}
          {notice && <p className="auth-notice" role="status" lang="en">{notice}</p>}
          <button className="primary auth-submit" type="submit" disabled={operation !== null}>
            {operation === 'login' ? copy.signingIn : copy.signIn}
          </button>
        </form>
        <button className="text-button forgot-password" type="button"
          onClick={() => { setError(null); setNotice(null); setRecovering(true); }}>
          {copy.forgot}
        </button>
      </div>}
      <aside className="auth-preview-note" aria-label="Developer preview boundary">
        <strong>Developer preview is separate and not private.</strong>
        <p>Legacy tasks, plans, Command Center and local tools are unprotected and not scoped to user accounts.</p>
        <button className="text-button" type="button" onClick={onOpenPreview}>Open developer preview</button>
      </aside>
      <p className="auth-footer">Signing in does not make legacy preview routes user-scoped.</p>
    </section>
  </main>;
}
