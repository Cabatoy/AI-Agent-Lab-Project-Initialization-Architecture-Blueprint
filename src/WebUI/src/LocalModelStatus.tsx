import { useState } from 'react';
import { api, isExpiredOperatorSession, type LocalCodeModelInfo } from './api';
import { useOperatorSession } from './useOperatorSession';

export function LocalModelStatus({ openConversations }: { openConversations: () => void }) {
  const [token, setToken] = useState('');
  const [model, setModel] = useState<LocalCodeModelInfo | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  function clear() {
    setToken('');
    setModel(null);
  }
  const session = useOperatorSession(async (accessToken, active) => {
    if (active()) setToken(accessToken);
    const result = await api.localCodeModel(accessToken);
    if (active()) { setModel(result); setError(null); }
  }, clear, reason => setError(reason instanceof Error ? reason.message : 'Model status is unavailable.'));

  async function refresh() {
    setBusy(true);
    setError(null);
    try { setModel(await api.localCodeModel(token)); }
    catch (reason) {
      if (isExpiredOperatorSession(reason)) clear();
      else setError(reason instanceof Error ? reason.message : 'Model status is unavailable.');
      setModel(null);
    } finally { setBusy(false); }
  }

  return <section className="panel">
    <p className="eyebrow">LOCAL PROVIDER DIAGNOSTICS</p>
    <h2>Configured model status</h2>
    {session.restoring && <p role="status">Restoring operator session and checking model status…</p>}
    {session.notice && <p role="status" className="notice">{session.notice}</p>}
    {error && <p role="alert" className="notice error">{error}</p>}
    {!session.restoring && !token && <p className="notice">
      Authenticate in Conversations to inspect the configured local model.
      {' '}<button className="text-button" onClick={openConversations}>Open Conversations</button>
    </p>}
    {token && model && <>
      <p className={model.readiness === 'available' ? 'notice' : 'notice error'}
        role={model.readiness === 'available' ? 'status' : 'alert'}>
        Readiness: {model.readiness}. {model.readiness === 'available'
          ? 'The provider lists this model; generation is not guaranteed.'
          : 'No response or plan will be fabricated.'}
      </p>
      <p>Provider: {model.provider}<br />Model: {model.model || 'not configured'}<br />
        Message timeout: {model.timeoutSeconds}s</p>
    </>}
    {token && <button className="secondary" disabled={busy || session.restoring} onClick={() => void refresh()}>
      {busy ? 'Checking model status…' : 'Refresh model status'}
    </button>}
    <p className="muted small">Model selection, cloud providers, capability routing and token or budget visibility are not implemented.
      This inventory check sends no prompt and grants no execution or publication authorization.</p>
  </section>;
}
