// HIC developer-only review envelope verifier. No network, disk writes, commands,
// SDK sessions, Git changes, HIC state transitions, or execution authority.
import { createPublicKey, verify as verifySignature } from 'node:crypto';
import { REPO } from './relay.mjs';

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
const SHA = /^[0-9a-f]{40}$/;
const DIGEST = /^[0-9a-f]{64}$/;
const KEY_ID = /^[a-z][a-z0-9-]{2,63}$/;
const BRANCH = /^feature\/[a-z0-9-]+(?:\/[a-z0-9-]+)*$/;
const PATH = /^tools\/dev-handoff\/(?:[a-z0-9-]+\/)*[A-Za-z0-9.-]+\.(?:md|mjs|json)$/;
const UTC = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{3})?Z$/;
const MAX_ENVELOPE_BYTES = 8192;
const MAX_PAYLOAD_BYTES = 4096;
const DOMAIN = 'HIC-DEV-REVIEWED-WORK-ORDER-V1\n';

const fail = code => { throw new Error(code); };
const record = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const exact = (value, fields) => record(value) && Object.keys(value).sort().join('|') === fields.slice().sort().join('|');
const inRange = (value, min, max) => Number.isSafeInteger(value) && value >= min && value <= max;
const timestamp = value => {
  if (typeof value !== 'string' || !UTC.test(value)) fail('invalid-time');
  const n = Date.parse(value);
  if (!Number.isFinite(n) || new Date(n).toISOString() !== (value.includes('.') ? new Date(n).toISOString() : value.replace('Z', '.000Z'))) {
    // Normalization avoids accepting impossible calendar dates such as February 30.
    fail('invalid-time');
  }
  return n;
};
const asciiText = (s, max) => typeof s === 'string' && s.length > 0 && s.length <= max && !/[\u0000-\u001f\u007f]/.test(s);
const base64url = (s, maxBytes) => {
  if (typeof s !== 'string' || s.length > Math.ceil(maxBytes * 4 / 3) + 2 || !/^[A-Za-z0-9_-]+$/.test(s)) fail('invalid-base64url');
  const bytes = Buffer.from(s, 'base64url');
  if (bytes.length < 1 || bytes.length > maxBytes || bytes.toString('base64url') !== s) fail('invalid-base64url');
  return bytes;
};

// Canonical serialized payload; signed bytes must equal this representation.
// Using strict fixed field validation below prevents a model from smuggling extra fields.
export function canonicalJson(value) {
  if (Array.isArray(value)) return '[' + value.map(canonicalJson).join(',') + ']';
  if (record(value)) return '{' + Object.keys(value).sort().map(k => JSON.stringify(k) + ':' + canonicalJson(value[k])).join(',') + '}';
  if (value === null || typeof value === 'string' || typeof value === 'boolean' || (typeof value === 'number' && Number.isFinite(value))) return JSON.stringify(value);
  fail('invalid-json-value');
}

function validatePayload(p, now) {
  if (!exact(p, ['version', 'kind', 'orderId', 'createdAt', 'expiresAt', 'source', 'task', 'review', 'limits'])) fail('invalid-payload');
  if (p.version !== 'hic.dev-reviewed-work-order/1' || p.kind !== 'ReviewedDevWorkOrder' || !UUID.test(p.orderId)) fail('invalid-payload');
  const created = timestamp(p.createdAt), expires = timestamp(p.expiresAt);
  if (created > now + 60000 || expires <= now || expires <= created || expires - created > 24 * 3600000) fail('invalid-lifetime');
  const s = p.source;
  if (!exact(s, ['repository', 'transportRef', 'queueIssue', 'issueNumber', 'reviewedGitSha']) || s.repository !== REPO || s.transportRef !== 'hic-dev-orders' || s.queueIssue !== 75 || !inRange(s.issueNumber, 1, 1000000) || !SHA.test(s.reviewedGitSha)) fail('invalid-source');
  const t = p.task;
  if (!exact(t, ['taskId', 'repository', 'branch', 'baseSha', 'allowedPaths', 'objective', 'requiresHumanApproval']) || !UUID.test(t.taskId) || t.repository !== REPO || !BRANCH.test(t.branch) || t.branch.length > 120 || !SHA.test(t.baseSha) || !asciiText(t.objective, 400) || t.requiresHumanApproval !== true) fail('invalid-task');
  if (!Array.isArray(t.allowedPaths) || t.allowedPaths.length < 1 || t.allowedPaths.length > 8 || t.allowedPaths.some(path => typeof path !== 'string' || !PATH.test(path) || path.includes('//') || path.includes('..')) || new Set(t.allowedPaths).size !== t.allowedPaths.length) fail('invalid-paths');
  const rev = p.review;
  if (!exact(rev, ['reviewId', 'reviewerId', 'reviewedAt', 'revisionDigest', 'status']) || !UUID.test(rev.reviewId) || !asciiText(rev.reviewerId, 64) || !/^[a-zA-Z0-9_-]+$/.test(rev.reviewerId) || !DIGEST.test(rev.revisionDigest) || rev.status !== 'reviewed') fail('invalid-review');
  const reviewed = timestamp(rev.reviewedAt);
  if (reviewed < created || reviewed > now + 60000 || reviewed >= expires) fail('invalid-review-time');
  const lm = p.limits;
  // Intentionally non-executable v1; widening requires an independently reviewed new protocol version.
  if (!exact(lm, ['executionAllowed', 'allowedTools', 'maxModelCostUsd', 'maxExecutionSeconds', 'allowNetwork', 'allowGitWrite']) || lm.executionAllowed !== false || !Array.isArray(lm.allowedTools) || lm.allowedTools.length !== 0 || lm.maxModelCostUsd !== 0 || lm.maxExecutionSeconds !== 0 || lm.allowNetwork !== false || lm.allowGitWrite !== false) fail('execution-denied-v1');
  return {orderId:p.orderId, taskId:t.taskId, baseSha:t.baseSha, branch:t.branch, reviewedGitSha:s.reviewedGitSha, reviewId:rev.reviewId, reviewerId:rev.reviewerId, allowedPaths:[...t.allowedPaths]};
}

// trustedKeys is an operator-provisioned *in-memory* map, never sourced from the
// same GitHub order. Identity/authorization must be established out of band.
export function verifyReviewedEnvelope(envelopeBytes, {trustedKeys, now=Date.now(), expectedGitSha}={}) {
  if (!Buffer.isBuffer(envelopeBytes) || envelopeBytes.length === 0 || envelopeBytes.length > MAX_ENVELOPE_BYTES) fail('invalid-envelope-bytes');
  let e; try {e = JSON.parse(envelopeBytes.toString('utf8'));} catch {fail('invalid-envelope-json');}
  if (!exact(e, ['version', 'kind', 'algorithm', 'keyId', 'payload', 'signature']) || e.version !== 'hic.dev-review-envelope/1' || e.kind !== 'SignedReviewedDevWorkOrder' || e.algorithm !== 'Ed25519' || !KEY_ID.test(e.keyId)) fail('invalid-envelope');
  // Don't trust or parse a GitHub-supplied public key or reviewer registry.
  const key = trustedKeys instanceof Map ? trustedKeys.get(e.keyId) : undefined;
  if (!record(key) || typeof key.publicKeyPem !== 'string' || typeof key.reviewerId !== 'string' || key.revoked !== false || key.authorized !== true) fail('untrusted-reviewer-key');
  const bytes = base64url(e.payload, MAX_PAYLOAD_BYTES), signature = base64url(e.signature, 128);
  if (signature.length !== 64) fail('invalid-signature');
  let p; try {p = JSON.parse(bytes.toString('utf8'));} catch {fail('invalid-payload-json');}
  if (Buffer.from(canonicalJson(p), 'utf8').equals(bytes) === false) fail('noncanonical-payload');
  const reviewed = validatePayload(p, now);
  if (reviewed.reviewerId !== key.reviewerId) fail('reviewer-mismatch');
  if (!SHA.test(expectedGitSha) || reviewed.reviewedGitSha !== expectedGitSha) fail('github-sha-not-verified');
  const signed = Buffer.concat([Buffer.from(DOMAIN + e.keyId + '\n'), bytes]);
  let ok = false;
  try {
    const publicKey = createPublicKey(key.publicKeyPem);
    if (publicKey.asymmetricKeyType !== 'ed25519') fail('invalid-public-key');
    ok = verifySignature(null, signed, publicKey, signature);
  } catch {fail('invalid-public-key');}
  if (!ok) fail('invalid-signature');
  return Object.freeze({status:'accepted-for-review', executionAllowed:false, ledgerVerified:false, signerKeyId:e.keyId, ...reviewed});
}

// Only for local disposable test fixtures; NOT a signer/provisioning API.
export const SIGNING_CONTEXT_FOR_TESTS = DOMAIN;
