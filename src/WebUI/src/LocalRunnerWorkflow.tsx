import { useState } from 'react';
import { api, type LocalHealthIntent, type LocalHealthRunSnapshot, type LocalRunnerCapability, type Task } from './api';

const delay = (milliseconds: number) => new Promise(resolve => setTimeout(resolve, milliseconds));
const errorMessage = (reason: unknown) => reason instanceof Error ? reason.message : 'Unexpected local runner error.';

export function LocalRunnerWorkflow() {
  const [bootstrapToken, setBootstrapToken] = useState('');
  const [sessionToken, setSessionToken] = useState('');
  const [capability, setCapability] = useState<LocalRunnerCapability | null>(null);
  const [tasks, setTasks] = useState<Task[]>([]);
  const [taskId, setTaskId] = useState('');
  const [intent, setIntent] = useState<LocalHealthIntent | null>(null);
  const [run, setRun] = useState<LocalHealthRunSnapshot | null>(null);
  const [connecting, setConnecting] = useState(false);
  const [loadingTasks, setLoadingTasks] = useState(false);
  const [loadingIntent, setLoadingIntent] = useState(false);
  const [approving, setApproving] = useState(false);
  const [executing, setExecuting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  async function connect() {
    setConnecting(true);
    setError(null);
    setNotice(null);
    try {
      const session = await api.createLocalRunnerSession(bootstrapToken);
      const registered = await api.localRunnerCapabilities(session.accessToken);
      setSessionToken(session.accessToken);
      setCapability(registered);
      setBootstrapToken('');
      setTaskId('');
      setIntent(null);
      setRun(null);
      setTasks((await api.tasks()).filter(task => task.status === 'Draft'));
    } catch (reason) {
      setError(errorMessage(reason));
    } finally {
      setConnecting(false);
    }
  }

  async function refreshTasks() {
    if (!sessionToken) return;
    setLoadingTasks(true);
    setError(null);
    try {
      setTasks((await api.tasks()).filter(task => task.status === 'Draft'));
    } catch (reason) {
      setError(errorMessage(reason));
    } finally {
      setLoadingTasks(false);
    }
  }

  async function selectTask(id: string) {
    setTaskId(id);
    setIntent(null);
    setRun(null);
    setError(null);
    if (!id || !sessionToken) return;
    setLoadingIntent(true);
    try {
      setIntent(await api.localHealthIntent(id, sessionToken));
    } catch (reason) {
      setError(errorMessage(reason));
    } finally {
      setLoadingIntent(false);
    }
  }

  async function runApproved(runId: string) {
    if (!sessionToken) return;
    setExecuting(true);
    setError(null);
    setNotice('The approved local health check is being claimed. Do not retry if the connection is interrupted.');
    const execution = api.executeLocalHealthRun(runId, sessionToken);
    const executionOutcome = execution.then(() => null, reason => reason);
    try {
      for (let attempt = 0; attempt < 30; attempt++) {
        const snapshot = await api.localHealthRun(runId, sessionToken);
        setRun(snapshot);
        if (snapshot.status === 'completed' || snapshot.status === 'expired' || snapshot.status === 'invalidated') break;
        await delay(250);
      }
      const executionError = await executionOutcome;
      if (executionError) throw executionError;
      const final = await api.localHealthRun(runId, sessionToken);
      setRun(final);
      if (final.status === 'running' || final.status === 'queued')
        setNotice('The durable run is still in progress. Refresh status to check again; do not execute it a second time.');
      else if (final.status === 'completed')
        setNotice('The read-only system health result was durably recorded.');
      else if (final.status === 'invalidated')
        setNotice('The task changed after approval. This run was not executed.');
      else
        setNotice('This approval expired before it could be claimed. It cannot be replayed.');
    } catch (reason) {
      setError(errorMessage(reason));
      try { setRun(await api.localHealthRun(runId, sessionToken)); } catch { /* The original error remains visible. */ }
    } finally {
      setExecuting(false);
    }
  }

  async function approve() {
    if (!intent || !taskId || !sessionToken || approving) return;
    setApproving(true);
    setError(null);
    setNotice(null);
    try {
      const approved = await api.approveLocalHealthRun(taskId, intent.intentDigestSha256, sessionToken);
      const snapshot = await api.localHealthRun(approved.runId, sessionToken);
      setRun(snapshot);
      setApproving(false);
      if (snapshot.status === 'queued') await runApproved(snapshot.runId);
      else if (snapshot.status === 'completed') setNotice('This task already has a completed run; no new execution was started.');
      else if (snapshot.status === 'expired') setNotice('This task approval has expired and cannot be replayed.');
      else if (snapshot.status === 'invalidated') setNotice('The task changed after approval. This run cannot be executed.');
      else setNotice('This exact task intent is already approved and running.');
    } catch (reason) {
      setError(errorMessage(reason));
    } finally {
      setApproving(false);
    }
  }

  return <section className="local-runner" aria-labelledby="local-runner-title">
    <section className="panel">
      <div className="panel-heading">
        <div><p className="eyebrow">LOCAL RUNNER · PHASE 1</p><h2 id="local-runner-title">Run a read-only local task</h2></div>
        <span className="outline-badge">NO WORKER · NO MODEL · NO SHELL</span>
      </div>
      <p className="muted">Authenticate the configured local operator, select a Draft task, review its exact action and scope, and explicitly approve one read-only system health check. The Control Plane records the consent and result; this flow never queues the Agent Worker.</p>

      {!sessionToken
        ? <div className="command-form">
          <label className="field-label" htmlFor="runner-bootstrap-token">Local operator bootstrap token</label>
          <input id="runner-bootstrap-token" className="command-title" type="password" autoComplete="off"
            value={bootstrapToken} onChange={event => setBootstrapToken(event.target.value)}
            placeholder="Paste the configured local registration token" />
          <div className="form-actions">
            <button type="button" className="primary" disabled={connecting || !bootstrapToken}
              onClick={() => void connect()}>{connecting ? 'Authenticating…' : 'Authenticate local operator'}</button>
            <span className="muted small">Token is not stored in browser storage.</span>
          </div>
        </div>
        : <div className="detail-block" role="status">
          <span className="label">Authenticated local actor</span>
          <p>{capability?.actorId ?? 'Loading…'} · session expires after 15 minutes</p>
          <button className="text-button" type="button" onClick={() => {
            setSessionToken('');
            setCapability(null);
            setTasks([]);
            setTaskId('');
            setIntent(null);
            setRun(null);
            setNotice(null);
            setError(null);
          }}>End local session</button>
        </div>}

      {error && <p className="notice error" role="alert">{error}</p>}
      {notice && <p className="notice" role="status">{notice}</p>}
    </section>

    {sessionToken && capability && <section className="panel">
      <div className="panel-heading">
        <div><p className="eyebrow">TASK AND CONSENT</p><h2>Review before approval</h2></div>
        <button className="secondary" type="button" disabled={loadingTasks} onClick={() => void refreshTasks()}>
          {loadingTasks ? 'Refreshing…' : 'Refresh Draft tasks'}
        </button>
      </div>
      <label className="field-label" htmlFor="local-runner-task">Draft task</label>
      <select id="local-runner-task" className="command-title" value={taskId}
        onChange={event => void selectTask(event.target.value)}>
        <option value="">Select a Draft task</option>
        {tasks.map(task => <option key={task.id} value={task.id}>{task.title}</option>)}
      </select>
      {!tasks.length && <p className="muted small">No Draft tasks are available. Create one in Command Center, then refresh this list.</p>}

      {loadingIntent && <p className="notice" role="status">Reading the exact task version from the Control Plane…</p>}
      {intent && <div className="intent-preview" aria-label="Exact action and scope">
        <div className="detail-block"><span className="label">Task</span><p>{intent.taskTitle}</p></div>
        <div className="detail-block"><span className="label">Exact action</span><code>{intent.action}</code></div>
        <div className="detail-block"><span className="label">Scope</span><code>{intent.scope}</code></div>
        <div className="detail-block"><span className="label">Task version</span><code>{intent.taskUpdatedAtUtc}</code></div>
        <div className="detail-block"><span className="label">Consent hash (SHA-256)</span><code className="plan-hash">{intent.intentDigestSha256}</code></div>
        <p className="muted small">Approval is bound to this task version, action, scope and hash. Any task change invalidates this exact intent.</p>
        <button className="primary" type="button" disabled={approving || executing || Boolean(run)}
          onClick={() => void approve()}>{approving ? 'Recording approval…' : 'Approve and run system.health'}</button>
      </div>}
    </section>}

    {run && <section className="panel" aria-labelledby="local-run-result-title">
      <div className="panel-heading">
        <div><p className="eyebrow">DURABLE LOCAL RUN</p><h2 id="local-run-result-title">Run status: {run.status}</h2></div>
        <span className="outline-badge">ONE-SHOT · REPLAY BLOCKED</span>
      </div>
      <p className="muted small">Run {run.runId} · {run.action} · {run.scope}</p>
      <div className="detail-block"><span className="label">Audit events</span>
        {run.events.length
          ? <ol className="activity-list">{run.events.map((event, index) =>
            <li key={`${event.eventCode}-${event.recordedAtUtc}-${index}`}><strong>{event.eventCode}</strong> · {new Date(event.recordedAtUtc).toLocaleString()}</li>)}</ol>
          : <p>No durable events reported.</p>}
      </div>
      {run.result && <div className="detail-block">
        <span className="label">Sanitized system health result</span>
        <dl className="health-result">
          <div><dt>Control Plane</dt><dd>{run.result.controlPlane}</dd></div>
          <div><dt>PostgreSQL</dt><dd>{run.result.postgres}</dd></div>
          <div><dt>Redis</dt><dd>{run.result.redis}</dd></div>
          <div><dt>Agent Worker</dt><dd>{run.result.worker}</dd></div>
        </dl>
        <p className="muted small">No raw exception, connection string, prompt, credential or arbitrary process output is stored.</p>
      </div>}
      {run.status === 'queued' && <p className="notice" role="status">Approved and queued; waiting for the local runner claim.</p>}
      {run.status === 'running' && <p className="notice" role="status">The claimed read-only check is running. Do not retry.</p>}
      {run.status === 'expired' && <p className="notice warning" role="status">Approval expired before claim; the run was not executed.</p>}
      {run.status === 'invalidated' && <p className="notice warning" role="status">The task changed after approval; the run was not executed.</p>}
      {(run.status === 'queued' || run.status === 'running') && sessionToken && <button className="secondary" type="button" disabled={executing}
        onClick={() => void api.localHealthRun(run.runId, sessionToken).then(setRun, reason => setError(errorMessage(reason)))}>
        Refresh run status
      </button>}
    </section>}
  </section>;
}
