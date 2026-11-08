import { localRunnerRequest, type LocalHealthRunEvent } from './api';

export interface GitRepository { id: string; path: string; baseCommit: string; available: true }
export interface GitPlan {
  revision: number; taskId: string; actorId: string; taskDigest: string;
  repositoryId: string; repositoryPath: string; baseCommit: string;
  file: 'message.txt'; content: string; expiresAtUtc: string;
}
export interface GitPreview { plan: GitPlan; intentDigestSha256: string }
export interface GitResult {
  action: 'repository.test-edit'; runId: string; repositoryId: string | null; baseCommit: string | null;
  worktreePath: string | null; branch: string | null; diff: string;
  testCode: string; sourceUnchanged: boolean; checkedAtUtc: string;
}
export interface GitRun {
  runId: string; taskId: string; action: 'repository.test-edit'; scope: string;
  intentDigestSha256: string; status: string; plan: GitPlan; result: GitResult | null;
  events: LocalHealthRunEvent[];
}
const object = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v);
const str = (v: unknown): v is string => typeof v === 'string';
const hash = (v: unknown): v is string => str(v) && /^[0-9a-f]{64}$/.test(v);
const sha = (v: unknown): v is string => str(v) && /^[0-9a-f]{40}$/.test(v);
const guid = (v: unknown): v is string => str(v) && /^[0-9a-f-]{36}$/.test(v);
const date = (v: unknown): v is string => str(v) && !Number.isNaN(Date.parse(v));
const plan = (v: unknown): v is GitPlan => object(v) && v.revision === 1 && guid(v.taskId) &&
  guid(v.actorId) && hash(v.taskDigest) && str(v.repositoryId) && str(v.repositoryPath) &&
  sha(v.baseCommit) && v.file === 'message.txt' && str(v.content) && v.content.length <= 512 && date(v.expiresAtUtc);
const preview = (v: unknown): v is GitPreview => object(v) && plan(v.plan) && hash(v.intentDigestSha256);
const repository = (v: unknown): v is GitRepository => object(v) && str(v.id) && str(v.path) && sha(v.baseCommit) && v.available === true;
const nullable = (v: unknown) => v === null || str(v);
const result = (v: unknown): v is GitResult => object(v) && v.action === 'repository.test-edit' &&
  guid(v.runId) && nullable(v.repositoryId) && nullable(v.baseCommit) && nullable(v.worktreePath) &&
  nullable(v.branch) && str(v.diff) && v.diff.length <= 4096 && str(v.testCode) &&
  typeof v.sourceUnchanged === 'boolean' && date(v.checkedAtUtc);
const run = (v: unknown): v is GitRun => object(v) && guid(v.runId) && guid(v.taskId) &&
  v.action === 'repository.test-edit' && v.scope === 'disposable-worktree:message.txt:write' &&
  hash(v.intentDigestSha256) && ['queued', 'running', 'completed', 'failed', 'expired', 'invalidated'].includes(String(v.status)) &&
  plan(v.plan) && (v.result === null || result(v.result)) && Array.isArray(v.events) &&
  v.events.every(e => object(e) && str(e.eventCode) && date(e.recordedAtUtc));
const approval = (v: unknown): v is { runId: string } => object(v) && guid(v.runId) && v.action === 'repository.test-edit';

export const gitApi = {
  repositories: (token: string) => localRunnerRequest('/repositories',
    (v): v is GitRepository[] => Array.isArray(v) && v.every(repository), token, 'GET'),
  preview: (taskId: string, repo: GitRepository, content: string, token: string) =>
    localRunnerRequest(`/tasks/${taskId}/git-preview`, preview, token, 'POST',
      { repositoryId: repo.id, baseCommit: repo.baseCommit, file: 'message.txt', content }),
  approve: (taskId: string, value: GitPreview, token: string) =>
    localRunnerRequest(`/tasks/${taskId}/git-approve`, approval, token, 'POST', value, 202),
  run: (runId: string, token: string) => localRunnerRequest(`/runs/${runId}`, run, token, 'GET'),
};
