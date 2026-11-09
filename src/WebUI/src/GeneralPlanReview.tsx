import { useEffect, useState } from 'react';
import { api, ApiError, type GeneralPlan, type GeneralPlanTask } from './api';

export function GeneralPlanReview({ conversationId, token, hasMessages, onTasksCreated, onViewTask }: {
  conversationId: string; token: string; hasMessages: boolean;
  onTasksCreated: () => void; onViewTask: (id: string) => void;
}) {
  const [plan, setPlan] = useState<GeneralPlan | null>(null);
  const [edits, setEdits] = useState<GeneralPlanTask[]>([]);
  const [reviewed, setReviewed] = useState(false);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [reload, setReload] = useState(0);

  function install(value: GeneralPlan) {
    setPlan(value);
    setEdits(value.tasks.map(task => ({ ...task })));
    setReviewed(false);
  }
  useEffect(() => {
    let active = true;
    setPlan(null); setEdits([]); setReviewed(false); setError(null); setLoading(true);
    void api.generalPlan(conversationId, token).then(value => {
      if (active) install(value);
    }).catch(reason => {
      if (active && !(reason instanceof ApiError && reason.status === 404))
        setError(reason instanceof Error ? reason.message : 'Plan is unavailable.');
    }).finally(() => { if (active) setLoading(false); });
    return () => { active = false; };
  }, [conversationId, token, reload]);

  const dirty = plan !== null && JSON.stringify(edits) !== JSON.stringify(plan.tasks);
  const valid = edits.length > 0 && edits.length <= 8 && edits.every(task =>
    task.title.trim().length > 0 && task.title.length <= 120 &&
    task.description.trim().length > 0 && task.description.length <= 1000);
  async function perform(action: () => Promise<GeneralPlan>, after?: () => void) {
    setBusy(true); setError(null); setReviewed(false);
    try { install(await action()); after?.(); }
    catch (reason) { setError(reason instanceof Error ? reason.message : 'Plan request failed.'); }
    finally { setBusy(false); }
  }

  return <section className="detail-block" aria-label="General conversation plan">
    <h3>Model task plan</h3>
    <p className="muted small">Plan text is untrusted. Review and save the exact revision before creating Draft tasks.
      Draft creation does not approve execution, tools, GitHub publication or deployment.</p>
    {loading && <p role="status">Loading plan…</p>}
    {error && <p role="alert" className="notice error">{error} Reload the current plan before retrying an interrupted write.
      {' '}<button className="text-button" onClick={() => setReload(value => value + 1)}>Reload plan</button></p>}
    {!loading && !error && <>
      {!plan && <p className="notice">No model plan yet.</p>}
      {plan && <>
        <p>Revision {plan.revision} · {plan.source} · {plan.approved ? 'Draft tasks created' : plan.rejected ? 'Rejected' : 'Not approved'}</p>
        {edits.map((task, index) => <div className="command-form" key={index}>
          <label htmlFor={`plan-title-${index}`}>Task {index + 1} title</label>
          <input id={`plan-title-${index}`} className="command-title" maxLength={120} value={task.title}
            disabled={busy || plan.approved} onChange={event => {
              const value = event.target.value;
              setEdits(items => items.map((item, i) => i === index ? { ...item, title: value } : item));
              setReviewed(false);
            }} />
          <label htmlFor={`plan-description-${index}`}>Task {index + 1} description</label>
          <textarea id={`plan-description-${index}`} className="command-input" maxLength={1000} value={task.description}
            disabled={busy || plan.approved} onChange={event => {
              const value = event.target.value;
              setEdits(items => items.map((item, i) => i === index ? { ...item, description: value } : item));
              setReviewed(false);
            }} />
          {!plan.approved && <button className="text-button" disabled={busy || edits.length <= 1}
            onClick={() => { setEdits(items => items.filter((_, i) => i !== index)); setReviewed(false); }}>
            Remove task {index + 1}
          </button>}
        </div>)}
        {!plan.approved && <>
          <button className="secondary" disabled={busy || edits.length >= 8}
            onClick={() => { setEdits(items => [...items, { title: '', description: '' }]); setReviewed(false); }}>
            Add task
          </button>
          <button className="secondary" disabled={busy || !dirty || !valid}
            onClick={() => void perform(() => api.reviseGeneralPlan(conversationId, plan.revision, edits, token))}>
            Save edited revision
          </button>
          <label><input type="checkbox" checked={reviewed} disabled={busy || dirty || plan.rejected}
            onChange={event => setReviewed(event.target.checked)} /> I reviewed this exact saved revision.</label>
          <button className="primary" disabled={busy || dirty || !reviewed || plan.rejected}
            onClick={() => void perform(() => api.approveGeneralPlan(conversationId, plan.revision, token), onTasksCreated)}>
            Approve Draft task creation
          </button>
          <button className="secondary" disabled={busy || dirty || plan.rejected}
            onClick={() => void perform(() => api.rejectGeneralPlan(conversationId, plan.revision, token))}>
            Reject saved revision
          </button>
        </>}
        {plan.approved && plan.taskIds.map(id => <button key={id} className="task-link" onClick={() => onViewTask(id)}>
          Open Draft task <span className="identifier">{id}</span>
        </button>)}
      </>}
      {!plan?.approved && <button className="secondary" disabled={busy || dirty || !hasMessages}
        onClick={() => void perform(() => api.generateGeneralPlan(conversationId, plan?.revision ?? 0, token))}>
        {busy ? 'Working…' : 'Generate model plan'}
      </button>}
    </>}
  </section>;
}
