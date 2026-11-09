import { useEffect, useState } from 'react';
import { api, isExpiredOperatorSession, type GeneralConversation, type GeneralConversationSummary } from './api';
import { GeneralPlanReview } from './GeneralPlanReview';

export function GeneralConversationWorkspace({ token, onTasksCreated, onViewTask }: {
  token: string; onTasksCreated: () => void; onViewTask: (id: string) => void;
}) {
  const [saved, setSaved] = useState<GeneralConversationSummary[]>([]);
  const [current, setCurrent] = useState<GeneralConversation | null>(null);
  const [title, setTitle] = useState('');
  const [input, setInput] = useState('');
  const [busy, setBusy] = useState(false);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [unavailable, setUnavailable] = useState(false);
  const [retry, setRetry] = useState(0);

  useEffect(() => {
    let active = true;
    setLoading(true);
    setError(null);
    void api.generalConversations(token).then(items => {
      if (active) { setSaved(items); setUnavailable(false); }
    }).catch(reason => {
      if (active) { setUnavailable(true); setError(reason instanceof Error ? reason.message : 'General conversations are unavailable.'); }
    }).finally(() => { if (active) setLoading(false); });
    return () => { active = false; };
  }, [token, retry]);

  async function perform(action: () => Promise<void>) {
    setBusy(true);
    setError(null);
    try { await action(); }
    catch (reason) {
      if (!isExpiredOperatorSession(reason))
        setError(reason instanceof Error ? reason.message : 'General conversation request failed.');
    } finally { setBusy(false); }
  }

  return <section className="panel">
    <p className="eyebrow">GENERAL CONVERSATIONS · NO REPOSITORY REQUIRED</p>
    <h2>General conversation</h2>
    <p className="muted small">Discuss ideas without selecting a repository. The model has no tools;
      conversation does not authorize task execution or publication.</p>
    {loading && <p role="status">Loading general conversations…</p>}
    {error && <p role="alert" className="notice error">{error} {unavailable && <>
      If the general conversation schema is not installed, this feature requires an explicitly approved additive
      migration on the selected database.{' '}
      <button className="text-button" onClick={() => setRetry(n => n + 1)}>Retry</button></>}</p>}
    {!loading && !unavailable && <>
      <div className="command-form">
        <label htmlFor="general-title">General conversation title</label>
        <input id="general-title" className="command-title" maxLength={120} value={title}
          disabled={busy} onChange={event => setTitle(event.target.value)} />
        <button className="secondary" disabled={busy || !title.trim()} onClick={() => void perform(async () => {
          const created = await api.createGeneralConversation(title.trim(), token);
          setCurrent(created);
          setSaved(items => [created, ...items]);
          setTitle('');
        })}>Create general conversation</button>
      </div>
      {!saved.length && <p className="notice">No general conversations yet.</p>}
      {saved.map(item => <button key={item.conversationId} className="task-link" disabled={busy}
        onClick={() => void perform(async () => setCurrent(await api.generalConversation(item.conversationId, token)))}>
        {item.title}<span className="identifier">{new Date(item.updatedAtUtc).toLocaleString()}</span>
      </button>)}
      {current && <div>
        <h3>{current.title}</h3>
        <div aria-live="polite">
          {!current.messages.length && <p className="notice">No messages yet.</p>}
          {current.messages.map(item => <article className="detail-block" key={item.messageId}>
            <strong>{item.role === 'user' ? 'You' : `HIC · ${item.provider ?? 'local model'}`}</strong>
            <span className="muted small"> · {item.status}</span><p className="preserve">{item.content}</p>
          </article>)}
        </div>
        <div className="command-form">
          <label htmlFor="general-message">Message general conversation</label>
          <textarea id="general-message" className="command-input" maxLength={4000} value={input} disabled={busy}
            onChange={event => setInput(event.target.value)} />
          <button className="primary" disabled={busy || !input.trim()} onClick={() => void perform(async () => {
            const text = input;
            setInput('');
            try {
              const updated = await api.sendGeneralMessage(current.conversationId, crypto.randomUUID(), text, token);
              setCurrent(updated);
              setSaved(items => items.map(item => item.conversationId === updated.conversationId ? updated : item));
            } catch (reason) {
              setCurrent(await api.generalConversation(current.conversationId, token));
              throw reason;
            }
          })}>Send general message</button>
        </div>
        <GeneralPlanReview key={current.conversationId} conversationId={current.conversationId} token={token}
          hasMessages={current.messages.some(message => message.role === 'user')}
          onTasksCreated={onTasksCreated} onViewTask={onViewTask} />
      </div>}
    </>}
    {busy && <p role="status">Working with the local Control Plane…</p>}
  </section>;
}
