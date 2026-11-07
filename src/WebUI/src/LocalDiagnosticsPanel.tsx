import { useRef, useState } from 'react';
import { api, type LocalDiagnosticSnapshot } from './api';

const label = (status: 'ok' | 'unavailable' | 'not-checked') =>
  status === 'ok' ? 'Responding' : status === 'unavailable' ? 'Unavailable' : 'Not checked';

export function LocalDiagnosticsPanel() {
  const [result, setResult] = useState<LocalDiagnosticSnapshot | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);
  const busy = useRef(false);

  async function inspect() {
    if (busy.current) return;
    busy.current = true;
    setPending(true);
    setError(null);
    try {
      setResult(await api.localDiagnostics());
    } catch {
      // Do not surface low-level networking details, secrets or internal server traces.
      setResult(null);
      setError('Local diagnostics unavailable. Check that the new Control Plane version is running.');
    } finally {
      busy.current = false;
      setPending(false);
    }
  }

  return <section className="panel command-panel" aria-labelledby="local-diagnostics-title">
    <div className="panel-heading">
      <div><p className="eyebrow">SAFE LOCAL ACTION</p><h2 id="local-diagnostics-title">Check system health</h2></div>
      <span className="outline-badge">READ ONLY</span>
    </div>
    <p className="muted">Runs a fixed, allowlisted diagnostic on the local Control Plane. It checks PostgreSQL and Redis connectivity without executing a task, Worker, model or shell command. Worker state is not measured.</p>
    <button type="button" className="secondary" disabled={pending} onClick={() => void inspect()}>
      {pending ? 'Checking…' : 'Run local diagnostics'}
    </button>
    {pending && <p role="status" className="muted small">Checking local services…</p>}
    {error && <p className="notice error" role="alert">{error}</p>}
    {result && <div className="local-diagnostics" role="status">
      <div className="boundary"><span>Control Plane API</span><strong>{label(result.controlPlane)}</strong></div>
      <div className="boundary"><span>PostgreSQL</span><strong>{label(result.postgres)}</strong></div>
      <div className="boundary"><span>Redis</span><strong>{label(result.redis)}</strong></div>
      <div className="boundary"><span>Worker</span><strong>{label(result.worker)}</strong></div>
      <p className="muted small">Checked: {new Date(result.checkedAtUtc).toLocaleString()} · No task created or executed. This is not an audited agent run.</p>
    </div>}
  </section>;
}
