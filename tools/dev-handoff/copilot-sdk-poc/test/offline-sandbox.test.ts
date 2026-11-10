import assert from "node:assert/strict";
import {
  existsSync,
  lstatSync,
  readFileSync,
  renameSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from "node:fs";
import { execFileSync } from "node:child_process";
import { join } from "node:path";
import test from "node:test";
import {
  buildSandboxPreflight,
  cleanupDisposableOfflineFixture,
  createDisposableOfflineFixture,
  OFFLINE_SANDBOX_POLICY,
  SANDBOX_CHECKS,
  type SandboxCheck,
  type SandboxCheckState,
  type SandboxProbeEvidence,
} from "../src/offline-sandbox.ts";
import { runOfflineSandboxPreflight } from "../src/sandbox-cli.ts";

function unobservedEvidence(): SandboxProbeEvidence {
  const checks = Object.fromEntries(
    SANDBOX_CHECKS.map((name) => [name, "unverified" satisfies SandboxCheckState]),
  ) as Record<SandboxCheck, SandboxCheckState>;
  return {
    sandboxType: "podman-rootless",
    imageDigest: `sha256:${"a".repeat(64)}`,
    fixtureId: "11111111-2222-4333-8444-555555555555",
    checks,
  };
}

test("offline sandbox policy is disabled by default and denies capabilities", () => {
  assert.equal(OFFLINE_SANDBOX_POLICY.enabledByDefault, false);
  assert.equal(OFFLINE_SANDBOX_POLICY.network, "none");
  assert.equal(OFFLINE_SANDBOX_POLICY.inheritedEnvironment, "deny");
  assert.equal(OFFLINE_SANDBOX_POLICY.credentials, "deny");
  assert.equal(OFFLINE_SANDBOX_POLICY.hostSocketsAndDevices, "deny");
  assert.equal(OFFLINE_SANDBOX_POLICY.broadCleanupAllowed, false);
});

test("disposable fixture is synthetic, unique, and removable only with its ownership marker", (t) => {
  const first = createDisposableOfflineFixture();
  const second = createDisposableOfflineFixture();
  t.after(() => {
    for (const fixture of [first, second]) {
      if (existsSync(fixture.root)) {
        assert.equal(cleanupDisposableOfflineFixture(fixture), true);
      }
    }
  });
  assert.notEqual(first.id, second.id);
  assert.match(first.id, /^[0-9a-f-]{36}$/i);
  const repository = join(first.input, "synthetic-public-repository");
  assert.equal(existsSync(join(repository, ".git")), true);
  assert.match(
    execFileSync("git", ["rev-parse", "--verify", "HEAD"], {
      cwd: repository,
      encoding: "utf8",
      windowsHide: true,
    }).trim(),
    /^[0-9a-f]{40,64}$/,
  );
  assert.equal(
    execFileSync("git", ["rev-parse", "--verify", "HEAD"], {
      cwd: repository,
      encoding: "utf8",
      windowsHide: true,
    }).trim(),
    execFileSync("git", ["rev-parse", "--verify", "HEAD"], {
      cwd: join(second.input, "synthetic-public-repository"),
      encoding: "utf8",
      windowsHide: true,
    }).trim(),
  );
  assert.equal(
    execFileSync("git", ["status", "--porcelain"], {
      cwd: repository,
      encoding: "utf8",
      windowsHide: true,
    }).trim(),
    "",
  );
  assert.equal(
    readFileSync(
      join(first.input, "synthetic-public-repository", ".dummy-github-token"),
      "utf8",
    ),
    "NOT-A-CREDENTIAL\n",
  );
  assert.equal(cleanupDisposableOfflineFixture({
    ...first,
    root: second.root,
  }), false);
  assert.equal(existsSync(first.root), true);

  writeFileSync(join(first.root, ".hic-sandbox-owner"), "mismatched-id");
  try {
    assert.equal(cleanupDisposableOfflineFixture(first), false);
    assert.equal(existsSync(first.root), true);
  } finally {
    writeFileSync(join(first.root, ".hic-sandbox-owner"), first.id);
  }
  assert.equal(cleanupDisposableOfflineFixture(first), true);
  assert.equal(existsSync(first.root), false);
  assert.equal(cleanupDisposableOfflineFixture(first), false);
});

test("cleanup refuses a junction/reparse alias and preserves its target", (t) => {
  const fixture = createDisposableOfflineFixture();
  const movedRoot = `${fixture.root}-target`;
  t.after(() => {
    if (existsSync(fixture.root) && lstatSync(fixture.root).isSymbolicLink()) {
      rmSync(fixture.root);
    }
    if (existsSync(movedRoot) && !existsSync(fixture.root)) {
      renameSync(movedRoot, fixture.root);
    }
    if (existsSync(fixture.root)) {
      assert.equal(cleanupDisposableOfflineFixture(fixture), true);
    }
  });

  renameSync(fixture.root, movedRoot);
  symlinkSync(
    movedRoot,
    fixture.root,
    process.platform === "win32" ? "junction" : "dir",
  );
  assert.equal(lstatSync(fixture.root).isSymbolicLink(), true);
  assert.equal(cleanupDisposableOfflineFixture(fixture), false);
  assert.equal(existsSync(movedRoot), true);
});

test("sandbox preflight is UNVERIFIED without runtime observations and never invents evidence", () => {
  const preflight = buildSandboxPreflight();
  assert.equal(preflight.status, "unverified");
  assert.equal(preflight.sandboxType, "unavailable");
  assert.equal(preflight.imageDigest, null);
  assert.equal(preflight.fixtureId, null);
  assert.equal(preflight.cleanupVerified, false);
  assert.deepEqual(preflight.sanitizedReasons, ["runtime-provider-unavailable"]);
});

test("sandbox preflight requires every isolation, resource, and cleanup check", () => {
  const evidence = unobservedEvidence();
  const partialChecks = { ...evidence.checks, fixtureReadOnly: "verified" as const };
  const incomplete = buildSandboxPreflight({ ...evidence, checks: partialChecks });
  assert.equal(incomplete.filesystemIsolation, "unverified");
  assert.equal(incomplete.status, "unverified");
  assert.equal(incomplete.cleanupVerified, false);

  const blockedChecks = { ...evidence.checks, dnsEgressBlocked: "blocked" as const };
  const blocked = buildSandboxPreflight({ ...evidence, checks: blockedChecks });
  assert.equal(blocked.egressPolicy, "blocked");
  assert.equal(blocked.status, "blocked");

  const cleanupBlocked = {
    ...evidence.checks,
    ownedFixtureRemoved: "blocked" as const,
  };
  const unsafeCleanup = buildSandboxPreflight({
    ...evidence,
    checks: cleanupBlocked,
  });
  assert.equal(unsafeCleanup.cleanupVerified, false);
  assert.equal(unsafeCleanup.status, "blocked");
});

test("sandbox diagnostics contain only fixed reason codes, never paths or fixture content", () => {
  const evidence = unobservedEvidence();
  const checks = { ...evidence.checks, credentialEnvironmentAbsent: "blocked" as const };
  const result = buildSandboxPreflight({
    ...evidence,
    fixtureId: "C:\\Users\\private\\repo",
    imageDigest: "private-image",
    checks,
  });
  const serialized = JSON.stringify(result);
  assert.equal(result.status, "blocked");
  assert.equal(result.fixtureId, null);
  assert.equal(result.imageDigest, null);
  assert.equal(serialized.includes("C:\\Users"), false);
  assert.equal(serialized.includes("private-image"), false);
  assert.equal(serialized.includes("NOT-A-CREDENTIAL"), false);
  assert.equal(serialized.includes("credentialEnvironmentAbsent:blocked"), true);
});

test("operator command without a verified provider reports UNVERIFIED without touching runtime", () => {
  let output = "";
  let error = "";
  assert.equal(
    runOfflineSandboxPreflight(
      ["--offline-fixture"],
      (text) => { output += text; },
      (text) => { error += text; },
    ),
    1,
  );
  assert.equal(JSON.parse(output).status, "unverified");
  assert.equal(error, "");
  assert.equal(
    runOfflineSandboxPreflight(
      [],
      (text) => { output += text; },
      (text) => { error += text; },
    ),
    2,
  );
  assert.match(error, /Usage:/);
});
