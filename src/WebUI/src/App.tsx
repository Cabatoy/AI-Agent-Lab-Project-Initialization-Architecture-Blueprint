import { useEffect, useRef, useState } from 'react';
import { api, type Task } from './api';
import { CommandCenter } from './CommandCenter';
import { LocalRunnerWorkflow } from './LocalRunnerWorkflow';
import { LocalDiagnosticsPanel, LocalDiagnosticsResult } from './LocalDiagnosticsPanel';
import { NativeWorkspacePanel } from './NativeWorkspacePanel';
import { PlanningWorkflow } from './PlanningWorkflow';
import { Status } from './components';
import { readState, useDashboard, type Resource } from './useDashboard';

type Page = 'Command Center' | 'Planning' | 'Local Runner' | 'Dashboard' | 'Tasks' | 'Plans' | 'Agents' | 'Models & Providers' | 'Approvals';
type DetailResource = { data: Task | null; error: string | null };
const pages: { name: Page; icon: string }[] = [
  { name: 'Command Center', icon: '⌘' },
  { name: 'Planning', icon: '◇' },
  { name: 'Local Runner', icon: '▶' },
  { name: 'Dashboard', icon: '◫' }, { name: 'Tasks', icon: '▤' },
  { name: 'Plans', icon: '◇' }, { name: 'Agents', icon: '⬡' },
  { name: 'Models & Providers', icon: '◈' }, { name: 'Approvals', icon: '✓' },
];
const subtitles: Partial<Record<Page, string>> = {
  'Command Center': 'Describe work in natural language and save it as a Draft task.',
  'Local Runner': 'Approve one exact, read-only local system health action and review its durable result.',
  Planning: 'Select the configured repository and review a local, versioned plan preview.',
  Dashboard: 'Read-only task, plan and health data from the local Control Plane.',
  Tasks: 'Inspect task state from the Control Plane.',
  Plans: 'Review available execution plans.',
};
const initialDetail = (): DetailResource => ({ data: null, error: null });
const displayDate = (value: string) => new Date(value).toLocaleString();
const message = (reason: unknown) => reason instanceof Error ? reason.message : 'Unexpected error.';

function ResourceNotice<T>({ resource, now, online }: { resource: Resource<T>; now: number; online: boolean }) {
  const state = readState(resource, now, online);
  const stale = resource.data !== null && (state === 'stale' || state === 'offline');
  return <div className={`resource-notice ${stale ? 'stale' : ''}`} data-state={state}>
    <span>{stale ? 'Stale · last known data' : state === 'loading' ? 'Loading…' : state.replace('-', ' ')}</span>
    {resource.fetchedAt !== null && <span>Last successful read: <time dateTime={new Date(resource.fetchedAt).toISOString()}>{displayDate(new Date(resource.fetchedAt).toISOString())}</time></span>}
    {resource.error && <p role="alert">{resource.error}</p>}
    {state === 'offline' && <p>Offline · current data unknown. No empty result is implied.</p>}
    {resource.data === null && state !== 'loading' && <p>Unknown · no successful data available.</p>}
  </div>;
}

function State<T>({ resource, now, online, empty, children }: {
  resource: Resource<T[]>, now: number, online: boolean, empty: string, children: (data: T[]) => React.ReactNode
}) {
  return <><ResourceNotice resource={resource} now={now} online={online} />
    {resource.data !== null && (resource.data.length ? children(resource.data) : <div className="notice">{empty}</div>)}</>;
}

function TaskTable({ tasks, onSelect }: { tasks: Task[], onSelect: (id: string) => void }) {
  return <div className="table-scroll"><table>
    <thead><tr><th>Task</th><th>Status</th><th>Attempts</th><th>Created</th></tr></thead>
    <tbody>{tasks.map(task => <tr key={task.id}>
      <td data-label="Task"><button className="task-link" data-task-id={task.id} onClick={() => onSelect(task.id)}>{task.title}</button><span className="identifier">{task.id}</span></td>
      <td data-label="Status"><Status value={task.status} /></td><td data-label="Attempts">{task.attemptCount}</td><td data-label="Created">{displayDate(task.createdAtUtc)}</td>
    </tr>)}</tbody>
  </table></div>;
}

function Detail({ id, onClose }: { id: string, onClose: () => void }) {
  const [detail, setDetail] = useState<DetailResource>(initialDetail);
  const [loading, setLoading] = useState(true);
  const [retry, setRetry] = useState(0);
  const heading = useRef<HTMLHeadingElement>(null);
  useEffect(() => { heading.current?.focus(); }, [id]);
  useEffect(() => {
    let active = true;
    const controller = new AbortController();
    setLoading(true);
    setDetail(initialDetail());
    api.task(id, controller.signal).then(
      data => { if (active) setDetail({ data, error: null }); },
      error => { if (active) setDetail({ data: null, error: message(error) }); },
    ).finally(() => { if (active) setLoading(false); });
    return () => { active = false; controller.abort(); };
  }, [id, retry]);
  const task = detail.data;
  return <section className="panel detail" aria-labelledby="detail-title">
    <div className="panel-heading"><div><p className="eyebrow">TASK DETAIL</p><h2 id="detail-title" ref={heading} tabIndex={-1}>{task?.title ?? 'Task detail'}</h2></div>
      <button className="secondary" onClick={onClose}>Close detail</button></div>
    {loading && <div className="notice" role="status">Loading task detail…</div>}
    {detail.error && <div className="notice error" role="alert">{detail.error} <button className="text-button" onClick={() => setRetry(n => n + 1)}>Retry</button></div>}
    {task && <><div className="detail-grid">
      <div><span className="label">Identifier</span><span className="mono">{task.id}</span></div>
      <div><span className="label">Status</span><Status value={task.status} /></div>
      <div><span className="label">Attempts</span>{task.attemptCount}</div>
      <div><span className="label">Updated</span>{displayDate(task.updatedAtUtc)}</div>
    </div>
    <div className="detail-block"><span className="label">Description</span><p>{task.description || 'No description provided.'}</p></div>
    <div className="detail-block"><span className="label">Result</span><p className="preserve">{task.result ?? 'Not available.'}</p></div>
    <div className="detail-block"><span className="label">Error message</span><p className="preserve">{task.errorMessage ?? 'Not available.'}</p></div>
    <p className="muted small">Execution history and audit events are not exposed by this API.</p></>}
  </section>;
}

function Planned({ page }: { page: Page }) {
  const content: Record<string, { heading: string; description: string; items: string[] }> = {
    Agents: { heading: 'Agent management is planned', description: 'Agent creation and management will be introduced in a later milestone.', items: ['No persisted agent definitions are available.', 'No agent execution can be started from this dashboard.'] },
    'Models & Providers': { heading: 'Provider management is planned', description: 'Model selection and capability routing are not connected in this milestone.', items: ['Local Podman LLM providers', 'Optional paid / cloud providers', 'Provider capability routing', 'Token and budget visibility: not available'] },
    Approvals: { heading: 'Approvals are read-only', description: 'The full human approval workflow is not implemented here.', items: ['No approval or rejection actions are available.', 'No authorization or approval registry is implied.'] },
  };
  const info = content[page];
  return <section className="panel planned"><span className="planned-icon" aria-hidden="true">◇</span><p className="eyebrow">FUTURE CAPABILITY</p>
    <h2>{info.heading}</h2><p className="muted">{info.description}</p><ul>{info.items.map(item => <li key={item}>{item}</li>)}</ul>
    <span className="outline-badge">Not implemented</span></section>;
}

export default function App() {
  const [page, setPage] = useState<Page>('Dashboard');
  const { health, tasks, plans, diagnostics, loading, online, now, refresh, refreshAfterWrite } = useDashboard();
  const [menuOpen, setMenuOpen] = useState(false);
  const menuButton = useRef<HTMLButtonElement>(null);
  const returnPage = useRef<Page>('Dashboard');
  const [selected, setSelected] = useState<string | null>(null);
  useEffect(() => {
    const clearDetail = () => setSelected(null);
    window.addEventListener('hic-operator-session-ended', clearDetail);
    window.addEventListener('hic-dashboard-context-changed', clearDetail);
    return () => {
      window.removeEventListener('hic-operator-session-ended', clearDetail);
      window.removeEventListener('hic-dashboard-context-changed', clearDetail);
    };
  }, []);
  useEffect(() => {
    if (tasks.failure === 'unauthenticated' || tasks.failure === 'forbidden') setSelected(null);
  }, [tasks.failure]);
  const selectTask = (id: string) => { returnPage.current = page; setSelected(id); setPage('Tasks'); };
  const navigate = (next: Page) => {
    setPage(next); setSelected(null); setMenuOpen(false);
    if (menuOpen) menuButton.current?.focus();
  };
  const closeDetail = () => {
    const id = selected;
    setSelected(null); setPage(returnPage.current);
    requestAnimationFrame(() => document.querySelector<HTMLButtonElement>(`button[data-task-id="${id}"]`)?.focus());
  };
  const counts = tasks.data?.reduce<Record<string, number>>((acc, item) => {
    acc[item.status] = (acc[item.status] ?? 0) + 1; return acc;
  }, {});
  const connected = health.data?.status === 'ok' && health.data.service === 'control-plane';
  return <div className="shell">
    <a className="skip-link" href="#main">Skip to content</a>
    <button className="menu-toggle secondary" ref={menuButton} aria-expanded={menuOpen} aria-controls="workspace-navigation"
      onClick={() => setMenuOpen(value => !value)}>Menu</button>
    <aside id="workspace-navigation" className={`sidebar ${menuOpen ? 'menu-open' : ''}`} onKeyDown={event => {
      if (event.key === 'Escape') { setMenuOpen(false); menuButton.current?.focus(); }
    }}>
      <div className="brand"><div className="brand-mark">H</div><div><strong>HIC</strong><span>CONTROL PLANE</span></div></div>
      <div className="nav-label">WORKSPACE</div>
      <nav aria-label="Main navigation">{pages.map(item => <button key={item.name} className={`nav-item ${page === item.name ? 'selected' : ''} ${item.name === 'Command Center' ? 'nav-command' : ''}`}
        aria-current={page === item.name ? 'page' : undefined} onClick={() => navigate(item.name)}>
        <span className="nav-icon" aria-hidden="true">{item.icon}</span>{item.name}
        {['Agents', 'Models & Providers', 'Approvals'].includes(item.name) && <span className="nav-soon">SOON</span>}
      </button>)}</nav>
      <div className="sidebar-bottom">LOCAL WORKSPACE <p>Loopback prototype · not tenant-authorized</p></div>
    </aside>
    <main id="main" className="main" tabIndex={-1}>
      <header className="topbar"><div><span className="breadcrumb">HIC / WORKSPACE /</span> <span>{page.toUpperCase()}</span></div>
        <div className="top-actions"><span className="read-only"><span>●</span> {page === 'Command Center' ? 'DRAFT CREATION ONLY' : page === 'Local Runner' ? 'HUMAN-APPROVED LOCAL RUN' : 'READ ONLY'}</span>
          {page !== 'Command Center' && <button className="secondary" onClick={() => navigate('Command Center')}>＋ New task</button>}
          <button className="refresh" aria-busy={loading} onClick={() => void refresh()}>{loading ? 'Refreshing…' : '↻  Refresh'}</button></div></header>
      <div className="content"><div className="page-heading"><div><p className="eyebrow">HUMAN IN CONTROL</p>
        <h1>{page}</h1><p className="subtitle">{subtitles[page] ?? 'A clear view of what comes next.'}</p></div>
        <div className="last-refresh"><span>LOCAL LOOPBACK DATA</span><strong>Per-panel timestamps · not a login</strong></div></div>
      {loading && <div className="refresh-state" role="status">Refreshing Control Plane data…</div>}
      <CommandCenter hidden={page !== 'Command Center'} onCreated={() => void refreshAfterWrite()} onViewTask={selectTask} onCheckTasks={refreshAfterWrite} />
      {page === 'Local Runner' && <LocalRunnerWorkflow />}
      {page === 'Planning' && <PlanningWorkflow />}
      {page === 'Command Center' && <LocalDiagnosticsPanel />}
      {page === 'Command Center' && <NativeWorkspacePanel />}
      {page === 'Command Center' && <section className="panel"><div className="panel-heading"><div><p className="eyebrow">CONTROL PLANE</p><h2>Recent tasks</h2></div>
        <button className="text-button" onClick={() => navigate('Tasks')}>View all →</button></div>
        <State resource={tasks} now={now} online={online} empty="No tasks yet.">{data => <TaskTable tasks={data.slice(0, 5)} onSelect={selectTask} />}</State></section>}
      {page === 'Dashboard' && <>
        <p className="notice">Read-only local overview · no operator or tenant authorization is implied. No services, Worker or executor are started.</p>
        <div className="metrics"><section className="metric" aria-label="Control Plane health"><span className="label">CONTROL PLANE</span><strong>{health.data ? (connected ? 'Connected' : 'Unknown') : 'Unknown'}</strong>
          <ResourceNotice resource={health} now={now} online={online} /><p>Health endpoint only · not a dependency health check</p></section>
          <section className="metric" aria-label="Task count"><span className="label">RETURNED TASKS</span><strong>{tasks.data !== null ? tasks.data.length : 'Unknown'}</strong><ResourceNotice resource={tasks} now={now} online={online} /></section>
          <section className="metric" aria-label="Plan count"><span className="label">EXECUTION PLANS</span><strong>{plans.data !== null ? plans.data.length : 'Unknown'}</strong><ResourceNotice resource={plans} now={now} online={online} /></section></div>
        <div className="overview-grid"><section className="panel" aria-labelledby="task-status-title"><div className="panel-heading"><div><p className="eyebrow">TASK INVENTORY</p><h2 id="task-status-title">Status distribution</h2></div></div>
          <p className="muted small">Exact task states, not approval counts or Worker activity.</p>
          <ResourceNotice resource={tasks} now={now} online={online} />
          {!counts ? <div className="notice">Task status unknown.</div>
            : !Object.keys(counts).length ? <div className="notice">No task statuses to display.</div>
              : <div className="status-list">{Object.entries(counts).map(([status, count]) => <div key={status}><Status value={status} /><strong>{count}</strong></div>)}</div>}</section>
          <section className="panel" aria-labelledby="dependencies-title"><p className="eyebrow">SOURCE-SCOPED HEALTH</p><h2 id="dependencies-title">Dependency diagnostics</h2>
            <ResourceNotice resource={diagnostics} now={now} online={online} />
            {diagnostics.data && <LocalDiagnosticsResult result={diagnostics.data} />}
          </section>
          <section className="panel"><p className="eyebrow">SYSTEM BOUNDARY</p><h2>Operational state</h2>
            <p className="muted">This dashboard reads existing Control Plane data and can create Draft tasks from the Command Center. Outside the fixed Local Runner health action, it cannot approve, dispatch or execute tasks.</p>
            <div className="boundary"><span>Worker / executor</span><strong>Not measured / not activated</strong></div>
            <div className="boundary"><span>Task creation</span><strong>Draft only</strong></div>
            <div className="boundary"><span>Approvals / GitHub CI / costs / resources</span><strong>Not connected</strong></div></section></div>
        <section className="panel"><div className="panel-heading"><div><p className="eyebrow">LATEST ACTIVITY</p><h2>Recent tasks</h2></div><button className="text-button" onClick={() => setPage('Tasks')}>View all →</button></div>
          <State resource={tasks} now={now} online={online} empty="No tasks yet.">{data => <TaskTable tasks={data.slice(0, 5)} onSelect={selectTask} />}</State></section>
      </>}
      {page === 'Tasks' && <>{selected && <Detail id={selected} onClose={closeDetail} />}<section className="panel">
        <div className="panel-heading"><div><p className="eyebrow">CONTROL PLANE</p><h2>All tasks</h2></div><span className="outline-badge">{tasks.data ? `${tasks.data.length} TASKS` : 'UNAVAILABLE'}</span></div>
        <p className="muted small">Showing at most 50 returned tasks. The current API is unpaginated.</p>
        <State resource={tasks} now={now} online={online} empty="No tasks yet.">{data => <TaskTable tasks={data.slice(0, 50)} onSelect={selectTask} />}</State></section></>}
      {page === 'Plans' && <section className="panel"><div className="panel-heading"><div><p className="eyebrow">CONTROL PLANE</p><h2>Execution plans</h2></div><span className="outline-badge">READ ONLY</span></div>
        <p className="muted small">Task relationships and dependency graphs are not exposed by the current plans API.</p>
        <p className="muted small">Showing at most 50 returned plans. No server pagination or total aggregation is implied.</p>
        <State resource={plans} now={now} online={online} empty="No plans yet.">{data => <div className="plan-list">{data.slice(0, 50).map(plan => <article className="plan" key={plan.id}>
          <div className="plan-top"><div><h3>{plan.requestTitle}</h3><span className="identifier">{plan.id}</span></div><Status value={plan.status} /></div>
          <p className="preserve">{plan.planSummary}</p><div className="plan-meta"><span>Project: {plan.projectKey ?? 'Not provided'}</span><span>Created: {displayDate(plan.createdAtUtc)}</span></div>
          {plan.requestDescription && <p className="muted preserve">{plan.requestDescription}</p>}</article>)}</div>}</State></section>}
      {['Agents', 'Models & Providers', 'Approvals'].includes(page) && <Planned page={page} />}
      <footer>HIC LOCAL DASHBOARD <span>·</span> CONTROL PLANE IS THE SOURCE OF TRUTH</footer>
      </div>
    </main>
  </div>;
}
