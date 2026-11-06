import { useId, useRef, useState, type FormEvent, type KeyboardEvent } from 'react';
import { api, ApiError, type Task } from './api';
import { Status } from './components';
import { DESCRIPTION_MAX_LENGTH, TITLE_MAX_LENGTH, deriveTitle, validateCreateTask } from './limits';

const FALLBACK_UNKNOWN = 'The Control Plane did not confirm the request. The task may or may not have been created — check the Tasks list before submitting again.';

interface Props {
  hidden: boolean;
  onCreated: (task: Task) => void;
  onViewTask: (id: string) => void;
  onCheckTasks: () => Promise<boolean>;
}

export function CommandCenter({ hidden, onCreated, onViewTask, onCheckTasks }: Props) {
  const ids = useId();
  const [description, setDescription] = useState('');
  const [title, setTitle] = useState('');
  const [attempted, setAttempted] = useState(false);
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [uncertain, setUncertain] = useState(false);
  const [checking, setChecking] = useState(false);
  const [created, setCreated] = useState<Task | null>(null);
  const submitting = useRef(false);
  const requestRef = useRef<HTMLTextAreaElement>(null);

  const derived = deriveTitle(description);
  const effectiveTitle = title.trim() || derived;
  const descriptionError = !description.trim() ? 'Describe the request before submitting.'
    : description.length > DESCRIPTION_MAX_LENGTH ? `Request must be at most ${DESCRIPTION_MAX_LENGTH} characters.` : null;
  const titleError = title.length > TITLE_MAX_LENGTH ? `Title must be at most ${TITLE_MAX_LENGTH} characters.` : null;
  const showDescriptionError = descriptionError !== null && (attempted || description.length > DESCRIPTION_MAX_LENGTH);

  async function submit() {
    if (submitting.current || uncertain) return;
    setAttempted(true);
    const validation = validateCreateTask({ title: effectiveTitle, description });
    if (descriptionError || titleError || !validation.ok) {
      requestRef.current?.focus();
      return;
    }
    submitting.current = true;
    setPending(true);
    setError(null);
    try {
      const task = await api.createTask(validation.value);
      setCreated(task);
      setDescription('');
      setTitle('');
      setAttempted(false);
      onCreated(task);
    } catch (reason) {
      const failure = reason instanceof ApiError ? reason : new ApiError('network', FALLBACK_UNKNOWN, undefined, true);
      setError(failure.message);
      setUncertain(failure.outcomeUnknown);
    } finally {
      submitting.current = false;
      setPending(false);
    }
  }

  async function checkTasks() {
    setChecking(true);
    try {
      if (await onCheckTasks()) {
        setUncertain(false);
        setError(null);
      }
    } finally {
      setChecking(false);
    }
  }

  const onFormSubmit = (event: FormEvent) => { event.preventDefault(); void submit(); };
  const submitShortcut = (event: KeyboardEvent) => {
    if (event.key !== 'Enter') return;
    if (event.ctrlKey || event.metaKey) {
      event.preventDefault();
      void submit();
    } else if (event.currentTarget instanceof HTMLInputElement) {
      // Prevent implicit form submission from the single-line title field.
      event.preventDefault();
    }
  };

  return <div className="command-center" hidden={hidden}>
    <section className="panel command-panel" aria-labelledby={`${ids}-heading`}>
      <div className="panel-heading"><div><p className="eyebrow">NEW TASK</p><h2 id={`${ids}-heading`}>What should be done?</h2></div>
        <span className="outline-badge">CREATES DRAFT ONLY</span></div>
      <p className="muted">Describe the work in natural language. Submitting saves a <strong>Draft</strong> task in the Control Plane.
        Nothing is approved, dispatched or executed, no model is called and no command is run.</p>
      <form className="command-form" onSubmit={onFormSubmit} noValidate aria-busy={pending}>
        <label className="field-label" htmlFor={`${ids}-request`}>Request <span className="required">required</span></label>
        <textarea id={`${ids}-request`} ref={requestRef} className="command-input" rows={8} value={description}
          placeholder="e.g. Add input validation to the checkout form and cover it with unit tests."
          aria-invalid={showDescriptionError} aria-describedby={`${ids}-request-help ${ids}-request-count${showDescriptionError ? ` ${ids}-request-error` : ''}`}
          onChange={event => setDescription(event.target.value)} onKeyDown={submitShortcut} disabled={pending} />
        <div className="field-meta">
          <span id={`${ids}-request-help`}>Enter adds a new line. Press Ctrl+Enter (⌘+Enter on macOS) or use the button to submit.</span>
          <span id={`${ids}-request-count`} className={description.length > DESCRIPTION_MAX_LENGTH ? 'over-limit' : undefined}>
            {description.length.toLocaleString()} / {DESCRIPTION_MAX_LENGTH.toLocaleString()}</span>
        </div>
        {showDescriptionError && <p id={`${ids}-request-error`} className="field-error">{descriptionError}</p>}

        <label className="field-label" htmlFor={`${ids}-title`}>Title <span className="optional">optional</span></label>
        <input id={`${ids}-title`} className="command-title" type="text" value={title} autoComplete="off"
          placeholder={derived || 'Derived from the first line of the request'}
          aria-invalid={titleError !== null} aria-describedby={`${ids}-title-help${titleError ? ` ${ids}-title-error` : ''}`}
          onChange={event => setTitle(event.target.value)} onKeyDown={submitShortcut} disabled={pending} />
        <p id={`${ids}-title-help`} className="field-meta">{title.trim() ? `${title.length} / ${TITLE_MAX_LENGTH}`
          : derived ? `Leave blank to use: “${derived}”` : 'Leave blank to derive a title from the request. The full request is kept as the description.'}</p>
        {titleError && <p id={`${ids}-title-error`} className="field-error">{titleError}</p>}

        <div className="form-actions">
          <button type="submit" className="primary" disabled={pending || uncertain}>{pending ? 'Submitting…' : 'Create Draft task'}</button>
          {pending && <span role="status" className="muted small">Sending once to the Control Plane…</span>}
        </div>
      </form>
      {error && <div className={`notice ${uncertain ? 'warning' : 'error'}`} role="alert">
        <p className="notice-text">{error}</p>
        {uncertain && <div className="notice-actions">
          <button type="button" className="secondary" onClick={() => void checkTasks()} disabled={checking}>
            {checking ? 'Checking…' : 'Check Tasks list'}</button>
          <span className="muted small">Submission stays disabled until the task list is refreshed, to avoid duplicate tasks.</span>
        </div>}
      </div>}
    </section>
    {created && <section className="panel created" role="status" aria-labelledby={`${ids}-created`}>
      <div className="panel-heading"><div><p className="eyebrow">TASK SAVED</p><h2 id={`${ids}-created`}>Created as Draft — not executed</h2></div>
        <Status value={created.status} /></div>
      <div className="detail-grid">
        <div><span className="label">Title</span>{created.title}</div>
        <div><span className="label">Identifier</span><span className="mono">{created.id}</span></div>
        <div><span className="label">Status</span>{created.status}</div>
        <div><span className="label">Attempts</span>{created.attemptCount}</div>
      </div>
      <p className="muted small">No approval, dispatch or agent execution was triggered. The task stays in Draft until a separate, explicit approval step exists.</p>
      <div className="form-actions">
        <button type="button" className="primary" onClick={() => onViewTask(created.id)}>View task</button>
        <button type="button" className="secondary" onClick={() => { setCreated(null); requestRef.current?.focus(); }}>Dismiss</button>
      </div>
    </section>}
  </div>;
}
