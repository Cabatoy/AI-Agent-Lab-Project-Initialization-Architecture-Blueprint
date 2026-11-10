import { randomUUID } from 'node:crypto';
import { canonicalJson, verifyReviewedEnvelope } from './reviewed-order.mjs';
import {
  AUTHORITY_AUDIENCE, digest, trustedKey, utcMillis, validOrigin, verifyAuthoritySnapshot,
} from './authority-snapshot.mjs';

const fail = reason => Object.freeze({ status: 'blocked', reason, executionAllowed: false });
const MAX_OBSERVATIONS = 1024;
const MAX_DURATION_MS = 5000;

async function bounded(operation, signal) {
  const controller = new AbortController();
  let timer, abort;
  const interrupted = new Promise((_, reject) => {
    abort = () => { controller.abort(); reject(new Error('authority-interrupted')); };
    timer = setTimeout(abort, MAX_DURATION_MS);
    signal?.addEventListener('abort', abort, { once: true });
    if (signal?.aborted) abort();
  });
  try {
    return await Promise.race([
      Promise.resolve().then(() => {
        if (controller.signal.aborted) throw new Error('authority-interrupted');
        return operation(controller.signal);
      }),
      interrupted,
    ]);
  } finally {
    clearTimeout(timer);
    signal?.removeEventListener('abort', abort);
    controller.abort();
  }
}

// Offline injection boundary only. No HTTP, disk cache, enrollment or watcher dispatch.
// These bounded observations prevent in-process replay; they are NOT a durable ledger.
export class ReadOnlyAuthorityBridge {
  #provider;
  #readRegistry;
  #origin;
  #clock;
  #nonce;
  #busy = false;
  #nonces = new Set();
  #observations = new Map();
  #keyPins = new Map();
  #revokedApprovals = new Set();
  #faulted = false;

  constructor({ authorityProvider, readTrustRegistry, origin, clock = Date.now, nonce = randomUUID } = {}) {
    this.#provider = authorityProvider;
    this.#readRegistry = readTrustRegistry;
    this.#origin = origin;
    this.#clock = clock;
    this.#nonce = nonce;
  }

  async inspect(envelopeBytes, { expectedGitSha, expectedTaskRevision, signal } = {}) {
    if (this.#busy || this.#faulted) return fail('authority-session-blocked');
    if (typeof this.#provider?.readSnapshot !== 'function' ||
        typeof this.#readRegistry !== 'function' || !validOrigin(this.#origin)) return fail('live-authority-unavailable');
    if (this.#nonces.size >= MAX_OBSERVATIONS) return fail('authority-session-exhausted');
    if (!Buffer.isBuffer(envelopeBytes) || envelopeBytes.length > 8192 ||
        !Number.isSafeInteger(expectedTaskRevision) || expectedTaskRevision < 1) return fail('invalid-reviewed-order');
    const reviewBytes = Buffer.from(envelopeBytes);
    this.#busy = true;
    try {
      return await bounded(async providerSignal => {
        const requestedAt = this.#clock();
        const nonce = this.#nonce();
        if (typeof nonce !== 'string' || !/^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/.test(nonce)) return fail('invalid-authority-request');
        if (this.#nonces.has(nonce)) return fail('authority-request-replayed');
        this.#nonces.add(nonce);
        const registry = await this.#readRegistry(providerSignal);
        if (providerSignal.aborted) return fail('authority-interrupted');
        const envelope = JSON.parse(reviewBytes.toString('utf8'));
        const payload = JSON.parse(Buffer.from(envelope.payload, 'base64url').toString('utf8'));
        const reviewer = trustedKey(registry, envelope.keyId, 'reviewer', payload.review?.reviewerId, requestedAt);
        const reviewerFingerprint = reviewer.fingerprint;
        if (Date.parse(payload.review.reviewedAt) < utcMillis(reviewer.notBefore) ||
            Date.parse(payload.review.reviewedAt) > requestedAt) return fail('reviewer-key-time-mismatch');
        const reviewKeys = new Map([[envelope.keyId, {
          publicKeyPem: reviewer.publicKeyPem, reviewerId: reviewer.identity,
          authorized: true, revoked: false,
        }]]);
        const review = verifyReviewedEnvelope(reviewBytes, {
          trustedKeys: reviewKeys, now: requestedAt, expectedGitSha,
        });
        const pathAliases = new Set();
        for (const path of review.allowedPaths) {
          if (path.split('/').some(part => part.endsWith('.') ||
              /^(?:con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)/i.test(part)) ||
              pathAliases.has(path.toLowerCase())) return fail('invalid-reviewed-path-alias');
          pathAliases.add(path.toLowerCase());
        }
        const response = await this.#provider.readSnapshot(Object.freeze({
          version: 'hic.dev-authority-request/1', audience: AUTHORITY_AUDIENCE,
          taskId: review.taskId, taskRevision: expectedTaskRevision,
          orderId: review.orderId, reviewId: review.reviewId,
          reviewPayloadDigest: digest(Buffer.from(canonicalJson(payload))),
          nonce, requestedAt: new Date(requestedAt).toISOString(),
        }), { signal: providerSignal });
        if (providerSignal.aborted) return fail('authority-interrupted');
        if (!Buffer.isBuffer(response) || response.length > 8192) return fail('invalid-authority-response');
        const snapshotBytes = Buffer.from(response);
        // Never reuse a previous key registry after an authority/network await.
        const latestRegistry = await this.#readRegistry(providerSignal);
        const now = this.#clock();
        const latestReviewer = trustedKey(latestRegistry, envelope.keyId, 'reviewer', review.reviewerId, now);
        if (latestReviewer.fingerprint !== reviewerFingerprint) return fail('reviewer-pin-changed');
        verifyReviewedEnvelope(reviewBytes, {
          trustedKeys: reviewKeys, now, expectedGitSha,
        });
        const { snapshot, issuerKeyId, issuerFingerprint } = verifyAuthoritySnapshot(snapshotBytes, {
          registry: latestRegistry, origin: this.#origin, nonce, requestedAt, now,
          review, payload, expectedRevision: expectedTaskRevision,
        });
        if (issuerFingerprint === latestReviewer.fingerprint) return fail('authority-key-role-collision');
        if (utcMillis(snapshot.expiresAt) > utcMillis(latestReviewer.expiresAt)) return fail('reviewer-key-expiry-mismatch');
        if (providerSignal.aborted) return fail('authority-interrupted');
        for (const [id, fingerprint] of [
          [`reviewer:${envelope.keyId}`, reviewerFingerprint],
          [`issuer:${issuerKeyId}`, issuerFingerprint],
        ]) {
          if (this.#keyPins.has(id) && this.#keyPins.get(id) !== fingerprint) {
            this.#faulted = true;
            return fail('authority-key-rebound');
          }
          this.#keyPins.set(id, fingerprint);
        }
        const previous = this.#observations.get(review.taskId);
        const stateDigest = digest(canonicalJson({
          taskRevision: snapshot.taskRevision, actorId: snapshot.actorId,
          reviewPayloadDigest: snapshot.reviewPayloadDigest, approval: snapshot.approval,
        }));
        const intentDigest = payload.review.revisionDigest;
        const sourceDigest = digest(canonicalJson(payload.source));
        if (previous && (snapshot.sequence <= previous.sequence ||
            snapshot.taskRevision < previous.revision ||
            snapshot.revocationEpoch < previous.epoch ||
            utcMillis(snapshot.issuedAt) < previous.issuedAt ||
            (snapshot.taskRevision === previous.revision &&
             (intentDigest !== previous.intentDigest || sourceDigest !== previous.sourceDigest)) ||
            (snapshot.taskRevision === previous.revision &&
             snapshot.revocationEpoch === previous.epoch && stateDigest !== previous.stateDigest))) {
          this.#faulted = true;
          return fail('authority-rollback-or-equivocation');
        }
        if (this.#revokedApprovals.has(snapshot.approval.approvalId) &&
            snapshot.approval.state !== 'revoked') return fail('approval-previously-revoked');
        this.#observations.set(review.taskId, {
          sequence: snapshot.sequence, revision: snapshot.taskRevision,
          epoch: snapshot.revocationEpoch, issuedAt: utcMillis(snapshot.issuedAt),
          stateDigest, intentDigest, sourceDigest,
        });
        if (snapshot.approval.state === 'revoked') {
          this.#revokedApprovals.add(snapshot.approval.approvalId);
          return fail('approval-revoked');
        }
        return Object.freeze({
          status: snapshot.approval.state === 'pending' ? 'awaiting-approval' : 'accepted-for-review',
          reason: snapshot.approval.state === 'pending' ? 'human-approval-pending' : 'offline-authority-cross-check-only',
          executionAllowed: false,
          ledgerVerified: false,
          liveAuthorityVerified: false,
        });
      }, signal);
    } catch {
      // Only a fixed code crosses this boundary; provider/crypto/path errors are untrusted.
      return fail('authority-verification-failed');
    } finally {
      this.#busy = false;
    }
  }
}
