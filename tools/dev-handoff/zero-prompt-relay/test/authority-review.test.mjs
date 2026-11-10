import assert from 'node:assert/strict';
import { createPrivateKey, createPublicKey, sign } from 'node:crypto';
import test from 'node:test';
import { ReadOnlyAuthorityBridge } from '../src/authority-review.mjs';
import {
  AUTHORITY_AUDIENCE, AUTHORITY_DOMAIN, AUTHORITY_ISSUER, digest, publicKeyFingerprint,
} from '../src/authority-snapshot.mjs';
import { canonicalJson, SIGNING_CONTEXT_FOR_TESTS } from '../src/reviewed-order.mjs';
import { REPO } from '../src/relay.mjs';

const NOW = Date.UTC(2026, 10, 10, 19, 0, 0);
const uuid = n => `11111111-2222-4333-8444-${n.toString(16).padStart(12, '0')}`;
const sha = 'a'.repeat(40);
const origin = 'https://authority.example.invalid';
const iso = ms => new Date(ms).toISOString();
// Public deterministic seeds ONLY for offline tests; never use these keys operationally.
function syntheticKey(byte) {
  return createPrivateKey({
    key: Buffer.concat([Buffer.from('302e020100300506032b657004220420', 'hex'), Buffer.alloc(32, byte)]),
    format: 'der', type: 'pkcs8',
  });
}
const reviewerPrivate = syntheticKey(1), issuerPrivate = syntheticKey(2), forgedPrivate = syntheticKey(3);
function entry(key, role, identity) {
  const publicKeyPem = createPublicKey(key).export({ type: 'spki', format: 'pem' });
  return {
    publicKeyPem, fingerprint: publicKeyFingerprint(publicKeyPem), role, identity,
    authorized: true, revoked: false, notBefore: iso(NOW - 3600_000), expiresAt: iso(NOW + 3600_000),
  };
}
function signedEnvelope(payload, { authority = false, privateKey, keyId, rawPayload } = {}) {
  const bytes = rawPayload ?? Buffer.from(canonicalJson(payload));
  const id = keyId ?? (authority ? 'authority-test' : 'reviewer-test');
  const context = authority ? AUTHORITY_DOMAIN : SIGNING_CONTEXT_FOR_TESTS;
  const signature = sign(null, Buffer.concat([Buffer.from(context + id + '\n'), bytes]),
    privateKey ?? (authority ? issuerPrivate : reviewerPrivate));
  return Buffer.from(canonicalJson({
    version: authority ? 'hic.dev-authority-envelope/1' : 'hic.dev-review-envelope/1',
    kind: authority ? 'SignedReadOnlyAuthoritySnapshot' : 'SignedReviewedDevWorkOrder',
    algorithm: 'Ed25519', keyId: id, payload: bytes.toString('base64url'), signature: signature.toString('base64url'),
  }));
}

function fixture() {
  const payload = {
    version: 'hic.dev-reviewed-work-order/1', kind: 'ReviewedDevWorkOrder', orderId: uuid(1),
    createdAt: iso(NOW - 120_000), expiresAt: iso(NOW + 240_000),
    source: { repository: REPO, transportRef: 'hic-dev-orders', queueIssue: 75, issueNumber: 102, reviewedGitSha: sha },
    task: {
      taskId: uuid(2), repository: REPO, branch: 'feature/authority-test', baseSha: 'b'.repeat(40),
      allowedPaths: ['tools/dev-handoff/zero-prompt-relay/README.md'],
      objective: 'Synthetic review only', requiresHumanApproval: true,
    },
    review: { reviewId: uuid(3), reviewerId: 'offline-reviewer', reviewedAt: iso(NOW - 60_000), revisionDigest: '', status: 'reviewed' },
    limits: { executionAllowed: false, allowedTools: [], maxModelCostUsd: 0, maxExecutionSeconds: 0, allowNetwork: false, allowGitWrite: false },
  };
  payload.review.revisionDigest = digest(canonicalJson(payload.task));
  const registry = new Map([
    ['reviewer-test', entry(reviewerPrivate, 'reviewer', 'offline-reviewer')],
    ['authority-test', entry(issuerPrivate, 'issuer', AUTHORITY_ISSUER)],
  ]);
  let sequence = 0, nonceCounter = 100;
  const f = {
    payload, registry, now: NOW, requests: [], epoch: 1, state: 'approved', approvalId: uuid(4),
    mutate: () => {}, envelopeOptions: {}, responseTransform: bytes => bytes,
    readRegistry: () => registry,
  };
  f.provider = {
    async readSnapshot(request, { signal }) {
      assert.equal(signal.aborted, false);
      assert.equal(request.audience, AUTHORITY_AUDIENCE);
      assert.equal(request.version, 'hic.dev-authority-request/1');
      f.requests.push(request);
      const snapshot = {
        version: 'hic.dev-authority-snapshot/1', kind: 'ReadOnlyTaskAuthority',
        issuer: AUTHORITY_ISSUER, audience: AUTHORITY_AUDIENCE, serverOrigin: origin,
        repository: REPO, taskId: payload.task.taskId, taskRevision: 7, taskStatus: 'Draft',
        actorId: payload.review.reviewerId, orderId: payload.orderId,
        reviewId: payload.review.reviewId, reviewerKeyId: 'reviewer-test',
        reviewPayloadDigest: digest(canonicalJson(payload)), source: { ...payload.source },
        approval: {
          approvalId: f.approvalId, state: f.state, actorId: payload.review.reviewerId,
          taskRevision: 7, intentDigest: payload.review.revisionDigest,
          decidedAt: f.state === 'pending' ? null : iso(NOW - 30_000),
          expiresAt: iso(NOW + 120_000), revokedAt: f.state === 'revoked' ? iso(f.now) : null,
        },
        sequence: ++sequence, revocationEpoch: f.epoch, issuedAt: iso(f.now),
        expiresAt: iso(f.now + 30_000), nonce: request.nonce, executionAllowed: false,
      };
      f.mutate(snapshot);
      return f.responseTransform(signedEnvelope(snapshot, { authority: true, ...f.envelopeOptions }));
    },
  };
  f.bridge = options => new ReadOnlyAuthorityBridge({
    authorityProvider: f.provider, readTrustRegistry: (...args) => f.readRegistry(...args),
    origin, clock: () => f.now, nonce: () => uuid(++nonceCounter), ...options,
  });
  f.inspect = (bridge = f.bridge(), options = {}) => bridge.inspect(signedEnvelope(payload), {
    expectedGitSha: sha, expectedTaskRevision: 7, ...options,
  });
  return f;
}

function assertBlocked(result) {
  assert.equal(result.status, 'blocked');
  assert.equal(result.executionAllowed, false);
  assert.equal(JSON.stringify(result).includes('SECRET'), false);
}

test('default bridge is BLOCKED; signed fake authority accepts review, NEVER execution', async () => {
  const f = fixture();
  assertBlocked(await f.inspect(new ReadOnlyAuthorityBridge()));
  const result = await f.inspect();
  assert.deepEqual(result, {
    status: 'accepted-for-review', reason: 'offline-authority-cross-check-only',
    executionAllowed: false, ledgerVerified: false, liveAuthorityVerified: false,
  });
  assert.equal(f.requests.length, 1);
});

test('pending and revoked approvals stay non-executing; legacy task objects cannot substitute for signed authority', async () => {
  const f = fixture();
  f.state = 'pending';
  assert.equal((await f.inspect()).status, 'awaiting-approval');
  f.state = 'revoked';
  assert.equal((await f.inspect()).reason, 'approval-revoked');
  assertBlocked(await f.inspect(f.bridge({ authorityProvider: {
    readSnapshot: async () => ({ id: f.payload.task.taskId, status: 'Draft', nativeRunRevision: 7, authenticated: true }),
  } })));
});

const mutations = {
  'task ID': s => { s.taskId = uuid(99); },
  'task revision': s => { s.taskRevision++; },
  'task status': s => { s.taskStatus = 'Approved'; },
  actor: s => { s.actorId = 'forged-actor'; },
  reviewer: s => { s.reviewerKeyId = 'other-reviewer'; },
  issuer: s => { s.issuer = 'github-issue'; },
  audience: s => { s.audience = 'hic-native-host'; },
  origin: s => { s.serverOrigin = 'https://attacker.example.invalid'; },
  repository: s => { s.repository = 'attacker/repo'; },
  'base/source SHA': s => { s.source.reviewedGitSha = 'c'.repeat(40); },
  'transport ref': s => { s.source.transportRef = 'main'; },
  'order ID': s => { s.orderId = uuid(99); },
  'review ID': s => { s.reviewId = uuid(99); },
  'payload digest': s => { s.reviewPayloadDigest = 'f'.repeat(64); },
  'approval actor': s => { s.approval.actorId = 'forged-actor'; },
  'approval revision': s => { s.approval.taskRevision++; },
  'approval digest': s => { s.approval.intentDigest = 'f'.repeat(64); },
  'approval state': s => { s.approval.state = 'running'; },
  'approval expired': s => { s.approval.expiresAt = iso(NOW - 1); },
  'approval future': s => { s.approval.decidedAt = iso(NOW + 1); },
  'approval revoked flag': s => { s.approval.revokedAt = iso(NOW); },
  'approval lifetime': s => { s.approval.decidedAt = iso(NOW - 600_000); },
  'expired snapshot': s => { s.expiresAt = iso(NOW); },
  'future snapshot': s => { s.issuedAt = iso(NOW + 1); },
  'stale cached snapshot': s => { s.issuedAt = iso(NOW - 1); },
  'snapshot lifetime': s => { s.expiresAt = iso(NOW + 61_000); },
  nonce: s => { s.nonce = uuid(999); },
  'zero epoch': s => { s.revocationEpoch = 0; },
  'unsafe sequence': s => { s.sequence = Number.MAX_SAFE_INTEGER + 1; },
  'mixed version': s => { s.version = 'hic.dev-authority-snapshot/2'; },
  'executable flag': s => { s.executionAllowed = true; },
  'unknown field': s => { s.authenticated = true; },
};
for (const [name, mutate] of Object.entries(mutations)) {
  test(`signed snapshot rejects mismatched ${name}`, async () => {
    const f = fixture();
    f.mutate = mutate;
    assertBlocked(await f.inspect());
  });
}

test('every required snapshot and approval field is mandatory', async () => {
  const fields = ['version', 'kind', 'issuer', 'audience', 'serverOrigin', 'repository',
    'taskId', 'taskRevision', 'taskStatus', 'actorId', 'orderId', 'reviewId', 'reviewerKeyId',
    'reviewPayloadDigest', 'source', 'approval', 'sequence', 'revocationEpoch',
    'issuedAt', 'expiresAt', 'nonce', 'executionAllowed'];
  for (const field of fields) {
    const f = fixture();
    f.mutate = s => { delete s[field]; };
    assertBlocked(await f.inspect());
  }
  for (const field of ['approvalId', 'state', 'actorId', 'taskRevision', 'intentDigest', 'decidedAt', 'expiresAt', 'revokedAt']) {
    const f = fixture();
    f.mutate = s => { delete s.approval[field]; };
    assertBlocked(await f.inspect());
  }
});

test('forged signature, unknown keys, role confusion, fingerprint swap and envelope downgrade fail closed', async () => {
  for (const options of [{ privateKey: forgedPrivate }, { keyId: 'unknown-issuer' }]) {
    const f = fixture(); f.envelopeOptions = options;
    assertBlocked(await f.inspect());
  }
  for (const keyId of ['reviewer-test', 'authority-test']) {
    for (const change of [
      { revoked: true }, { authorized: false }, { fingerprint: '0'.repeat(64) },
      { role: keyId === 'reviewer-test' ? 'issuer' : 'reviewer' },
      { expiresAt: iso(NOW) }, { notBefore: iso(NOW + 1) },
      { identity: 'forged-actor' }, { publicKeyPem: 'SECRET invalid key' },
    ]) {
      const f = fixture();
      f.registry.set(keyId, { ...f.registry.get(keyId), ...change });
      assertBlocked(await f.inspect());
    }
  }
  for (const change of [{ algorithm: 'RSA' }, { version: 'hic.dev-authority-envelope/2' },
    { signature: 'invalid' }, { keyId: 'reviewer-test' }, { publicKey: 'caller-selected' }]) {
    const f = fixture();
    f.responseTransform = bytes => Buffer.from(canonicalJson({ ...JSON.parse(bytes), ...change }));
    assertBlocked(await f.inspect());
  }
});

test('noncanonical, duplicate-key, oversized, malformed and raw signature-tampered snapshots fail closed', async () => {
  for (const transform of [
    () => Buffer.from('SECRET not json'),
    () => Buffer.alloc(8193),
    bytes => Buffer.from(JSON.stringify(JSON.parse(bytes), null, 2)),
    bytes => Buffer.from(bytes.toString().replace('"algorithm":', '"extra":1,"extra":2,"algorithm":')),
    bytes => {
      const e = JSON.parse(bytes);
      const p = JSON.parse(Buffer.from(e.payload, 'base64url'));
      p.actorId = 'forged';
      e.payload = Buffer.from(canonicalJson(p)).toString('base64url');
      return Buffer.from(canonicalJson(e));
    },
  ]) {
    const f = fixture(); f.responseTransform = transform;
    assertBlocked(await f.inspect());
  }
  const f = fixture();
  f.envelopeOptions = { rawPayload: Buffer.from('{"version":"x","version":"x"}') };
  assertBlocked(await f.inspect());
});

test('GitHub transformations, malformed paths and unbound review digests cannot become authority', async () => {
  for (const path of ['../secret', 'tools/dev-handoff/../secret.md', 'C:\\SECRET.md',
    'tools/dev-handoff/relay/file.md:stream', 'tools/dev-handoff/relay/README.md.',
    'tools/dev-handoff/relay/CON.md', 'tools/dev-handoff/relay/NUL.json']) {
    const f = fixture();
    f.payload.task.allowedPaths = [path];
    f.payload.review.revisionDigest = digest(canonicalJson(f.payload.task));
    assertBlocked(await f.inspect());
    assert.equal(f.requests.length, 0);
  }
  const f = fixture();
  f.payload.review.revisionDigest = 'c'.repeat(64);
  assertBlocked(await f.inspect());
  const g = fixture();
  assertBlocked(await g.inspect(undefined, { expectedGitSha: 'd'.repeat(40) }));
  assert.equal(g.requests.length, 0);
  const raw = JSON.parse(signedEnvelope(g.payload));
  const p = structuredClone(g.payload);
  p.task.objective = 'transformed GitHub text';
  raw.payload = Buffer.from(canonicalJson(p)).toString('base64url');
  assertBlocked(await g.bridge().inspect(Buffer.from(canonicalJson(raw)), {
    expectedGitSha: sha, expectedTaskRevision: 7,
  }));
});

test('Windows case-colliding paths cannot appear in the same reviewed intent', async () => {
  const f = fixture();
  f.payload.task.allowedPaths.push('tools/dev-handoff/zero-prompt-relay/readme.md');
  f.payload.review.revisionDigest = digest(canonicalJson(f.payload.task));
  assertBlocked(await f.inspect());
  assert.equal(f.requests.length, 0);
});

test('replay, out-of-order sequence and same-epoch equivocation latch the session BLOCKED', async () => {
  for (const mutation of [
    s => { s.sequence = 1; },
    s => { s.revocationEpoch = 0; },
    s => { s.approval.approvalId = uuid(88); },
  ]) {
    const f = fixture(), bridge = f.bridge();
    assert.equal((await f.inspect(bridge)).status, 'accepted-for-review');
    f.mutate = mutation;
    assertBlocked(await f.inspect(bridge));
  }
  const f = fixture(), bridge = f.bridge({ nonce: () => uuid(100) });
  assert.equal((await f.inspect(bridge)).status, 'accepted-for-review');
  assert.equal((await f.inspect(bridge)).reason, 'authority-request-replayed');
  assert.equal(f.requests.length, 1);
});

test('pending can advance with a new epoch; revoked approvals cannot resurrect even at higher epochs', async () => {
  const f = fixture(), bridge = f.bridge();
  f.state = 'pending';
  assert.equal((await f.inspect(bridge)).status, 'awaiting-approval');
  f.state = 'approved'; f.epoch++;
  assert.equal((await f.inspect(bridge)).status, 'accepted-for-review');
  f.state = 'revoked'; f.epoch++;
  assert.equal((await f.inspect(bridge)).reason, 'approval-revoked');
  f.state = 'approved'; f.epoch++;
  assert.equal((await f.inspect(bridge)).reason, 'approval-previously-revoked');
});

test('rollback of a previously observed revocation epoch is rejected', async () => {
  const f = fixture(), bridge = f.bridge();
  f.epoch = 3;
  assert.equal((await f.inspect(bridge)).status, 'accepted-for-review');
  f.epoch = 2;
  assert.equal((await f.inspect(bridge)).reason, 'authority-rollback-or-equivocation');
  f.epoch = 4;
  assert.equal((await f.inspect(bridge)).reason, 'authority-session-blocked');
});

test('changing base SHA or source at the same immutable revision is equivocation even with a higher epoch', async () => {
  for (const mutate of [
    p => { p.task.baseSha = 'e'.repeat(40); },
    p => { p.source.issueNumber++; },
  ]) {
    const f = fixture(), bridge = f.bridge();
    assert.equal((await f.inspect(bridge)).status, 'accepted-for-review');
    mutate(f.payload);
    f.payload.review.revisionDigest = digest(canonicalJson(f.payload.task));
    f.epoch++;
    assert.equal((await f.inspect(bridge)).reason, 'authority-rollback-or-equivocation');
  }
});

test('old signed snapshot cannot be replayed into a new bridge with a fresh challenge', async () => {
  const f = fixture();
  let captured;
  f.responseTransform = bytes => { captured = bytes; return bytes; };
  assert.equal((await f.inspect()).status, 'accepted-for-review');
  f.provider.readSnapshot = async () => captured;
  assertBlocked(await f.inspect());
});

test('operator-enrolled new issuer ID can rotate; same-ID public-key replacement is rejected', async () => {
  const f = fixture(), bridge = f.bridge();
  assert.equal((await f.inspect(bridge)).status, 'accepted-for-review');
  f.registry.get('authority-test').revoked = true;
  f.registry.set('authority-next', entry(forgedPrivate, 'issuer', AUTHORITY_ISSUER));
  f.envelopeOptions = { keyId: 'authority-next', privateKey: forgedPrivate };
  assert.equal((await f.inspect(bridge)).status, 'accepted-for-review');

  f.registry.set('authority-next', entry(issuerPrivate, 'issuer', AUTHORITY_ISSUER));
  f.envelopeOptions.privateKey = issuerPrivate;
  assert.equal((await f.inspect(bridge)).reason, 'authority-key-rebound');
});

test('issuer and reviewer cannot reuse the same public key across roles', async () => {
  const f = fixture();
  f.registry.set('authority-test', entry(reviewerPrivate, 'issuer', AUTHORITY_ISSUER));
  f.envelopeOptions = { privateKey: reviewerPrivate };
  assert.equal((await f.inspect()).reason, 'authority-key-role-collision');
});

test('clock rollback after an observed snapshot latches BLOCKED', async () => {
  const f = fixture(), bridge = f.bridge();
  assert.equal((await f.inspect(bridge)).status, 'accepted-for-review');
  f.now--;
  assert.equal((await f.inspect(bridge)).reason, 'authority-rollback-or-equivocation');
});

test('no prior snapshot is accepted when the final trust-registry refresh fails', async () => {
  const f = fixture();
  let reads = 0;
  f.readRegistry = () => {
    if (++reads === 2) throw new Error('SECRET outage');
    return f.registry;
  };
  assertBlocked(await f.inspect());
  assert.equal(reads, 2);
});

test('partial outages never reuse a successful decision or stale key registry', async () => {
  const f = fixture(), bridge = f.bridge();
  assert.equal((await f.inspect(bridge)).status, 'accepted-for-review');
  f.provider.readSnapshot = async () => { throw new Error('SECRET token C:\\private'); };
  assertBlocked(await f.inspect(bridge));
  f.readRegistry = () => { throw new Error('SECRET registry path'); };
  assertBlocked(await f.inspect(bridge));
});

test('revocation, rotation, expiry and cancellation during provider await fail closed', async () => {
  for (const change of ['reviewer-revoked', 'issuer-revoked', 'pin-swapped', 'expired', 'aborted']) {
    const f = fixture(), controller = new AbortController();
    const original = f.provider.readSnapshot;
    f.provider.readSnapshot = async (...args) => {
      const bytes = await original(...args);
      if (change === 'reviewer-revoked') f.registry.get('reviewer-test').revoked = true;
      if (change === 'issuer-revoked') f.registry.get('authority-test').revoked = true;
      if (change === 'pin-swapped') f.registry.set('reviewer-test', entry(forgedPrivate, 'reviewer', 'offline-reviewer'));
      if (change === 'expired') f.now += 60_000;
      if (change === 'aborted') controller.abort();
      return bytes;
    };
    assertBlocked(await f.inspect(undefined, { signal: controller.signal }));
  }
});

test('concurrent inspect is denied and cancelled providers cannot update session observations', async () => {
  const f = fixture(), controller = new AbortController();
  let entered, finish;
  const ready = new Promise(resolve => { entered = resolve; });
  f.provider.readSnapshot = async () => {
    entered();
    return new Promise(resolve => { finish = resolve; });
  };
  const bridge = f.bridge();
  const pending = f.inspect(bridge, { signal: controller.signal });
  await ready;
  assert.equal((await f.inspect(bridge)).reason, 'authority-session-blocked');
  controller.abort();
  assertBlocked(await pending);
  finish(Buffer.from('SECRET late result'));
});

test('input buffers are copied before registry awaits; no caller mutation changes signed intent', async () => {
  const f = fixture(), bytes = signedEnvelope(f.payload);
  f.readRegistry = () => { bytes.fill(0); return f.registry; };
  const result = await f.bridge().inspect(bytes, { expectedGitSha: sha, expectedTaskRevision: 7 });
  assert.equal(result.status, 'accepted-for-review');
});

test('uncooperative provider is bounded by the five-second deadline and its signal is aborted', async () => {
  const f = fixture();
  let observedSignal;
  f.provider.readSnapshot = async (_request, { signal }) => {
    observedSignal = signal;
    return new Promise(() => {});
  };
  assertBlocked(await f.inspect());
  assert.equal(observedSignal.aborted, true);
});

test('bounded observation capacity is exhausted fail-closed, never evicted to allow replay', async () => {
  const f = fixture(), bridge = f.bridge();
  for (let i = 0; i < 1024; i++) {
    assertBlocked(await bridge.inspect(Buffer.from('invalid-json'), {
      expectedGitSha: sha, expectedTaskRevision: 7,
    }));
  }
  assert.equal((await f.inspect(bridge)).reason, 'authority-session-exhausted');
  assert.equal(f.requests.length, 0);
});

test('invalid expected revision, origin, nonce and pre-cancelled signal fail before authority read', async () => {
  const f = fixture();
  for (const value of [undefined, 0, -1, 1.5, '7', Number.MAX_SAFE_INTEGER + 1]) {
    assertBlocked(await f.inspect(undefined, { expectedTaskRevision: value }));
  }
  for (const value of [undefined, 'http://localhost', `${origin}/path`, `https://user:SECRET@authority.example.invalid`]) {
    assertBlocked(await f.inspect(f.bridge({ origin: value })));
  }
  assertBlocked(await f.inspect(f.bridge({ nonce: () => 'SECRET' })));
  const controller = new AbortController(); controller.abort();
  assertBlocked(await f.inspect(undefined, { signal: controller.signal }));
  assert.equal(f.requests.length, 0);
});
