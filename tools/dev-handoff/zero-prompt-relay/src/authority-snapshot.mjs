import { createHash, createPublicKey, verify } from 'node:crypto';
import { canonicalJson } from './reviewed-order.mjs';
import { REPO } from './relay.mjs';

export const AUTHORITY_ISSUER = 'hic-control-plane-dev-authority';
export const AUTHORITY_AUDIENCE = 'hic-dev-relay-review-only';
export const AUTHORITY_DOMAIN = 'HIC-DEV-AUTHORITY-SNAPSHOT-V1\n';
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
const KEY_ID = /^[a-z][a-z0-9-]{2,63}$/;
const DIGEST = /^[0-9a-f]{64}$/;
const fail = code => { throw new Error(code); };
const record = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const exact = (value, fields) => record(value) &&
  Object.keys(value).sort().join('|') === fields.slice().sort().join('|');
const positive = value => Number.isSafeInteger(value) && value > 0;
export const digest = value => createHash('sha256').update(value).digest('hex');

export function utcMillis(value) {
  if (typeof value !== 'string' || !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/.test(value)) fail('invalid-time');
  const time = Date.parse(value);
  if (!Number.isFinite(time) || new Date(time).toISOString() !== value) fail('invalid-time');
  return time;
}

export function publicKeyFingerprint(publicKeyPem) {
  const key = createPublicKey(publicKeyPem);
  if (key.asymmetricKeyType !== 'ed25519') fail('untrusted-key');
  return digest(key.export({ type: 'spki', format: 'der' }));
}

export function trustedKey(registry, keyId, role, identity, now) {
  if (!(registry instanceof Map) || !KEY_ID.test(keyId)) fail('untrusted-key');
  const entry = registry.get(keyId);
  if (!exact(entry, ['publicKeyPem', 'fingerprint', 'role', 'identity', 'authorized', 'revoked', 'notBefore', 'expiresAt']) ||
      entry.role !== role || entry.identity !== identity || entry.authorized !== true || entry.revoked !== false ||
      typeof entry.publicKeyPem !== 'string' || entry.publicKeyPem.length > 1024 ||
      !entry.publicKeyPem.startsWith('-----BEGIN PUBLIC KEY-----\n') ||
      !DIGEST.test(entry.fingerprint)) fail('untrusted-key');
  const starts = utcMillis(entry.notBefore), ends = utcMillis(entry.expiresAt);
  if (starts > now || ends <= now || ends <= starts) fail('key-expired-or-not-yet-valid');
  try {
    if (publicKeyFingerprint(entry.publicKeyPem) !== entry.fingerprint) fail('untrusted-key');
  } catch {
    fail('untrusted-key');
  }
  return entry;
}

function decode(value, max) {
  if (typeof value !== 'string' || value.length > Math.ceil(max * 4 / 3) + 2 ||
      !/^[A-Za-z0-9_-]+$/.test(value)) fail('invalid-snapshot-encoding');
  const bytes = Buffer.from(value, 'base64url');
  if (bytes.length === 0 || bytes.length > max || bytes.toString('base64url') !== value) fail('invalid-snapshot-encoding');
  return bytes;
}

export function parseCanonical(bytes, max) {
  if (!Buffer.isBuffer(bytes) || bytes.length === 0 || bytes.length > max) fail('invalid-snapshot-bytes');
  let value;
  try { value = JSON.parse(bytes.toString('utf8')); } catch { fail('invalid-snapshot-json'); }
  if (!Buffer.from(canonicalJson(value)).equals(bytes)) fail('noncanonical-snapshot');
  return value;
}

export function validOrigin(origin) {
  if (typeof origin !== 'string' || origin.length > 200) return false;
  try {
    const url = new URL(origin);
    return url.protocol === 'https:' && url.origin === origin &&
      url.username === '' && url.password === '';
  } catch {
    return false;
  }
}

export function verifyAuthoritySnapshot(bytes, {
  registry, origin, nonce, requestedAt, now, review, payload, expectedRevision,
}) {
  if (!validOrigin(origin) || !UUID.test(nonce) || !positive(expectedRevision) ||
      !Number.isSafeInteger(now) || !Number.isSafeInteger(requestedAt) ||
      requestedAt > now || now - requestedAt >= 60_000) fail('invalid-authority-request');
  const envelope = parseCanonical(bytes, 8192);
  if (!exact(envelope, ['version', 'kind', 'algorithm', 'keyId', 'payload', 'signature']) ||
      envelope.version !== 'hic.dev-authority-envelope/1' ||
      envelope.kind !== 'SignedReadOnlyAuthoritySnapshot' ||
      envelope.algorithm !== 'Ed25519') fail('invalid-snapshot-envelope');
  const key = trustedKey(registry, envelope.keyId, 'issuer', AUTHORITY_ISSUER, now);
  const payloadBytes = decode(envelope.payload, 4096);
  const signature = decode(envelope.signature, 64);
  if (signature.length !== 64 || !verify(null,
    Buffer.concat([Buffer.from(AUTHORITY_DOMAIN + envelope.keyId + '\n'), payloadBytes]),
    key.publicKeyPem, signature)) fail('invalid-snapshot-signature');
  const snapshot = parseCanonical(payloadBytes, 4096);
  if (!exact(snapshot, ['version', 'kind', 'issuer', 'audience', 'serverOrigin', 'repository',
    'taskId', 'taskRevision', 'taskStatus', 'actorId', 'orderId', 'reviewId', 'reviewerKeyId',
    'reviewPayloadDigest', 'source', 'approval', 'sequence', 'revocationEpoch',
    'issuedAt', 'expiresAt', 'nonce', 'executionAllowed']) ||
      snapshot.version !== 'hic.dev-authority-snapshot/1' ||
      snapshot.kind !== 'ReadOnlyTaskAuthority' || snapshot.issuer !== AUTHORITY_ISSUER ||
      snapshot.audience !== AUTHORITY_AUDIENCE || snapshot.serverOrigin !== origin ||
      snapshot.repository !== REPO || snapshot.executionAllowed !== false ||
      !positive(snapshot.sequence) || !positive(snapshot.revocationEpoch)) fail('invalid-snapshot');
  const issued = utcMillis(snapshot.issuedAt), expires = utcMillis(snapshot.expiresAt);
  if (issued < requestedAt || issued > now || expires <= now || expires <= issued ||
      expires - issued > 60_000 || expires > Date.parse(payload.expiresAt) ||
      issued < utcMillis(key.notBefore) ||
      expires > utcMillis(key.expiresAt) || snapshot.nonce !== nonce) fail('stale-snapshot-or-nonce');
  if (snapshot.taskId !== review.taskId || snapshot.taskRevision !== expectedRevision ||
      snapshot.taskStatus !== 'Draft' || snapshot.actorId !== review.reviewerId ||
      snapshot.orderId !== review.orderId || snapshot.reviewId !== review.reviewId ||
      snapshot.reviewerKeyId !== review.signerKeyId ||
      snapshot.reviewPayloadDigest !== digest(Buffer.from(canonicalJson(payload))) ||
      canonicalJson(snapshot.source) !== canonicalJson(payload.source) ||
      payload.review.revisionDigest !== digest(Buffer.from(canonicalJson(payload.task)))) fail('authority-binding-mismatch');
  const approval = snapshot.approval;
  if (!exact(approval, ['approvalId', 'state', 'actorId', 'taskRevision', 'intentDigest',
    'decidedAt', 'expiresAt', 'revokedAt']) || !UUID.test(approval.approvalId) ||
      !['pending', 'approved', 'revoked'].includes(approval.state) ||
      approval.actorId !== snapshot.actorId || approval.taskRevision !== expectedRevision ||
      approval.intentDigest !== payload.review.revisionDigest) fail('invalid-snapshot-approval');
  const approvalExpiry = utcMillis(approval.expiresAt);
  if (approvalExpiry < expires || approvalExpiry > Date.parse(payload.expiresAt)) fail('invalid-snapshot-approval');
  if (approval.state === 'pending') {
    if (approval.decidedAt !== null || approval.revokedAt !== null) fail('invalid-snapshot-approval');
  } else {
    const decision = utcMillis(approval.decidedAt);
    if (decision < Date.parse(payload.review.reviewedAt) || decision > issued ||
        approvalExpiry <= decision || approvalExpiry - decision > 300_000) fail('invalid-snapshot-approval');
    if (approval.state === 'approved' && approval.revokedAt !== null) fail('invalid-snapshot-approval');
    if (approval.state === 'revoked') {
      const revoked = utcMillis(approval.revokedAt);
      if (revoked < decision || revoked > issued) fail('invalid-snapshot-approval');
    }
  }
  return { snapshot, issuerKeyId: envelope.keyId, issuerFingerprint: key.fingerprint };
}
