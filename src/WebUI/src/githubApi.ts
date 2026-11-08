import { localRunnerRequest } from './api';

export interface GitHubDestination { id: number; owner: string; name: string; baseBranch: string }
export interface GitHubPublicationPlan {
  publicationId: string; actorId: string; taskId: string | null; runId: string | null;
  conversationId: string | null; kind: 'task' | 'bootstrap'; destination: GitHubDestination;
  baseCommit: string | null; baseTree: string | null; taskBranch: string; file: string; content: string;
  contentDigest: string; codePlanDigest: string | null; credentialScopeDigest: string; expiresAtUtc: string;
}
export interface GitHubPublication {
  publicationId: string; runId: string | null; state: string; plan: GitHubPublicationPlan;
  intentDigestSha256: string; commitSha: string | null; remoteSha: string | null;
  prNumber: number | null; prUrl: string | null; errorCode: string | null; updatedAtUtc: string;
  events: { eventCode: string; recordedAtUtc: string }[];
}
export interface GitHubAppStatus {
  configured: boolean; repository: string; missing: string[];
  appId?: string; installationId?: string; runtimeRevision?: string;
}
const object = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v);
const str = (v: unknown): v is string => typeof v === 'string';
const nullable = (v: unknown): v is string | null => v === null || str(v);
const guid = (v: unknown): v is string => str(v) && /^[0-9a-f-]{36}$/.test(v);
const sha = (v: unknown): v is string => str(v) && /^[0-9a-f]{40}$/.test(v);
const hash = (v: unknown): v is string => str(v) && /^[0-9a-f]{64}$/.test(v);
const date = (v: unknown): v is string => str(v) && !Number.isNaN(Date.parse(v));
const nullableSha = (v: unknown) => v === null || sha(v);
const destination = (v: unknown): v is GitHubDestination => object(v) && Number.isSafeInteger(v.id) &&
  Number(v.id) > 0 && str(v.owner) && /^[A-Za-z0-9-]{1,39}$/.test(v.owner) &&
  str(v.name) && /^[A-Za-z0-9_.-]{1,100}$/.test(v.name) && str(v.baseBranch);
const plan = (v: unknown): v is GitHubPublicationPlan => object(v) && guid(v.publicationId) && guid(v.actorId) &&
  (v.taskId === null || guid(v.taskId)) && (v.runId === null || guid(v.runId)) &&
  (v.conversationId === null || guid(v.conversationId)) && ['task', 'bootstrap'].includes(String(v.kind)) &&
  destination(v.destination) && nullableSha(v.baseCommit) && nullableSha(v.baseTree) &&
  str(v.taskBranch) && str(v.file) && str(v.content) && v.content.length <= 8192 &&
  hash(v.contentDigest) && (v.codePlanDigest === null || hash(v.codePlanDigest)) &&
  hash(v.credentialScopeDigest) && date(v.expiresAtUtc);
export const publication = (v: unknown): v is GitHubPublication => object(v) && guid(v.publicationId) &&
  (v.runId === null || guid(v.runId)) && str(v.state) &&
  ['preview', 'pending', 'blob-intent', 'tree-intent', 'commit-intent', 'commit-created', 'ref-intent',
    'pushed', 'pr-intent', 'succeeded', 'unknown', 'blocked'].includes(v.state) && plan(v.plan) &&
  hash(v.intentDigestSha256) && nullableSha(v.commitSha) && nullableSha(v.remoteSha) &&
  (v.prNumber === null || (Number.isSafeInteger(v.prNumber) && Number(v.prNumber) > 0)) &&
  nullable(v.prUrl) && (v.prUrl === null ||
    v.prUrl === `https://github.com/${v.plan.destination.owner}/${v.plan.destination.name}/pull/${v.prNumber}`) &&
  nullable(v.errorCode) && date(v.updatedAtUtc) && Array.isArray(v.events) &&
  v.events.every(e => object(e) && str(e.eventCode) && date(e.recordedAtUtc));
const status = (v: unknown): v is GitHubAppStatus => object(v) && typeof v.configured === 'boolean' &&
  str(v.repository) && Array.isArray(v.missing) && v.missing.every(str) &&
  (v.appId === undefined || str(v.appId)) && (v.installationId === undefined || str(v.installationId)) &&
  (v.runtimeRevision === undefined || str(v.runtimeRevision));
const base = '/github';
export const githubApi = {
  status: (token: string) => localRunnerRequest(`${base}/status`, status, token, 'GET'),
  bootstraps: (token: string) => localRunnerRequest(`${base}/bootstraps`,
    (v): v is GitHubPublication[] => Array.isArray(v) && v.every(publication), token, 'GET'),
  preview: (runId: string, token: string) =>
    localRunnerRequest(`${base}/runs/${runId}/preview`, publication, token, 'POST', undefined, 200, 180000),
  bootstrapPreview: (token: string) =>
    localRunnerRequest(`${base}/bootstrap-preview`, publication, token, 'POST', undefined, 200, 180000),
  read: (id: string, token: string) =>
    localRunnerRequest(`${base}/publications/${id}`, publication, token, 'GET'),
  approve: (value: GitHubPublication, token: string) =>
    localRunnerRequest(`${base}/publications/${value.publicationId}/approve`, publication, token, 'POST',
      { intentDigestSha256: value.intentDigestSha256 }, 200, 180000),
  reject: (value: GitHubPublication, token: string) =>
    localRunnerRequest(`${base}/publications/${value.publicationId}/reject`, publication, token, 'POST',
      { intentDigestSha256: value.intentDigestSha256 }),
  execute: (id: string, token: string) =>
    localRunnerRequest(`${base}/publications/${id}/execute`, publication, token, 'POST', undefined, 200, 180000),
  reconcile: (id: string, token: string) =>
    localRunnerRequest(`${base}/publications/${id}/reconcile`, publication, token, 'POST', undefined, 200, 180000),
  completeDraft: (id: string, token: string) =>
    localRunnerRequest(`${base}/publications/${id}/complete-draft`, publication, token, 'POST', undefined, 200, 180000),
};
