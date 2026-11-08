import { useEffect, useMemo, useState } from 'react';
import { GitHubPublisherPanel } from './GitHubPublisherPanel';
import {
  api,
  type LocalCodeModelInfo,
  type ModelConversation,
  type ModelConversationSummary,
  type ModelRepository,
} from './api';

const errorText = (reason: unknown) => reason instanceof Error ? reason.message : 'Unexpected conversation error.';
const stamp = (value: string) => new Date(value).toLocaleString();
const emptyModel: LocalCodeModelInfo = { configured: false, provider: 'disabled', model: '', timeoutSeconds: 60 };

export function ModelConversationWorkspace() {
  const [bootstrap, setBootstrap] = useState('');
  const [token, setToken] = useState('');
  const [model, setModel] = useState<LocalCodeModelInfo>(emptyModel);
  const [repositories, setRepositories] = useState<ModelRepository[]>([]);
  const [conversations, setConversations] = useState<ModelConversationSummary[]>([]);
  const [conversation, setConversation] = useState<ModelConversation | null>(null);
  const [selectedRepository, setSelectedRepository] = useState('');
  const [selectedFile, setSelectedFile] = useState('');
  const [title, setTitle] = useState('');
  const [input, setInput] = useState('');
  const [busy, setBusy] = useState(false);
  const [attemptedRunIds, setAttemptedRunIds] = useState<string[]>([]);
  const [error, setError] = useState<string | null>(null);
  const repository = repositories.find(item => item.id === selectedRepository);
  const authorizedFiles = useMemo(() => repository?.files.filter(file => file.endsWith('.cs') && file !== '.hic-disposable') ?? [],
    [repository]);

  useEffect(() => {
    if (!authorizedFiles.includes(selectedFile)) setSelectedFile(authorizedFiles[0] ?? '');
  }, [authorizedFiles, selectedFile]);

  async function perform(action: () => Promise<void>) {
    setBusy(true);
    setError(null);
    try { await action(); }
    catch (reason) { setError(errorText(reason)); }
    finally { setBusy(false); }
  }

  async function refreshConversation(id = conversation?.conversationId) {
    if (!id || !token) return;
    const updated = await api.modelConversation(id, token);
    setConversation(updated);
    setAttemptedRunIds(previous => previous.filter(runId =>
      updated.runs.some(run => run.runId === runId && run.status === 'queued')));
  }

  async function loadWorkspace(accessToken: string) {
    const [modelInfo, repos, saved] = await Promise.all([
      api.localCodeModel(accessToken),
      api.modelRepositories(accessToken),
      api.modelConversations(accessToken),
    ]);
    setToken(accessToken);
    setModel(modelInfo);
    setRepositories(repos);
    setConversations(saved);
    if (saved.length) {
      const current = await api.modelConversation(saved[0].conversationId, accessToken);
      setConversation(current);
    } else {
      setConversation(null);
    }
  }

  return <section className="local-runner" aria-label="HIC conversation workspace">
    <section className="panel">
      <p className="eyebrow">CONVERSATION · LOCAL MODEL · HUMAN-APPROVED EDIT</p>
      <h2>Project conversation</h2>
      <p className="muted">Real local model calls, durable conversation history and one explicitly approved C# file replacement
        in an isolated worktree. GitHub publication needs a separate repository-scoped approval.
        No shell, merge, deployment or paid-provider fallback.</p>
      {!token ? <div className="command-form">
        <label htmlFor="conversation-bootstrap">Local operator bootstrap token</label>
        <input id="conversation-bootstrap" className="command-title" type="password" autoComplete="off"
          value={bootstrap} disabled={busy} onChange={event => setBootstrap(event.target.value)} />
        <button className="primary" disabled={busy || !bootstrap} onClick={() => void perform(async () => {
          const session = await api.createLocalRunnerSession(bootstrap);
          setBootstrap('');
          await loadWorkspace(session.accessToken);
        })}>Open conversation workspace</button>
      </div> : <div className="notice">
        Operator session · memory only · 15 minutes · Model: {model.configured ? `${model.provider} / ${model.model}` : 'not configured'}
        <button className="text-button" disabled={busy} onClick={() => {
          setToken('');
          setModel(emptyModel);
          setRepositories([]);
          setConversations([]);
          setConversation(null);
          setAttemptedRunIds([]);
        }}>End session</button>
      </div>}
      {token && <p className={model.configured ? 'notice' : 'notice error'} role={model.configured ? 'status' : 'alert'}>
        {model.configured
          ? `Requests use only the configured local model; model timeout ${model.timeoutSeconds}s.`
          : 'Local model is unavailable or not configured. Messages are retained, but no model response or plan will be fabricated.'}
      </p>}
      {error && <p className="notice error" role="alert">{error} For an interrupted write, refresh this conversation before sending again.</p>}
      {busy && <p role="status">Working with the local Control Plane…</p>}
      {token && <GitHubPublisherPanel token={token} />}
    </section>

    {token && <div className="overview-grid">
      <section className="panel command-form">
        <div className="panel-heading"><div><p className="eyebrow">NEW CONVERSATION / IMPORT AUTHORIZED CHECKOUT</p>
          <h2>Start a conversation</h2></div></div>
        <p className="muted small">Start a project discussion against a server-authorized local checkout and configured C# file.
          Arbitrary URLs are not permitted. An empty GitHub destination requires separate bootstrap approval.</p>
        <label htmlFor="conversation-title">Conversation title</label>
        <input id="conversation-title" className="command-title" maxLength={120} value={title}
          disabled={busy} onChange={event => setTitle(event.target.value)} placeholder="Project or change request" />
        <label htmlFor="conversation-repository">Authorized repository</label>
        <select id="conversation-repository" className="command-title" value={selectedRepository} disabled={busy}
          onChange={event => { setSelectedRepository(event.target.value); setSelectedFile(''); }}>
          <option value="">Choose an authorized checkout</option>
          {repositories.map(item => <option key={item.id} value={item.id}>{item.id}</option>)}
        </select>
        {repository && <p className="mono">{repository.path}<br />Immutable base: {repository.baseCommit}</p>}
        <label htmlFor="conversation-file">One permitted C# source file</label>
        <select id="conversation-file" className="command-title" value={selectedFile} disabled={busy || !authorizedFiles.length}
          onChange={event => setSelectedFile(event.target.value)}>
          <option value="">Choose a configured C# file</option>
          {authorizedFiles.map(file => <option key={file} value={file}>{file}</option>)}
        </select>
        <button className="primary" disabled={busy || !title.trim() || !repository || !selectedFile}
          onClick={() => void perform(async () => {
            const created = await api.createModelConversation({
              title: title.trim(), repositoryId: repository!.id, file: selectedFile,
            }, token);
            setConversation(created);
            setConversations(previous => [{
              conversationId: created.conversationId, taskId: created.taskId, title: created.title,
              repositoryId: created.repositoryId, baseCommit: created.baseCommit, file: created.file,
              updatedAtUtc: created.updatedAtUtc,
            }, ...previous]);
            setTitle('');
          })}>Create conversation</button>
      </section>

      <section className="panel command-form">
        <div className="panel-heading"><div><p className="eyebrow">DURABLE HISTORY</p><h2>Conversations</h2></div></div>
        {!conversations.length && <p className="notice">No saved conversations yet.</p>}
        {conversations.map(item => <button key={item.conversationId}
          className={`task-link ${conversation?.conversationId === item.conversationId ? 'selected' : ''}`}
          disabled={busy} onClick={() => void perform(async () => {
            setConversation(await api.modelConversation(item.conversationId, token));
            setAttemptedRunIds([]);
          })}>
          {item.title}<span className="identifier">{item.repositoryId} · {item.file} · {stamp(item.updatedAtUtc)}</span>
        </button>)}
        {conversation && <div className="detail-block">
          <p className="eyebrow">CURRENT SCOPE</p>
          <p className="mono">{conversation.repositoryId} · {conversation.file}<br />Base: {conversation.baseCommit}<br />
            Task: {conversation.taskId}</p>
          <button className="secondary" disabled={busy} onClick={() => void perform(() => refreshConversation())}>Refresh history and run status</button>
        </div>}
      </section>
    </div>}

    {token && conversation && <section className="panel">
      <div className="panel-heading"><div><p className="eyebrow">MULTI-TURN PROJECT CONTEXT</p>
        <h2>{conversation.title}</h2></div><span className="outline-badge">{conversation.messages.length} MESSAGES</span></div>
      <div className="conversation-messages" aria-live="polite">
        {!conversation.messages.length && <p className="notice">Start with architecture questions, requirements or a bounded change request.</p>}
        {conversation.messages.map(item => <article className="detail-block" key={item.messageId}>
          <div className="panel-heading"><strong>{item.role === 'user' ? 'You' : `HIC · ${item.provider ?? 'local model'}`}</strong>
            <span className="muted small">{stamp(item.createdAtUtc)} · {item.status}</span></div>
          <p className="preserve">{item.content}</p>
          {item.proposal && <div className="proposal">
            <h3>Proposed code plan — revision {item.proposal.plan.revision}</h3>
            <p className="mono">File: {item.proposal.plan.file}<br />Repository: {item.proposal.plan.repositoryId}<br />
              Base commit: {item.proposal.plan.baseCommit}<br />Provider/model: {item.proposal.plan.provider} / {item.proposal.plan.model}<br />
              Expires: {stamp(item.proposal.plan.expiresAtUtc)}<br />Plan SHA-256: {item.proposal.intentDigestSha256}</p>
            <pre className="preserve">{item.proposal.plan.content}</pre>
            <p className="muted small">Review this exact replacement. Approval reserves the existing Draft task and local-run audit; it does not commit or publish.</p>
            <button className="primary" disabled={busy || Date.parse(item.proposal.plan.expiresAtUtc) <= Date.now() ||
              conversation.runs.some(run => run.taskId === item.proposal!.plan.taskId)}
              onClick={() => void perform(async () => {
                await api.approveModelCodeEdit(conversation.conversationId, item.messageId,
                  item.proposal!.intentDigestSha256, token);
                await refreshConversation();
              })}>Approve exact plan</button>
          </div>}
        </article>)}
      </div>
      <div className="command-form">
        <label htmlFor="conversation-message">Message the local model</label>
        <textarea id="conversation-message" className="command-input" maxLength={4000} value={input} disabled={busy}
          onChange={event => setInput(event.target.value)} placeholder="Discuss architecture or explicitly request one bounded C# change." />
        <button className="primary" disabled={busy || !input.trim()} onClick={() => void perform(async () => {
          const current = input;
          setInput('');
          try {
            const updated = await api.sendModelMessage(conversation.conversationId, crypto.randomUUID(), current, token);
            setConversation(updated);
            setConversations(previous => previous.map(item => item.conversationId === updated.conversationId
              ? { ...item, updatedAtUtc: updated.updatedAtUtc } : item));
          } catch (reason) {
            await refreshConversation();
            throw reason;
          }
        })}>Send message</button>
        <p className="muted small">Model output and repository text are untrusted. Only a validated proposal plus this separate human approval can start the bounded local edit.</p>
      </div>
    </section>}

    {token && conversation?.runs.map(run => <section className="panel" key={run.runId}>
      <div className="panel-heading"><div><p className="eyebrow">EXISTING CONTROL PLANE RUN</p>
        <h2>Code edit · {run.status}</h2></div><span className="outline-badge">{run.action}</span></div>
      <p className="mono">Run: {run.runId}<br />Task: {run.taskId}<br />Scope: {run.scope}<br />
        Approved digest: {run.intentDigestSha256}</p>
      {run.status === 'queued' && !attemptedRunIds.includes(run.runId) && <button className="primary" disabled={busy}
        onClick={() => void perform(async () => {
          setAttemptedRunIds(previous => [...previous, run.runId]);
          await api.executeLocalHealthRun(run.runId, token, 120000);
          await refreshConversation();
        })}>Execute approved isolated edit</button>}
      {run.status === 'running' && <p className="notice">Execution claim is durable. An interrupted run is not automatically replayed; refresh to reconcile its recorded status.</p>}
      {run.result && <div className="detail-block">
        <h3>Bounded validation: {run.result.testCode}</h3>
        <p>Source checkout unchanged: {String(run.result.sourceUnchanged)}</p>
        <p className="mono">File: {run.result.file}<br />Worktree: {run.result.worktreePath}<br />Branch: {run.result.branch}</p>
        <h3>Actual worktree diff</h3><pre className="preserve">{run.result.diff || 'No diff was released.'}</pre>
      </div>}
      {run.status === 'completed' && run.result?.sourceUnchanged &&
        run.result.testCode === 'passed:isolated-csharp-compile-and-scope' &&
        <GitHubPublisherPanel token={token} runId={run.runId}
          saved={conversation.publications} onChanged={() => refreshConversation()} />}
      <h3>Durable audit</h3><ol>{run.events.map((event, index) =>
        <li key={`${event.eventCode}-${index}`}>{event.eventCode} · {stamp(event.recordedAtUtc)}</li>)}</ol>
    </section>)}
  </section>;
}
