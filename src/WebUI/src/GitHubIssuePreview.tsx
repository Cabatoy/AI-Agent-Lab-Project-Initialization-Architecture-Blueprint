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

export function GitHubIssuePreview({ token }: { token: string }) {
  const [reference, setReference] = useState('');
  const [preview, setPreview] = useState<IssuePreview | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function load() {
    setPreview(null); setError(null); setBusy(true);
    try {
      setPreview(await localRunnerRequest('/github/issues/preview', isIssuePreview, token, 'POST', { reference }, 200, 120000));
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Issue preview unavailable.');
    } finally { setBusy(false); }
  }
  return <section aria-label="GitHub issue preview">
    <h3>GitHub issue preview</h3>
    <p>Read-only preview for the configured authorized public repository. Private issues are not enabled.</p>
    <label htmlFor="github-issue-reference">Issue URL or owner/repository/#number</label>
    <input id="github-issue-reference" value={reference} maxLength={512} disabled={busy}
      onChange={event => { setReference(event.target.value); setPreview(null); setError(null); }} />
    <button type="button" disabled={busy || !reference.trim()} onClick={() => void load()}>Preview issue</button>
    {busy && <p role="status">Loading issue…</p>}
    {error && <p role="alert">{error}</p>}
    {preview && <div>
      <p className="notice">Untrusted issue content. This preview creates no task, approval, execution, or publication.
        Import and durable history are not yet available.</p>
      <h4>{preview.title}</h4>
      <p>Source: https://github.com/{preview.reference.owner}/{preview.reference.repository}/issues/{preview.reference.number}</p>
      <p>Updated: {preview.updatedAtUtc}</p>
      <p>Revision SHA-256: {preview.revisionSha256}</p>
      <pre style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{preview.body}</pre>
    </div>}
  </section>;
}
