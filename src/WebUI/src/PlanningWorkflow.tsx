import { useMemo, useState } from 'react';
import { api, type NativeWorkspaceInspection } from './api';
import { DESCRIPTION_MAX_LENGTH, TITLE_MAX_LENGTH } from './limits';

type PreviewStep = {
  id: string;
  title: string;
  agent: string;
  dependsOn: string[];
  availability: string;
};

type PlanPreview = {
  version: number;
  hash: string;
  title: string;
  description: string;
  repository: string;
  steps: PreviewStep[];
  risks: string[];
  acceptanceCriteria: string[];
  sourceSignature: string;
};

type PreviewEvent = {
  action: 'preview.created' | 'preview.revised';
  version: number;
  hash: string;
  recordedAtUtc: string;
};

const REPOSITORY_ID = 'configured-workspace';

function signature(title: string, description: string, repository: string) {
  return JSON.stringify([title.trim(), description.trim(), repository]);
}

async function sha256(value: string) {
  const digest = await globalThis.crypto.subtle.digest('SHA-256', new TextEncoder().encode(value));
  return `sha256:${Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, '0')).join('')}`;
}

function makePlanBody(
  version: number,
  title: string,
  description: string,
  repository: string,
) {
  return {
    version,
    title: title.trim(),
    description: description.trim(),
    repository,
    steps: [
      {
        id: 'scope',
        title: 'Review the request and inspect the selected repository metadata.',
        agent: 'HIC deterministic planner',
        dependsOn: [],
        availability: 'Available for read-only plan preview only',
      },
      {
        id: 'implementation',
        title: 'Propose implementation changes for human review.',
        agent: 'Implementation agent',
        dependsOn: ['scope'],
        availability: 'Unavailable; Agent Worker is not part of this preview',
      },
      {
        id: 'validation',
        title: 'Define and run the approved validation checks.',
        agent: 'Validation agent',
        dependsOn: ['implementation'],
        availability: 'Unavailable; task execution is disabled',
      },
    ] satisfies PreviewStep[],
    risks: [
      'This deterministic preview does not check model availability; consult Models & Providers.',
      'This general plan preview has no authenticated approval or durable audit. Local Runner approval covers only its fixed action.',
      'Repository inspection is limited to the single Native Host configured workspace.',
    ],
    acceptanceCriteria: [
      `The proposed work addresses: ${title.trim()}`,
      'Changes remain scoped to the selected repository and are reviewed by a human.',
      'Relevant build and test checks are reported before any later execution is considered.',
      'No merge or deployment occurs without a separate human approval.',
    ],
  };
}

export function PlanningWorkflow() {
  const [inspection, setInspection] = useState<NativeWorkspaceInspection | null>(null);
  const [inspectionError, setInspectionError] = useState<string | null>(null);
  const [inspecting, setInspecting] = useState(false);
  const [repository, setRepository] = useState('');
  const [title, setTitle] = useState('');
  const [description, setDescription] = useState('');
  const [preview, setPreview] = useState<PlanPreview | null>(null);
  const [events, setEvents] = useState<PreviewEvent[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [generating, setGenerating] = useState(false);

  const currentSignature = useMemo(
    () => signature(title, description, repository),
    [title, description, repository],
  );
  const hasAuthorizedRepository = Boolean(
    inspection?.workspaceAvailable && inspection.gitCheckoutFound && repository === REPOSITORY_ID,
  );
  const previewStale = preview !== null && preview.sourceSignature !== currentSignature;

  async function inspectRepository() {
    setInspecting(true);
    setInspectionError(null);
    setInspection(null);
    setRepository('');
    try {
      const result = await api.inspectNativeWorkspace();
      setInspection(result);
      if (result.workspaceAvailable && result.gitCheckoutFound) setRepository(REPOSITORY_ID);
      else setInspectionError('The configured workspace is not an available Git checkout.');
    } catch {
      setInspectionError('The Native Host did not confirm an authorized repository. No repository was selected.');
    } finally {
      setInspecting(false);
    }
  }

  async function generatePreview() {
    if (!hasAuthorizedRepository || !title.trim() || !description.trim() ||
        title.length > TITLE_MAX_LENGTH || description.length > DESCRIPTION_MAX_LENGTH) return;

    setGenerating(true);
    setError(null);
    try {
      const version = (preview?.version ?? 0) + 1;
      const body = makePlanBody(version, title, description, repository);
      const hash = await sha256(JSON.stringify(body));
      const next: PlanPreview = { ...body, hash, sourceSignature: currentSignature };
      setPreview(next);
      setEvents(previous => [...previous, {
        action: previous.length ? 'preview.revised' : 'preview.created',
        version,
        hash,
        recordedAtUtc: new Date().toISOString(),
      }]);
    } catch {
      setError('A cryptographic plan hash could not be calculated in this browser.');
    } finally {
      setGenerating(false);
    }
  }

  return <section className="planning-workflow" aria-labelledby="planning-title">
    <section className="panel command-panel">
      <div className="panel-heading">
        <div><p className="eyebrow">HUMAN-CONTROLLED PLANNING</p><h2 id="planning-title">Prepare a task plan</h2></div>
        <span className="outline-badge">PREVIEW ONLY · NO EXECUTION</span>
      </div>
      <p className="muted">Inspect the Native Host's one configured repository, describe a task, and preview a versioned plan.
        Planning is deterministic and local; no model is called and no task or repository is modified.</p>

      <div className="panel-heading">
        <div><p className="eyebrow">REPOSITORY CATALOG</p><h3>Authorized local repository</h3></div>
        <button className="secondary" type="button" disabled={inspecting} onClick={() => void inspectRepository()}>
          {inspecting ? 'Inspecting…' : 'Discover configured repository'}
        </button>
      </div>
      {inspectionError && <p className="notice error" role="alert">{inspectionError}</p>}
      {inspection && <label className="field-label" htmlFor="planning-repository">
        Repository
        <select id="planning-repository" className="command-title" value={repository}
          onChange={event => setRepository(event.target.value)}>
          {inspection.workspaceAvailable && inspection.gitCheckoutFound
            ? <option value={REPOSITORY_ID}>Configured local workspace (read-only)</option>
            : <option value="">No authorized repository available</option>}
        </select>
      </label>}
      {inspection && <p className="muted small" role="status">
        Workspace {inspection.workspaceAvailable ? 'found' : 'not found'} · Git checkout {inspection.gitCheckoutFound ? 'found' : 'not found'} ·
        solution {inspection.solutionFound ? 'found' : 'not found'}. The Native Host does not expose a path or inspect other repositories.
      </p>}

      <div className="command-form">
        <label className="field-label" htmlFor="planning-title-input">Task title <span className="required">required</span></label>
        <input id="planning-title-input" className="command-title" maxLength={TITLE_MAX_LENGTH}
          value={title} onChange={event => setTitle(event.target.value)} />
        <label className="field-label" htmlFor="planning-description">Task description <span className="required">required</span></label>
        <textarea id="planning-description" className="command-input" rows={6} maxLength={DESCRIPTION_MAX_LENGTH}
          value={description} onChange={event => setDescription(event.target.value)}
          placeholder="Describe the requested software change. Do not include secrets." />
        <div className="form-actions">
          <button type="button" className="primary" disabled={!hasAuthorizedRepository || !title.trim() ||
            !description.trim() || generating} onClick={() => void generatePreview()}>
            {generating ? 'Preparing preview…' : 'Generate plan preview'}
          </button>
          <span className="muted small">Local only · no Control Plane write</span>
        </div>
      </div>
      {error && <p className="notice error" role="alert">{error}</p>}
    </section>

    <section className="panel" aria-labelledby="registry-title">
      <div className="panel-heading"><div><p className="eyebrow">AGENT CAPABILITY REGISTRY</p><h2 id="registry-title">Known capabilities</h2></div>
        <span className="outline-badge">NO PROVIDER MARKED READY</span></div>
      <div className="capability-row"><div><strong>HIC deterministic planner</strong><p className="muted small">Creates local plan previews and SHA-256 hashes; no LLM or code generation.</p></div><span className="status good">Available · local function</span></div>
      <div className="capability-row"><div><strong>Agent Worker</strong><p className="muted small">Task execution is not checked or started by this workflow.</p></div><span className="status">Not checked</span></div>
      {['Podman AI Lab', 'Ollama', 'OpenAI-compatible provider'].map(provider =>
        <div className="capability-row" key={provider}><div><strong>{provider}</strong><p className="muted small">No endpoint probe or external request is made by this preview.</p></div>
          <span className="status">Unverified</span></div>)}
    </section>

    {preview && <section className="panel" aria-labelledby="preview-title">
      <div className="panel-heading"><div><p className="eyebrow">VERSIONED EXECUTION PLAN</p><h2 id="preview-title">{preview.title}</h2></div>
        <span className={`outline-badge ${previewStale ? 'warning-badge' : ''}`}>VERSION {preview.version}{previewStale ? ' · OUTDATED' : ''}</span></div>
      <p className="muted small">Plan hash: <code className="plan-hash">{preview.hash}</code></p>
      {previewStale && <p className="notice warning" role="status">Inputs changed after this preview. Generate a new version; no approval exists for this plan.</p>}
      <div className="detail-block"><span className="label">Selected repository</span><p>Configured local workspace · read-only inspection</p></div>
      <div className="detail-block"><span className="label">Proposed agents and dependencies</span>
        {preview.steps.map(step => <div className="plan-step" key={step.id}>
          <strong>{step.id}. {step.title}</strong><span>Agent: {step.agent} · {step.availability}</span>
          <span>Depends on: {step.dependsOn.length ? step.dependsOn.join(', ') : 'None'}</span>
        </div>)}
      </div>
      <div className="detail-block"><span className="label">Acceptance criteria</span><ul>{preview.acceptanceCriteria.map(item => <li key={item}>{item}</li>)}</ul></div>
      <div className="detail-block"><span className="label">Risks and blockers</span><ul>{preview.risks.map(item => <li key={item}>{item}</li>)}</ul></div>
      <div className="approval-block" role="note">
        <strong>Approval unavailable</strong>
        <p className="muted small">This preview has no authenticated, plan-hash-bound approval receipt and cannot be executed. Local Runner approval does not authorize this general plan.</p>
        <button className="secondary" type="button" disabled>Approve plan</button>
      </div>
    </section>}

    <section className="panel" aria-labelledby="planning-audit-title">
      <div className="panel-heading"><div><p className="eyebrow">PREVIEW ACTIVITY</p><h2 id="planning-audit-title">Session-local events</h2></div>
        <span className="outline-badge">NOT DURABLE AUDIT</span></div>
      <p className="muted small">Only preview creation/revision, version, hash and timestamp are kept in page memory. Task text, credentials, approval and rejection decisions are not recorded.</p>
      {events.length
        ? <ul className="activity-list">{events.map(event => <li key={`${event.version}-${event.hash}`}>
          <strong>{event.action}</strong> · v{event.version} · <code className="plan-hash">{event.hash}</code> · {new Date(event.recordedAtUtc).toLocaleString()}
        </li>)}</ul>
        : <p className="notice">No preview activity in this page session.</p>}
    </section>
  </section>;
}
