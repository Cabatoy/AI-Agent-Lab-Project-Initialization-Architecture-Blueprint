import { useRef, useState } from 'react';
import { api, type NativeWorkspaceInspection } from './api';

export function NativeWorkspacePanel() {
  const [result, setResult] = useState<NativeWorkspaceInspection | null>(null);
  const [failure, setFailure] = useState<string | null>(null);
  const [running, setRunning] = useState(false);
  const inFlight = useRef(false);

  async function run() {
    if (inFlight.current) return;
    inFlight.current = true;
    setRunning(true);
    setResult(null);
    setFailure(null);
    try {
      setResult(await api.inspectNativeWorkspace());
    } catch {
      setFailure('Native host is unavailable or rejected the request. No action result was confirmed.');
    } finally {
      inFlight.current = false;
      setRunning(false);
    }
  }

  return <section className="panel command-panel" aria-labelledby="native-workspace-title">
    <div className="panel-heading">
      <div><p className="eyebrow">NATIVE HOST · WINDOWS</p><h2 id="native-workspace-title">Local workspace inspection</h2></div>
      <span className="outline-badge">FIXED READ-ONLY ACTION</span>
    </div>
    <p className="muted">The separate HIC Native Host checks the configured local checkout for Git metadata and HumanInControl.sln. It cannot run PowerShell, Git commands or arbitrary input. This is not a task-backed agent execution.</p>
    <button className="secondary" type="button" disabled={running} onClick={() => void run()}>
      {running ? 'Inspecting…' : 'Inspect local workspace'}
    </button>
    {running && <p role="status" className="muted small">Contacting local native host…</p>}
    {failure && <p role="alert" className="notice error">{failure}</p>}
    {result && <div role="status">
      <p className="muted small">Native action completed · {new Date(result.checkedAtUtc).toLocaleString()}</p>
      <div className="boundary"><span>Configured workspace</span><strong>{result.workspaceAvailable ? 'Found' : 'Not found'}</strong></div>
      <div className="boundary"><span>Git checkout</span><strong>{result.gitCheckoutFound ? 'Found' : 'Not found'}</strong></div>
      <div className="boundary"><span>HumanInControl.sln</span><strong>{result.solutionFound ? 'Found' : 'Not found'}</strong></div>
      <p className="muted small">Run ID: <span className="mono">{result.runId}</span> · Read-only probe; not persisted as a Control Plane task.</p>
    </div>}
  </section>;
}
