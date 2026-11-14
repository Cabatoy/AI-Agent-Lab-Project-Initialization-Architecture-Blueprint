import { useEffect, useRef, useState } from 'react';

type Observation = { orderId: string; status: 'observed-untrusted'; observedAt: string; executionAllowed: false };
type Snapshot = { version: 'hic.staging-ui-status/1'; trust: 'untrusted-local-observation'; executionAllowed: false; observations: Observation[] };

const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
function validSnapshot(value: unknown): value is Snapshot {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
  const v = value as Record<string, unknown>;
  return v.version === 'hic.staging-ui-status/1' && v.trust === 'untrusted-local-observation' &&
    v.executionAllowed === false && Array.isArray(v.observations) && v.observations.length <= 20 &&
    v.observations.every((item: unknown) => {
      if (!item || typeof item !== 'object' || Array.isArray(item)) return false;
      const o = item as Record<string, unknown>;
      return typeof o.orderId === 'string' && uuid.test(o.orderId) &&
        o.status === 'observed-untrusted' && o.executionAllowed === false &&
        typeof o.observedAt === 'string' && Number.isFinite(Date.parse(o.observedAt));
    });
}

async function readObservations(token: string, signal: AbortSignal): Promise<Snapshot> {
  const response = await fetch('/api/local-runner/staging-observations', {
    method: 'GET', signal, cache: 'no-store',
    headers: { 'x-hic-request': 'local-runner', authorization: `Bearer ${token}` },
  });
  if (!response.ok) throw new Error('Staging observations are unavailable or the operator session expired.');
  const body: unknown = await response.json();
  if (!validSnapshot(body)) throw new Error('The staging response failed the safety contract.');
  return body;
}

export function StagingObservationsPanel() {
  const [bootstrap, setBootstrap] = useState('');
  const [token, setToken] = useState('');
  const [snapshot, setSnapshot] = useState<Snapshot | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [lastUpdated, setLastUpdated] = useState<string | null>(null);
  const [authenticating, setAuthenticating] = useState(false);
  const pending = useRef(false);

  async function authenticate() {
    if (!bootstrap || authenticating) return;
    setAuthenticating(true);
    setError(null);
    try {
      const response = await fetch('/api/local-runner/session', {
        method: 'POST', cache: 'no-store',
        headers: { 'content-type': 'application/json', 'x-hic-request': 'local-runner' },
        body: JSON.stringify({ bootstrapToken: bootstrap }),
      });
      if (!response.ok) throw new Error('Operator authentication failed.');
      const result: unknown = await response.json();
      if (!result || typeof result !== 'object' || typeof (result as Record<string, unknown>).accessToken !== 'string')
        throw new Error('Invalid operator session response.');
      setToken((result as { accessToken: string }).accessToken);
      setBootstrap('');
    } catch {
      setToken('');
      setSnapshot(null);
      setError('Operator authentication failed. Verify the local operator token and API.');
    } finally {
      setAuthenticating(false);
    }
  }

  useEffect(() => {
    if (!token) return;
    let active = true;
    let controller: AbortController | null = null;
    const refresh = async () => {
      if (pending.current || document.visibilityState === 'hidden') return;
      pending.current = true;
      controller = new AbortController();
      try {
        const data = await readObservations(token, controller.signal);
        if (active) { setSnapshot(data); setError(null); setLastUpdated(new Date().toISOString()); }
      } catch {
        if (active && !controller.signal.aborted) {
          setSnapshot(null);
          setLastUpdated(null);
          setError('Staging status unavailable. No previous observations are being presented as current.');
        }
      } finally { pending.current = false; }
    };
    void refresh();
    const timer = window.setInterval(() => { void refresh(); }, 10000);
    return () => { active = false; window.clearInterval(timer); controller?.abort(); pending.current = false; };
  }, [token]);

  return <section className="panel" aria-labelledby="staging-observations-title">
    <div className="panel-heading">
      <div><p className="eyebrow">RELAY · READ ONLY</p><h2 id="staging-observations-title">Staging observations</h2></div>
      <span className="outline-badge">UNTRUSTED · EXECUTION DISABLED</span>
    </div>
    <p className="muted">These are local Relay observations, not approved tasks or evidence of agent execution. Refreshes every 10 seconds while this page is visible.</p>
    {!token && <div className="command-form">
      <label className="field-label" htmlFor="staging-bootstrap-token">Local operator bootstrap token</label>
      <input id="staging-bootstrap-token" type="password" autoComplete="off" className="command-title"
        value={bootstrap} onChange={event => setBootstrap(event.target.value)} />
      <button type="button" className="secondary" disabled={authenticating || !bootstrap}
        onClick={() => void authenticate()}>{authenticating ? 'Authenticating…' : 'Authenticate to view'}</button>
    </div>}
    {token && <button type="button" className="secondary" onClick={() => { setToken(''); setSnapshot(null); setLastUpdated(null); }}>Disconnect</button>}
    {error && <p className="notice error" role="alert">{error}</p>}
    {snapshot && <div>
      <p className="muted small">Observed records: {snapshot.observations.length} · Last refreshed: {lastUpdated ? new Date(lastUpdated).toLocaleString() : 'Never'}</p>
      {snapshot.observations.length === 0 ? <p className="notice">No observations found.</p> :
        <div className="table-scroll"><table>
          <thead><tr><th>Order ID</th><th>Status</th><th>Observed</th><th>Execution</th></tr></thead>
          <tbody>{snapshot.observations.map(item => <tr key={item.orderId}>
            <td className="mono">{item.orderId}</td><td>{item.status}</td>
            <td>{new Date(item.observedAt).toLocaleString()}</td><td>Disabled</td>
          </tr>)}</tbody>
        </table></div>}
    </div>}
  </section>;
}
