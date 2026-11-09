import { useState } from 'react';
import { localRunnerRequest } from './api';

interface IssuePreview {
  repositoryId: number;
  issueId: number;
  reference: { owner: string; repository: string; number: number };
  title: string;
  body: string;
  updatedAtUtc: string;
  revisionSha256: string;
}
interface ImportPreview { previewId: string; title: string; description: string; reference: string; revisionSha256: string; expiresAtUtc: string }
interface ImportResult { taskId: string; replayed: boolean }
const object = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v);
const guid = (v: unknown): v is string => typeof v === 'string' && /^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/i.test(v);
const importPreview = (v: unknown): v is ImportPreview => object(v) && guid(v.previewId) &&
  typeof v.title === 'string' && typeof v.description === 'string' && typeof v.reference === 'string' &&
  typeof v.revisionSha256 === 'string' && /^[a-f0-9]{64}$/.test(v.revisionSha256) &&
  typeof v.expiresAtUtc === 'string' && !Number.isNaN(Date.parse(v.expiresAtUtc));
const importResult = (v: unknown): v is ImportResult => object(v) && guid(v.taskId) && typeof v.replayed === 'boolean';

function isIssuePreview(value: unknown): value is IssuePreview {
  if (!value || typeof value !== 'object') return false;
  const v = value as Record<string, unknown>;
  if (!v.reference || typeof v.reference !== 'object') return false;
  const r = v.reference as Record<string, unknown>;
  return Number.isSafeInteger(v.repositoryId) && Number(v.repositoryId) > 0 &&
    Number.isSafeInteger(v.issueId) && Number(v.issueId) > 0 &&
    typeof r.owner === 'string' && typeof r.repository === 'string' &&
    Number.isSafeInteger(r.number) && Number(r.number) > 0 &&
    typeof v.title === 'string' && typeof v.body === 'string' &&
    typeof v.updatedAtUtc === 'string' && !Number.isNaN(Date.parse(v.updatedAtUtc)) &&
    typeof v.revisionSha256 === 'string' && /^[a-f0-9]{64}$/.test(v.revisionSha256);
}

export function GitHubIssuePreview({ token, onTasksCreated, onViewTask }: {
  token: string; onTasksCreated: () => void; onViewTask: (id: string) => void;
}) {
  const [reference, setReference] = useState('');
  const [preview, setPreview] = useState<IssuePreview | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [title, setTitle] = useState('');
  const [description, setDescription] = useState('');
  const [prepared, setPrepared] = useState<ImportPreview | null>(null);
  const [result, setResult] = useState<ImportResult | null>(null);

  async function load() {
    setPreview(null); setPrepared(null); setResult(null); setError(null); setBusy(true);
    try {
      const source = await localRunnerRequest('/github/issues/preview', isIssuePreview, token, 'POST', { reference }, 200, 120000);
      setPreview(source); setTitle(source.title.slice(0, 120)); setDescription('');
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Issue preview unavailable.');
    } finally { setBusy(false); }
  }
  async function prepare() {
    if (!preview) return;
    setError(null); setPrepared(null); setResult(null); setBusy(true);
    try {
      const value = await localRunnerRequest('/github/issues/import-preview', importPreview, token, 'POST',
        { reference, revisionSha256: preview.revisionSha256, title, description }, 200, 120000);
      if (value.revisionSha256 !== preview.revisionSha256) throw new Error('Issue revision changed; preview again.');
      setPrepared(value);
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'Draft review unavailable.'); }
    finally { setBusy(false); }
  }
  async function confirm() {
    if (!prepared) return;
    setError(null); setBusy(true);
    try {
      const value = await localRunnerRequest(`/github/issues/import-previews/${prepared.previewId}/confirm`,
        importResult, token, 'POST', undefined, 200, 120000);
      setResult(value); onTasksCreated();
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'Draft import unavailable.'); }
    finally { setBusy(false); }
  }
  return <section aria-label="GitHub issue preview">
    <h3>GitHub issue preview</h3>
    <p>Read-only preview for the configured authorized public repository. Private issues are not enabled.</p>
    <label htmlFor="github-issue-reference">Issue URL or owner/repository/#number</label>
    <input id="github-issue-reference" value={reference} maxLength={512} disabled={busy}
      onChange={event => { setReference(event.target.value); setPreview(null); setPrepared(null); setResult(null); setError(null); }} />
    <button type="button" disabled={busy || !reference.trim()} onClick={() => void load()}>Preview issue</button>
    {busy && <p role="status">Loading issue…</p>}
    {error && <p role="alert">{error}</p>}
    {preview && <div>
      <p className="notice">Untrusted issue content. Preview is not an approval and creates no task, execution, or publication.</p>
      <h4>{preview.title}</h4>
      <p>Source: https://github.com/{preview.reference.owner}/{preview.reference.repository}/issues/{preview.reference.number}</p>
      <p>Updated: {preview.updatedAtUtc}</p>
      <p>Revision SHA-256: {preview.revisionSha256}</p>
      <pre style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{preview.body}</pre>
      <label htmlFor="issue-task-title">Draft task title</label>
      <input id="issue-task-title" maxLength={120} value={title} disabled={busy}
        onChange={event => { setTitle(event.target.value); setPrepared(null); setResult(null); }} />
      <label htmlFor="issue-task-description">Draft task description</label>
      <textarea id="issue-task-description" maxLength={4000} value={description} disabled={busy}
        onChange={event => { setDescription(event.target.value); setPrepared(null); setResult(null); }} />
      <button type="button" disabled={busy || !title.trim() || !description.trim()} onClick={() => void prepare()}>Review Draft import</button>
      {prepared && !result && <div>
        <h4>Review exact Draft</h4>
        <p>Repository and issue: {prepared.reference}</p>
        <p>Revision: {prepared.revisionSha256}</p>
        <p>Title: {prepared.title}</p>
        <pre style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{prepared.description}</pre>
        <p>Expires: {prepared.expiresAtUtc}. Confirmation creates a Draft only; it does not authorize execution or publication.</p>
        <button type="button" disabled={busy} onClick={() => void confirm()}>Confirm Draft import</button>
      </div>}
      {result && <p role="status">{result.replayed ? 'Existing Draft import found.' : 'Draft created.'}{' '}
        <button type="button" onClick={() => onViewTask(result.taskId)}>View task</button></p>}
    </div>}
  </section>;
}
