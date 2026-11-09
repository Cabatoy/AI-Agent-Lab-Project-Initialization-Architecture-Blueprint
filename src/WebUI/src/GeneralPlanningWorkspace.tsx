import { useState } from 'react';
import { api, type GeneralConversation, type GeneralConversationSummary } from './api';
import { GeneralPlanReview } from './GeneralPlanReview';
import { useOperatorSession } from './useOperatorSession';
import { GitHubIssuePreview } from './GitHubIssuePreview';

export function GeneralPlanningWorkspace({ openConversations, onTasksCreated, onViewTask }: {
  openConversations: () => void; onTasksCreated: () => void; onViewTask: (id: string) => void;
}) {
  const [token, setToken] = useState('');
  const [saved, setSaved] = useState<GeneralConversationSummary[]>([]);
  const [conversation, setConversation] = useState<GeneralConversation | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  function clear() { setToken(''); setSaved([]); setConversation(null); }
  const session = useOperatorSession(async (accessToken, active) => {
    const conversations = await api.generalConversations(accessToken);
    if (!active()) return;
    setToken(accessToken); setSaved(conversations); setError(null);
  }, clear, reason => setError(reason instanceof Error ? reason.message : 'Planning is unavailable.'));

  async function select(id: string) {
    setConversation(null); setError(null);
    if (!id) return;
    setBusy(true);
    try { setConversation(await api.generalConversation(id, token)); }
    catch (reason) { setError(reason instanceof Error ? reason.message : 'Conversation is unavailable.'); }
    finally { setBusy(false); }
  }
  return <section className="panel">
    <p className="eyebrow">MODEL PLAN REVIEW</p>
    <h2>Conversation plans</h2>
    {session.restoring && <p role="status">Restoring operator session…</p>}
    {session.notice && <p role="status" className="notice">{session.notice}</p>}
    {error && <p role="alert" className="notice error">{error}</p>}
    {!token && !session.restoring && <p className="notice">Authenticate and create a general conversation first.{' '}
      <button className="text-button" onClick={openConversations}>Open Conversations</button></p>}
    {token && <>
      <GitHubIssuePreview key={token} token={token} />
      {!saved.length && <p className="notice">No saved general conversations.{' '}
        <button className="text-button" onClick={openConversations}>Open Conversations</button></p>}
      {!!saved.length && <div className="command-form">
        <label htmlFor="planning-conversation">Saved general conversation</label>
        <select id="planning-conversation" className="command-title" disabled={busy}
          value={conversation?.conversationId ?? ''} onChange={event => void select(event.target.value)}>
          <option value="">Select a conversation</option>
          {saved.map(item => <option key={item.conversationId} value={item.conversationId}>{item.title}</option>)}
        </select>
      </div>}
      {busy && <p role="status">Loading conversation…</p>}
      {conversation && <GeneralPlanReview key={conversation.conversationId} conversationId={conversation.conversationId}
        token={token} hasMessages={conversation.messages.some(message => message.role === 'user')}
        onTasksCreated={onTasksCreated} onViewTask={onViewTask} />}
    </>}
  </section>;
}
