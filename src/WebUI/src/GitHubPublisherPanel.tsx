import { useEffect, useState } from 'react';
import { githubApi, type GitHubAppStatus, type GitHubPublication } from './githubApi';

export function GitHubPublisherPanel({ token, runId, saved, onChanged }: {
  token: string; runId?: string; saved?: GitHubPublication[]; onChanged?: () => Promise<void>;
}) {
  const [status, setStatus] = useState<GitHubAppStatus | null>(null);
  const [current, setCurrent] = useState<GitHubPublication | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [now, setNow] = useState(Date.now());
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, []);
  useEffect(() => {
    let active = true;
    void githubApi.status(token).then(async value => {
      if (active) setStatus(value);
      if (value.configured && !runId) {
        const history = await githubApi.bootstraps(token);
        if (active) setCurrent(history.find(row => row.state !== 'preview') ?? history[0] ?? null);
      }
    })
      .catch(reason => { if (active) setError(reason instanceof Error ? reason.message : 'GitHub status unavailable.'); });
    return () => { active = false; };
  }, [token, runId]);
  useEffect(() => {
    if (saved) {
      const latest = [...saved].reverse();
      setCurrent(latest.find(value => value.runId === runId && value.state !== 'preview') ??
        latest.find(value => value.runId === runId) ?? null);
    }
  }, [saved, runId]);
  async function perform(action: () => Promise<GitHubPublication>) {
    setBusy(true); setError('');
    try { setCurrent(await action()); await onChanged?.(); }
    catch (reason) {
      setError(reason instanceof Error ? reason.message : 'GitHub publication interrupted; reconcile before retry.');
    } finally { setBusy(false); }
  }
  return <section className="detail-block" aria-label={runId ? 'GitHub task publisher' : 'GitHub App setup'}>
    <h3>{runId ? 'GitHub task publication — separate write approval' : 'GitHub App / empty repository bootstrap'}</h3>
    {error && <p className="notice error" role="alert">{error} No success is assumed; read/reconcile the saved publication.</p>}
    {status && !status.configured && <p className="notice">
      GitHub App configuration is missing: {status.missing.join(', ')}.
      Configure the backend only; never paste private keys or GitHub tokens into this conversation.
      Repository-scoped Metadata read, Contents write and Pull requests write are required.
    </p>}
    {status?.configured && <p className="mono">Authorized destination: https://github.com/{status.repository}</p>}
    {status?.configured && <p className="mono">GitHub App: {status.appId ?? 'not reported'}<br />
      Installation: {status.installationId ?? 'not reported'}<br />
      Running revision: {status.runtimeRevision || 'not reported'}</p>}
    {status?.configured && !current && <button disabled={busy} onClick={() =>
      void perform(() => runId ? githubApi.preview(runId, token) : githubApi.bootstrapPreview(token))}>
      {runId ? 'Preview GitHub task publication' : 'Preview separately approved empty-repository bootstrap'}
    </button>}
    {current && <>
      <p role="status">GitHub publication: {current.state}{current.errorCode ? ` · ${current.errorCode}` : ''}</p>
      <p className="mono">Publication: {current.publicationId}<br />
        Repository: https://github.com/{current.plan.destination.owner}/{current.plan.destination.name}<br />
        Immutable repository ID: {current.plan.destination.id}<br />
        Base branch: {current.plan.destination.baseBranch}<br />Base SHA: {current.plan.baseCommit ?? 'EMPTY — bootstrap only'}<br />
        Task branch: {current.plan.taskBranch}<br />File: {current.plan.file}<br />
        Content SHA-256: {current.plan.contentDigest}<br />Approval SHA-256: {current.intentDigestSha256}<br />
        Expires: {current.plan.expiresAtUtc}</p>
      <pre className="preserve">{current.plan.content}</pre>
      {current.plan.kind === 'bootstrap' && <>
        <h4>Exact initial file diff</h4>
        <pre className="preserve">{`--- /dev/null\n+++ b/${current.plan.file}\n@@ -0,0 +1,${current.plan.content.split('\n').length - 1} @@\n` +
          current.plan.content.split('\n').slice(0, -1).map(line => `+${line}\n`).join('')}</pre>
      </>}
      <p className="notice">GitHub write scope: only this repository and exact approved file.
        Metadata read · Contents write · Pull requests write. No merge, deployment, deletion or force push.
        Credential scope SHA-256: {current.plan.credentialScopeDigest}</p>
      {current.plan.kind === 'bootstrap' && <p className="notice">
        This separate one-time approval creates the first base commit containing only src/Value.cs.
        Approval authorizes this first commit only; it is not approval for a task publication, merge or deployment. Import the pinned base into an isolated
        server-authorized checkout before starting a model conversation.
      </p>}
      {current.state === 'preview' && <button className="primary" disabled={busy ||
        Date.parse(current.plan.expiresAtUtc) <= now}
        onClick={() => void perform(() => githubApi.approve(current, token))}>
        {current.plan.kind === 'bootstrap' ? 'Approve exact bootstrap write' : 'Approve exact GitHub write scope'}
      </button>}
      {current.state === 'preview' && <button disabled={busy}
        onClick={() => void perform(() => githubApi.reject(current, token))}>
        Reject exact GitHub write
      </button>}
      {current.state === 'preview' && Date.parse(current.plan.expiresAtUtc) <= now &&
        <button disabled={busy} onClick={() => void perform(() =>
          runId ? githubApi.preview(runId, token) : githubApi.bootstrapPreview(token))}>Create fresh publication preview</button>}
      {current.state === 'pending' && <button className="primary" disabled={busy ||
        Date.parse(current.plan.expiresAtUtc) <= now}
        onClick={() => void perform(() => githubApi.execute(current.publicationId, token))}>
        Execute approved GitHub publication
      </button>}
      {current.state === 'pushed' && <button disabled={busy ||
        Date.parse(current.plan.expiresAtUtc) <= now}
        onClick={() => void perform(() => githubApi.completeDraft(current.publicationId, token))}>
        Reconcile and complete approved draft PR
      </button>}
      {current.state !== 'preview' && current.errorCode !== 'human-rejected' && <button disabled={busy}
        onClick={() => void perform(() => githubApi.reconcile(current.publicationId, token))}>
        Read-only remote reconciliation
      </button>}
      {current.errorCode === 'human-rejected' && <p className="notice">
        Human rejected this write. No GitHub write was executed and no approval was recorded.
      </p>}
      <p className="mono">Commit SHA: {current.commitSha ?? 'not verified'}<br />
        Remote branch SHA: {current.remoteSha ?? 'not verified'}</p>
      {current.prUrl && <p><a href={current.prUrl} target="_blank" rel="noreferrer">
        Draft PR #{current.prNumber}</a></p>}
      {current.state === 'unknown' && <p className="notice">
        External write outcome is unknown. No automatic write retry, force push, deletion, merge or deployment.
      </p>}
      <ol>{current.events.map((event, index) =>
        <li key={index}>{event.eventCode} · {event.recordedAtUtc}</li>)}</ol>
    </>}
    {busy && <p role="status">GitHub request in progress; intent is recorded before external writes.</p>}
  </section>;
}
