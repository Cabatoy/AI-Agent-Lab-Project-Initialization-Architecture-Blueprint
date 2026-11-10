// @vitest-environment node
import { existsSync, readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import contract from '../../../docs-md/dashboard/dashboard-contract.json';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
const docsRoot = resolve(root, 'docs-md/dashboard');
const read = (path: string) => readFileSync(resolve(root, path), 'utf8');
const document = (path: string) => readFileSync(resolve(docsRoot, path), 'utf8');
const automaticIds = ['health', 'tasks', 'plans', 'diagnostics'];

describe('Issue 79 offline design evidence (not runtime or browser acceptance)', () => {
  it('keeps all execution, migration, lifecycle and tenancy grants disabled', () => {
    expect(contract.status).toBe('proposed-design-only');
    expect(contract.runtimeEnabled).toBe(false);
    expect(Object.values(contract.guardrails).every(value => value === false)).toBe(true);
    expect(contract.derivedApprovalCount).toBe(false);
    expect(contract.deadLetterSupported).toBe(false);
  });

  it('uses unique widget and endpoint IDs with explicit source references', () => {
    expect(new Set(contract.widgets.map(widget => widget.id)).size).toBe(contract.widgets.length);
    expect(new Set(contract.endpoints.map(endpoint => endpoint.id)).size).toBe(contract.endpoints.length);
    for (const endpoint of contract.endpoints) {
      expect(Object.keys(contract.sources)).toContain(endpoint.source);
      expect(endpoint.path.startsWith('/')).toBe(true);
      expect(endpoint.file.startsWith('src/ControlPlane/')).toBe(true);
    }
  });

  it.each(Object.entries(contract.sources))('pins reviewed source %s in both languages', (_id, source) => {
    expect(source.sha).toMatch(/^[a-f0-9]{40}$/);
    for (const language of ['tr', 'en']) {
      expect(document(`${language}/dashboard-design.md`)).toContain(source.sha);
      expect(document(`${language}/api-mapping.md`)).toContain(`/blob/${source.sha}/`);
    }
  });

  it.each(Object.values(contract.documents).flat())('keeps %s portable and linked', path => {
    const text = document(path);
    expect(text).toContain('#79');
    expect(text).toMatch(/## (Yönetici Özeti|Executive Summary)/);
    expect(text).toMatch(/## (Teknik Ek|Technical Appendix)/);
    const targets = [...text.matchAll(/\]\(([^)]+)\)/g)].map(match => match[1]);
    expect(targets.length).toBeGreaterThan(0);
    for (const target of targets) {
      expect(target).not.toMatch(/^(?:file:|[A-Za-z]:|\/)/);
      if (target.startsWith('https://')) continue;
      const local = target.split('#')[0];
      expect(existsSync(resolve(dirname(resolve(docsRoot, path)), local)), `${path}: ${target}`).toBe(true);
    }
  });

  it.each(contract.endpoints)('documents method and authoritative path for $id', endpoint => {
    for (const language of ['tr', 'en']) {
      const text = document(`${language}/api-mapping.md`);
      expect(text).toContain(`\`${endpoint.id}\``);
      expect(text).toContain(`${endpoint.method} \`${endpoint.path}\``);
    }
    if (endpoint.source !== 'develop') {
      expect(endpoint.auth).toBe('operator-session');
      expect(endpoint.automatic).toBe(false);
    }
  });

  it.each(contract.endpoints.filter(endpoint => endpoint.source === 'develop'))(
    'verifies baseline $id against the actual Control Plane route declaration',
    endpoint => {
      const source = read(endpoint.file);
      expect(source).toContain(`app.MapGet("${endpoint.path}"`);
      expect(endpoint.method).toBe('GET');
      expect(endpoint.auth).toBe('local-loopback');
    },
  );

  it('maps all current task states without creating an approval or dead-letter state machine', () => {
    const source = read('src/ControlPlane/Program.cs');
    const statusBlock = source.split('internal static class TaskStatuses')[1].split('internal static class TaskStatusTransitions')[0];
    const statuses = [...statusBlock.matchAll(/public const string \w+ = "([^"]+)";/g)].map(match => match[1]);
    expect(contract.taskStatuses).toEqual(statuses);
    expect(contract.runningTaskStatuses).toEqual(['InProgress', 'Executing']);
    expect(contract.taskStatuses).not.toContain('DeadLetter');
    expect(contract.taskStatuses).not.toContain('WaitingForApproval');
  });

  it.each(contract.widgets)('maps $id only to known read data, or explicitly declares a gap', widget => {
    for (const id of widget.endpoints) {
      const endpoint = contract.endpoints.find(value => value.id === id);
      expect(endpoint, `${widget.id}: ${id}`).toBeDefined();
      expect(['read', 'provider-read']).toContain(endpoint?.effect);
    }
    if (widget.availability === 'gap') {
      expect(widget.endpoints).toEqual([]);
      expect(widget.fallback).toMatch(/^no-/);
    } else {
      expect(widget.endpoints.length).toBeGreaterThan(0);
    }
  });

  it('prevents automatic provider calls, previews and decision/import writes', () => {
    const automatic = contract.endpoints.filter(endpoint => endpoint.automatic);
    expect(automatic.map(endpoint => endpoint.id)).toEqual(automaticIds);
    expect(contract.refreshProposal.automaticEndpointIds).toEqual(automaticIds);
    expect(automatic.every(endpoint => endpoint.method === 'GET' && endpoint.effect === 'read')).toBe(true);
    for (const endpoint of contract.endpoints.filter(value => value.method === 'POST')) {
      expect(endpoint.automatic).toBe(false);
      expect(endpoint.auth).toBe('operator-session');
    }
    expect(contract.endpoints.some(endpoint => /\/execute$|\/claim$|\/complete-draft$/.test(endpoint.path))).toBe(false);
  });

  it('bounds refresh and requires cancellation plus late-response suppression', () => {
    const refresh = contract.refreshProposal;
    expect(refresh.intervalMs).toBe(30000);
    expect(refresh.timeoutMs).toBe(8000);
    expect(refresh.maxConcurrent).toBe(2);
    expect(refresh.maxRequestsPerCycle).toBe(automaticIds.length);
    expect(refresh.maxAutomaticRetryPerCycle).toBe(0);
    expect(refresh.maxRowsRendered).toBe(50);
    expect(refresh.staleAfterMs).toBe(60000);
    expect(refresh.abortOn).toEqual(['unmount', 'hidden', 'offline', 'session-ended', 'context-change']);
    expect(refresh.ignoreLateGeneration).toBe(true);
    expect(refresh.pollStackedProviders).toBe(false);
  });

  it('covers contiguous responsive widths without changing information availability', () => {
    expect(contract.responsiveProposal.map(value => value.minWidth)).toEqual([320, 768, 1200]);
    expect(contract.responsiveProposal.map(value => value.columns)).toEqual([1, 2, 3]);
    for (const [index, breakpoint] of contract.responsiveProposal.entries()) {
      const next = contract.responsiveProposal[index + 1];
      if (next) expect(breakpoint.maxWidth).toBe(next.minWidth - 1);
      else expect(breakpoint.maxWidth).toBeNull();
    }
  });

  it('distinguishes empty/zero from missing, stale, denied and invalid data', () => {
    expect(contract.requiredStates).toEqual([
      'loading', 'empty', 'ready', 'stale', 'offline', 'unauthenticated',
      'forbidden', 'invalid-contract', 'unavailable',
    ]);
    expect(contract.widgets.find(widget => widget.id === 'tasks')?.fallback).toBe('unknown-not-zero');
    expect(contract.widgets.find(widget => widget.id === 'decisions')?.fallback).toBe('no-global-inbox');
    expect(contract.widgets.find(widget => widget.id === 'github')?.fallback).toBe('no-ci-feed');
    expect(contract.widgets.find(widget => widget.id === 'resources')?.fallback).toBe('no-web-safe-api');
  });

  it.each(['tr', 'en'])('keeps the %s personalization ADR proposed with explicit future boundaries', language => {
    const text = document(`${language}/adr-079-personalization.md`);
    expect(text).toMatch(language === 'tr' ? /Durum:\*\* Önerildi/ : /Status:\*\* Proposed/);
    for (const field of [
      'tenantId', 'subjectId', 'policyVersion', 'schemaVersion', 'revision',
      'requestedCapabilities', 'contentDigest', 'distribution=private', 'rollback',
    ]) expect(text).toContain(field);
    expect(text).toContain('AWS/Azure');
    expect(text).toContain('409/412');
  });
});
