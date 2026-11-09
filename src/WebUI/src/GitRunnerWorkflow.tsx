import { useState } from 'react';
import { api, isExpiredOperatorSession, type Task } from './api';
import { gitApi, type GitDecisionHistory, type GitPreview, type GitRejection, type GitRepository, type GitRun } from './gitApi';
import { useOperatorSession } from './useOperatorSession';

export function GitRunnerWorkflow({ initialTaskId = null }: { initialTaskId?: string | null }) {
  const [bootstrap, setBootstrap] = useState('');
  const [token, setToken] = useState('');
  const [tasks, setTasks] = useState<Task[]>([]);
  const [repositories, setRepositories] = useState<GitRepository[]>([]);
  const [taskId, setTaskId] = useState('');
  const [repoId, setRepoId] = useState('');
  const [content, setContent] = useState('Approved HIC test edit.\n');
  const [preview, setPreview] = useState<GitPreview | null>(null);
  const [rejection, setRejection] = useState<GitRejection | null>(null);
  const [history, setHistory] = useState<GitDecisionHistory | null>(null);
  const [run, setRun] = useState<GitRun | null>(null);
  const [runId, setRunId] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const repository = repositories.find(r => r.id === repoId);
  const operator = useOperatorSession(async (accessToken, active) => {
    const [repos, drafts] = await Promise.all([gitApi.repositories(accessToken), api.tasks()]);
    if (!active()) return;
    const available = drafts.filter(t => t.status === 'Draft');
    setToken(accessToken); setRepositories(repos); setTasks(available);
    setTaskId(initialTaskId && available.some(t => t.id === initialTaskId) ? initialTaskId : '');
  }, () => {
    setToken(''); setBootstrap(''); setRepositories([]); setTasks([]); invalidate();
    setTaskId(''); setRepoId(''); setContent('Approved HIC test edit.\n');
    setError(null);
  }, reason => setError(reason instanceof Error ? reason.message : 'Operator session restore failed.'));

  async function perform(action: () => Promise<void>) {
    setBusy(true); setError(null);
    try { await action(); }
    catch (reason) {
      if (!isExpiredOperatorSession(reason))
        setError(reason instanceof Error ? reason.message : 'Local Git operation failed.');
    }
    finally { setBusy(false); }
  }
  function invalidate() { setPreview(null); setRejection(null); setHistory(null); setRun(null); setRunId(''); }

  return <section className="local-runner" aria-label="Isolated Git workflow">
    <section className="panel">
      <p className="eyebrow">HUMAN-CONTROLLED · DISPOSABLE REPOSITORY ONLY</p>
      <h2>Approve an isolated Git edit</h2>
      <p className="muted">This interface does not offer repository execution. The updated Control Plane denies it
        until an OS-isolated backend is verified; an older running Control Plane may not enforce this restriction.
        Existing approvals and historical results remain visible.</p>
      {!token ? <div className="command-form">
        <label htmlFor="git-bootstrap">Local operator bootstrap token</label>
        <input id="git-bootstrap" className="command-title" type="password" autoComplete="off" value={bootstrap}
          disabled={busy || operator.restoring} onChange={e => setBootstrap(e.target.value)} />
        <button className="primary" disabled={busy || operator.restoring || !bootstrap} onClick={() => void perform(async () => {
          const session = await api.createLocalRunnerSession(bootstrap);
          const repos = await gitApi.repositories(session.accessToken);
          const drafts = (await api.tasks()).filter(t => t.status === 'Draft');
          setToken(session.accessToken); setBootstrap(''); setRepositories(repos); setTasks(drafts);
          setTaskId(initialTaskId && drafts.some(t => t.id === initialTaskId) ? initialTaskId : '');
          operator.authenticated(session);
        })}>Authenticate Git operator</button>
      </div> : <div className="notice">Authenticated operator session · HttpOnly cookie · 8-hour absolute expiry
        <button className="text-button" disabled={busy} onClick={() => void perform(() =>
          operator.logout(token))}>End Git session</button></div>}
      {operator.restoring && <p role="status">Restoring operator session…</p>}
      {operator.notice && <p className="notice" role="status">{operator.notice}</p>}
      {error && <p className="notice error" role="alert">{error} Refresh the durable run status; never retry execution blindly.</p>}
      {busy && <p role="status">Checking local operation…</p>}
    </section>
    {token && <section className="panel command-form">
      <h2>Repository, task and bounded scope</h2>
      {initialTaskId && !tasks.some(task => task.id === initialTaskId) &&
        <p className="notice error" role="alert">The selected task is no longer an available Draft. Refresh the task list before preparing a plan.</p>}
      <label htmlFor="git-task">Draft task</label>
      <select id="git-task" className="command-title" value={taskId} disabled={busy} onChange={e => { invalidate(); setTaskId(e.target.value); }}>
        <option value="">Select a Draft task</option>
        {tasks.map(t => <option key={t.id} value={t.id}>{t.title}</option>)}
      </select>
      <label htmlFor="git-repository">Authorized disposable repository</label>
      <select id="git-repository" className="command-title" value={repoId} disabled={busy || !!run} onChange={e => { invalidate(); setRepoId(e.target.value); }}>
        <option value="">Select a verified repository</option>
        {repositories.map(r => <option key={r.id} value={r.id}>{r.id}</option>)}
      </select>
      {repository && <p className="mono">{repository.path}<br />Base commit: {repository.baseCommit}</p>}
      <label htmlFor="git-content">message.txt replacement (printable ASCII, newline terminated, max 512 characters)</label>
      <textarea id="git-content" className="command-input" value={content} maxLength={512} disabled={busy || !!run}
        onChange={e => { invalidate(); setContent(e.target.value); }} />
      <button className="secondary" disabled={busy || !!run || !taskId || !repository || !content}
        onClick={() => void perform(async () => {
          if (!repository) return;
          invalidate(); setPreview(await gitApi.preview(taskId, repository, content, token));
        })}>Preview exact Git plan</button>
      {preview && <div className="detail-block">
        <h3>Plan revision {preview.plan.revision}</h3>
        <p>Actor: {preview.plan.actorId}<br />Expires: {preview.plan.expiresAtUtc}</p>
        <p className="mono">SHA-256: {preview.intentDigestSha256}</p>
        <p>Scope: message.txt only. Repository execution is unavailable until an OS-isolated backend is verified.
          Approval reserves this task but does not execute it. Interrupted runs require manual review, never automatic replay.</p>
        <pre className="preserve">{preview.plan.content}</pre>
        <button className="primary" disabled={busy || !!run || !!rejection || Date.parse(preview.plan.expiresAtUtc) <= Date.now()}
          onClick={() => void perform(async () => {
            const approved = await gitApi.approve(taskId, preview, token);
            setRunId(approved.runId); setRun(await gitApi.run(approved.runId, token));
          })}>Approve exact Git plan</button>
        <button className="secondary" disabled={busy || !!run || !!rejection || Date.parse(preview.plan.expiresAtUtc) <= Date.now()}
          onClick={() => void perform(async () => {
            const result = await gitApi.reject(taskId, preview, token);
            if (result.taskId !== taskId || result.actorId !== preview.plan.actorId ||
                result.intentDigestSha256 !== preview.intentDigestSha256)
              throw new Error('Rejection response did not match the reviewed plan. Check durable decision history.');
            setRejection(result);
          })}>Reject exact Git plan</button>
        {rejection && <p className="notice" role="status">Rejected · {rejection.decidedAtUtc} ·
          Decision {rejection.decisionId}. This exact plan cannot be approved; a new preview requires a new decision.</p>}
      </div>}
      <button className="secondary" disabled={busy || !taskId} onClick={() => void perform(async () => {
        const result = await gitApi.decisions(taskId, token);
        if (result.decisions.some(item => item.taskId !== taskId))
          throw new Error('Decision history did not match the selected task.');
        setHistory(result);
      })}>Load Git decision history</button>
      {history && <div className="detail-block">
        <h3>Git decision history</h3>
        {!history.decisions.length && <p>No recorded Git plan decisions for this operator.</p>}
        {history.decisions.map(item => <article key={item.decisionId}>
          <p><strong>{item.decision}</strong> · {item.decidedAtUtc} · actor {item.actorId}</p>
          <p className="mono">Decision: {item.decisionId}<br />SHA-256: {item.intentDigestSha256}<br />
            Repository: {item.plan.repositoryId} · {item.plan.repositoryPath}<br />
            Base: {item.plan.baseCommit}<br />File: {item.plan.file}<br />
            Expires: {item.plan.expiresAtUtc}{item.runId && <><br />Run: {item.runId}</>}</p>
          <pre className="preserve">{item.plan.content}</pre>
        </article>)}
        {history.hasMore && <p>Showing the {history.limit} most recent decisions; older decisions are not shown.</p>}
        <p>Decisions recorded before this feature remain in their original run audit.</p>
      </div>}
      <label htmlFor="git-run-id">Durable run ID (also usable after restart)</label>
      <input id="git-run-id" className="mono" value={runId} disabled={busy} onChange={e => {
        setRunId(e.target.value); setRun(null);
      }} />
      <button className="secondary" disabled={busy || !/^[0-9a-f-]{36}$/.test(runId)}
        onClick={() => void perform(async () => { setRun(await gitApi.run(runId, token)); })}>Refresh Git run status</button>
      {run && <div className="detail-block">
        <h3>Run status: {run.status}</h3>
        <p>Task authority: Draft (reserved for this local run). Execution outcome is separate, not a Worker task completion.
          One immutable decision per task; use a new Draft task for a different plan.</p>
        <p><strong>Executor availability:</strong> This UI does not offer repository execution. The updated Control Plane
          denies it until a verified isolation backend is available; verify the API revision before relying on this restriction.
          The run status is a durable record, not evidence that an executor is available.</p>
        <p className="mono">Run: {run.runId}<br />Repository: {run.plan.repositoryPath}<br />
          Base: {run.plan.baseCommit}<br />Approved hash: {run.intentDigestSha256}</p>
        {run.status === 'queued' && <p className="notice">Repository execution is disabled until a verified isolation backend is available. This approval has not been executed.</p>}
        {run.status === 'running' && <p className="notice">Claim is durable. After a crash this run stays running/unknown;
          it cannot be replayed. Inspect its retained worktree before any future recovery decision.</p>}
        {run.result && <>
          <h3>Test result: {run.result.testCode}</h3>
          <p>Source unchanged: {String(run.result.sourceUnchanged)}</p>
          <p className="mono">Worktree: {run.result.worktreePath}<br />Branch: {run.result.branch}</p>
          <h3>Actual Git diff</h3><pre className="preserve">{run.result.diff || 'No diff released for a denied run.'}</pre>
        </>}
        <h3>Durable audit</h3><ol>{run.events.map((e, i) => <li key={i}>{e.eventCode} · {e.recordedAtUtc}</li>)}</ol>
      </div>}
    </section>}
  </section>;
}
