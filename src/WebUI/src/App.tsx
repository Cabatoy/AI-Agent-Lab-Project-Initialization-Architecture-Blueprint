import { useCallback, useEffect, useState } from 'react';
import { api, type Health, type Plan, type Task } from './api';
import { CommandCenter } from './CommandCenter';
import { LocalRunnerWorkflow } from './LocalRunnerWorkflow';
import { GitRunnerWorkflow } from './GitRunnerWorkflow';
import { ModelConversationWorkspace } from './ModelConversationWorkspace';
import { LocalDiagnosticsPanel } from './LocalDiagnosticsPanel';
import { NativeWorkspacePanel } from './NativeWorkspacePanel';
import { PlanningWorkflow } from './PlanningWorkflow';
import { Status } from './components';

type Page = 'Command Center' | 'Conversations' | 'Planning' | 'Local Runner' | 'Isolated Git' | 'Overview' | 'Tasks' | 'Plans' | 'Agents' | 'Models & Providers' | 'Approvals';
type Resource<T> = { data: T | null; error: string | null };
const pages: { name: Page; icon: string }[] = [
  { name: 'Command Center', icon: '⌘' },
  { name: 'Conversations', icon: '◉' },
  { name: 'Planning', icon: '◇' },
  { name: 'Local Runner', icon: '▶' },
  { name: 'Isolated Git', icon: '◇' },
  { name: 'Overview', icon: '◫' }, { name: 'Tasks', icon: '▤' },
  { name: 'Plans', icon: '◇' }, { name: 'Agents', icon: '⬡' },
  { name: 'Models & Providers', icon: '◈' }, { name: 'Approvals', icon: '✓' },
];
const subtitles: Partial<Record<Page, string>> = {
  'Command Center': 'Describe work in natural language and save it as a Draft task.',
  'Local Runner': 'Approve one exact, read-only local system health action and review its durable result.',
  'Isolated Git': 'Approve one bounded disposable-repository edit and inspect its actual diff and test result.',
  Conversations: 'Talk to the configured local model, review a versioned plan and approve one isolated code edit.',
  Planning: 'Select the configured repository and review a local, versioned plan preview.',
  Overview: 'Your local software factory, in view.',
  Tasks: 'Inspect task state from the Control Plane.',
  Plans: 'Review available execution plans.',
};
const initial = <T,>(): Resource<T> => ({ data: null, error: null });
const displayDate = (value: string) => new Date(value).toLocaleString();
const message = (reason: unknown) => reason instanceof Error ? reason.message : 'Unexpected error.';
const fromResult = <T,>(result: PromiseSettledResult<T>): Resource<T> =>
  result.status === 'fulfilled' ? { data: result.value, error: null } : { data: null, error: message(result.reason) };

function State<T>({ resource, loading, empty, children }: {
  resource: Resource<T[]>, loading: boolean, empty: string, children: (data: T[]) => React.ReactNode
}) {
  if (resource.error) return <div className="notice error" role="alert">{resource.error}</div>;
  if (!resource.data) return <div className="notice" role="status">{loading ? 'Loading data…' : 'Data unavailable.'}</div>;
  if (!resource.data.length) return <div className="notice">{empty}</div>;
  return <>{children(resource.data)}</>;
}

function TaskTable({ tasks, onSelect }: { tasks: Task[], onSelect: (id: string) => void }) {
  return <div className="table-scroll"><table>
    <thead><tr><th>Task</th><th>Status</th><th>Attempts</th><th>Created</th></tr></thead>
    <tbody>{tasks.map(task => <tr key={task.id}>
      <td><button className="task-link" onClick={() => onSelect(task.id)}>{task.title}</button><span className="identifier">{task.id}</span></td>
      <td><Status value={task.status} /></td><td>{task.attemptCount}</td><td>{displayDate(task.createdAtUtc)}</td>
    </tr>)}</tbody>
  </table></div>;
}

function Detail({ id, onClose }: { id: string, onClose: () => void }) {
  const [detail, setDetail] = useState<Resource<Task>>(initial);
  const [loading, setLoading] = useState(true);
  const [retry, setRetry] = useState(0);
  useEffect(() => {
    let active = true;
    setLoading(true);
    setDetail(initial());
    api.task(id).then(
      data => { if (active) setDetail({ data, error: null }); },
      error => { if (active) setDetail({ data: null, error: message(error) }); },
    ).finally(() => { if (active) setLoading(false); });
    return () => { active = false; };
  }, [id, retry]);
  const task = detail.data;
  return <section className="panel detail" aria-labelledby="detail-title">
    <div className="panel-heading"><div><p className="eyebrow">TASK DETAIL</p><h2 id="detail-title">{task?.title ?? 'Task detail'}</h2></div>
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
  const [page, setPage] = useState<Page>('Overview');
  const [health, setHealth] = useState<Resource<Health>>(initial);
  const [tasks, setTasks] = useState<Resource<Task[]>>(initial);
  const [plans, setPlans] = useState<Resource<Plan[]>>(initial);
  const [loading, setLoading] = useState(true);
  const [lastRefresh, setLastRefresh] = useState<Date | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const refresh = useCallback(async () => {
    setLoading(true);
    const [h, t, p] = await Promise.allSettled([api.health(), api.tasks(), api.plans()]);
    setHealth(fromResult(h)); setTasks(fromResult(t)); setPlans(fromResult(p));
    if (h.status === 'fulfilled' && t.status === 'fulfilled' && p.status === 'fulfilled') setLastRefresh(new Date());
    setLoading(false);
    return t.status === 'fulfilled';
  }, []);
  useEffect(() => { void refresh(); }, [refresh]);
  const selectTask = (id: string) => { setSelected(id); setPage('Tasks'); };
  const navigate = (next: Page) => { setPage(next); setSelected(null); };
  const counts = tasks.data?.reduce<Record<string, number>>((acc, item) => {
    acc[item.status] = (acc[item.status] ?? 0) + 1; return acc;
  }, {});
  const connected = health.data?.status === 'ok' && health.data.service === 'control-plane';
  return <div className="shell">
    <a className="skip-link" href="#main">Skip to content</a>
    <aside className="sidebar">
      <div className="brand"><div className="brand-mark">H</div><div><strong>HIC</strong><span>CONTROL PLANE</span></div></div>
      <div className="nav-label">WORKSPACE</div>
      <nav aria-label="Main navigation">{pages.map(item => <button key={item.name} className={`nav-item ${page === item.name ? 'selected' : ''} ${item.name === 'Command Center' ? 'nav-command' : ''}`}
        aria-current={page === item.name ? 'page' : undefined} onClick={() => navigate(item.name)}>
        <span className="nav-icon" aria-hidden="true">{item.icon}</span>{item.name}
        {['Agents', 'Models & Providers', 'Approvals'].includes(item.name) && <span className="nav-soon">SOON</span>}
      </button>)}</nav>
      <div className="sidebar-bottom"><span className="side-dot" /> LOCAL WORKSPACE <p>Read data · Draft tasks · fixed local health</p></div>
    </aside>
    <main id="main" className="main" tabIndex={-1}>
      <header className="topbar"><div><span className="breadcrumb">HIC / WORKSPACE /</span> <span>{page.toUpperCase()}</span></div>
        <div className="top-actions"><span className="read-only"><span>●</span> {page === 'Command Center' ? 'DRAFT CREATION ONLY' : page === 'Local Runner' || page === 'Isolated Git' ? 'HUMAN-APPROVED LOCAL RUN' : 'READ ONLY'}</span>
          {page !== 'Command Center' && <button className="secondary" onClick={() => navigate('Command Center')}>＋ New task</button>}
          <button className="refresh" disabled={loading} onClick={() => void refresh()}>{loading ? 'Refreshing…' : '↻  Refresh'}</button></div></header>
      <div className="content"><div className="page-heading"><div><p className="eyebrow">HUMAN IN CONTROL</p>
        <h1>{page}</h1><p className="subtitle">{subtitles[page] ?? 'A clear view of what comes next.'}</p></div>
        <div className="last-refresh"><span>LAST SUCCESSFUL REFRESH</span><strong>{lastRefresh ? displayDate(lastRefresh.toISOString()) : 'Not yet available'}</strong></div></div>
      {loading && <div className="refresh-state" role="status">Refreshing Control Plane data…</div>}
      <CommandCenter hidden={page !== 'Command Center'} onCreated={() => void refresh()} onViewTask={selectTask} onCheckTasks={refresh} />
      {page === 'Local Runner' && <LocalRunnerWorkflow />}
      {page === 'Isolated Git' && <GitRunnerWorkflow />}
      {page === 'Conversations' && <ModelConversationWorkspace />}
      {page === 'Planning' && <PlanningWorkflow />}
      {page === 'Command Center' && <LocalDiagnosticsPanel />}
      {page === 'Command Center' && <NativeWorkspacePanel />}
      {page === 'Command Center' && <section className="panel"><div className="panel-heading"><div><p className="eyebrow">CONTROL PLANE</p><h2>Recent tasks</h2></div>
        <button className="text-button" onClick={() => navigate('Tasks')}>View all →</button></div>
        <State resource={tasks} loading={loading} empty="No tasks yet.">{data => <TaskTable tasks={data.slice(0, 5)} onSelect={selectTask} />}</State></section>}
      {page === 'Overview' && <>
        <div className="metrics"><section className="metric"><span className="label">CONTROL PLANE</span><strong>{health.error ? 'Unavailable' : health.data ? (connected ? 'Connected' : 'Unexpected response') : 'Checking…'}</strong>
          <p>{health.error ?? 'Health endpoint only · not a dependency health check'}</p></section>
          <section className="metric"><span className="label">TOTAL TASKS</span><strong>{tasks.data ? tasks.data.length : '—'}</strong><p>{tasks.error ?? 'Reported by Control Plane'}</p></section>
          <section className="metric"><span className="label">EXECUTION PLANS</span><strong>{plans.data ? plans.data.length : '—'}</strong><p>{plans.error ?? 'Reported by Control Plane'}</p></section></div>
        <div className="overview-grid"><section className="panel"><div className="panel-heading"><div><p className="eyebrow">TASK INVENTORY</p><h2>Status distribution</h2></div><span className="outline-badge">LIVE DATA</span></div>
          {tasks.error ? <div className="notice error" role="alert">{tasks.error}</div> : !counts ? <div className="notice">Loading task status…</div>
            : !Object.keys(counts).length ? <div className="notice">No task statuses to display.</div>
              : <div className="status-list">{Object.entries(counts).map(([status, count]) => <div key={status}><Status value={status} /><strong>{count}</strong></div>)}</div>}</section>
          <section className="panel"><p className="eyebrow">SYSTEM BOUNDARY</p><h2>Operational state</h2>
            <p className="muted">This dashboard creates Draft tasks and supports the fixed health check and explicitly authorized disposable-repository edit. General task dispatch, Worker execution and deployment remain disabled.</p>
            <div className="boundary"><span>API health</span><strong>{health.error ? 'Unavailable' : health.data ? (connected ? 'Responding' : 'Unexpected response') : 'Checking'}</strong></div>
            <div className="boundary"><span>Task creation</span><strong>Draft only</strong></div>
            <div className="boundary"><span>Recovery (R3 / R4)</span><strong>Pending</strong></div>
            <div className="boundary"><span>General task approval</span><strong>Not implemented</strong></div></section></div>
        <section className="panel"><div className="panel-heading"><div><p className="eyebrow">LATEST ACTIVITY</p><h2>Recent tasks</h2></div><button className="text-button" onClick={() => setPage('Tasks')}>View all →</button></div>
          <State resource={tasks} loading={loading} empty="No tasks yet.">{data => <TaskTable tasks={data.slice(0, 5)} onSelect={selectTask} />}</State></section>
      </>}
      {page === 'Tasks' && <>{selected && <Detail id={selected} onClose={() => setSelected(null)} />}<section className="panel">
        <div className="panel-heading"><div><p className="eyebrow">CONTROL PLANE</p><h2>All tasks</h2></div><span className="outline-badge">{tasks.data ? `${tasks.data.length} TASKS` : 'UNAVAILABLE'}</span></div>
        <State resource={tasks} loading={loading} empty="No tasks yet.">{data => <TaskTable tasks={data} onSelect={selectTask} />}</State></section></>}
      {page === 'Plans' && <section className="panel"><div className="panel-heading"><div><p className="eyebrow">CONTROL PLANE</p><h2>Execution plans</h2></div><span className="outline-badge">READ ONLY</span></div>
        <p className="muted small">Task relationships and dependency graphs are not exposed by the current plans API.</p>
        <State resource={plans} loading={loading} empty="No plans yet.">{data => <div className="plan-list">{data.map(plan => <article className="plan" key={plan.id}>
          <div className="plan-top"><div><h3>{plan.requestTitle}</h3><span className="identifier">{plan.id}</span></div><Status value={plan.status} /></div>
          <p className="preserve">{plan.planSummary}</p><div className="plan-meta"><span>Project: {plan.projectKey ?? 'Not provided'}</span><span>Created: {displayDate(plan.createdAtUtc)}</span></div>
          {plan.requestDescription && <p className="muted preserve">{plan.requestDescription}</p>}</article>)}</div>}</State></section>}
      {['Agents', 'Models & Providers', 'Approvals'].includes(page) && <Planned page={page} />}
      <footer>HIC LOCAL DASHBOARD <span>·</span> CONTROL PLANE IS THE SOURCE OF TRUTH</footer>
      </div>
    </main>
  </div>;
}
