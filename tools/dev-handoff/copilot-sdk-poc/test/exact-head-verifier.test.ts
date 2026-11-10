import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { sign, generateKeyPairSync } from "node:crypto";
import {
  mkdirSync,
  mkdtempSync,
  realpathSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from "node:fs";
import { basename, join } from "node:path";
import { tmpdir } from "node:os";
import test from "node:test";
import type { ReviewedTask } from "../src/contracts.ts";
import { ReadOnlyGitExactHeadVerifier } from "../src/exact-head-verifier.ts";
import {
  canonicalReviewPayload,
  computeReviewDigest,
} from "../src/review-signature.ts";
import { parseReviewedTask } from "../src/validation.ts";

const now = Date.UTC(2026, 10, 10, 12, 0, 0);
const { privateKey, publicKey } = generateKeyPairSync("ed25519");
const trustedReviewKeys = { "test-key": publicKey.export({ type: "spki", format: "pem" }).toString() };

function git(cwd: string, ...args: string[]): string {
  return execFileSync("git", args, {
    cwd,
    encoding: "utf8",
    windowsHide: true,
  }).trim();
}

function signedTask(
  worktreePath: string,
  branch: string,
  baseSha: string,
  overrides: Partial<ReviewedTask> = {},
): ReviewedTask {
  const task = {
    issueNumber: 75,
    taskId: "11111111-2222-4333-8444-555555555555",
    repository: "Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint",
    worktreePath,
    branch,
    baseSha,
    permittedPaths: ["docs-md/readiness.md"],
    reviewKeyId: "test-key",
    reviewDigest: "",
    reviewSignature: "",
    expiresAt: new Date(now + 5 * 60_000).toISOString(),
    maxDurationMs: 60_000,
    maxTurns: 1 as const,
    oneShotOptIn: true as const,
    ...overrides,
  };
  const digest = computeReviewDigest(task);
  return parseReviewedTask(
    {
      ...task,
      reviewDigest: digest,
      reviewSignature: sign(null, Buffer.from(digest, "hex"), privateKey).toString("base64"),
    },
    now,
  );
}

function createFixture() {
  // Hosted Windows runners check out into a deeply nested directory. Keep disposable
  // Git fixtures in the OS temp root so Git's hook templates stay below MAX_PATH.
  // mkdtempSync reserves a unique identity; t.after removes only this fixture.
  const fixture = mkdtempSync(join(tmpdir(), "hic-git-v-"));
  const repository = join(fixture, "repo");
  const worktree = join(fixture, "worktree");
  mkdirSync(repository, { recursive: true });
  git(process.cwd(), "--version");
  git(process.cwd(), "init", "-q", repository);
  git(repository, "config", "user.name", "Offline Verifier Test");
  git(repository, "config", "user.email", "offline-verifier@example.invalid");
  git(repository, "config", "core.autocrlf", "false");
  git(repository, "config", "core.symlinks", "false");
  writeFileSync(join(repository, ".gitattributes"), "* -text -filter -ident\n");
  git(repository, "remote", "add", "origin", "https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint.git");
  mkdirSync(join(repository, "docs-md"));
  writeFileSync(join(repository, ".gitignore"), ".ignored-check\n");
  writeFileSync(join(repository, "docs-md", "readiness.md"), "fixture\n");
  git(repository, "add", ".gitattributes", ".gitignore", "docs-md/readiness.md");
  git(repository, "commit", "-q", "-m", "fixture");
  const baseSha = git(repository, "rev-parse", "HEAD");
  git(repository, "worktree", "add", "-q", "-b", "docs/verifier-fixture", worktree, baseSha);
  return {
    fixture,
    repository,
    worktree: realpathSync(worktree),
    baseSha,
  };
}

function verifier(overrides: {
  timeoutMs?: number;
  runGit?: ConstructorParameters<typeof ReadOnlyGitExactHeadVerifier>[0]["runGit"];
} = {}): ReadOnlyGitExactHeadVerifier {
  return new ReadOnlyGitExactHeadVerifier({
    trustedReviewKeys,
    clock: () => now,
    ...overrides,
  });
}

async function assertVerified(task: ReviewedTask): Promise<void> {
  const result = await verifier().verifyWithDiagnostics(task);
  assert.equal(
    result.verified,
    true,
    result.verified ? undefined : `verifier rejected at stage: ${result.stage}`,
  );
}

test("read-only verifier accepts only the signed, clean exact linked worktree", async (t) => {
  const fixture = createFixture();
  t.after(() => rmSync(fixture.fixture, { recursive: true, force: true }));

  const exactTask = signedTask(
    fixture.worktree,
    "docs/verifier-fixture",
    fixture.baseSha,
  );
  await assertVerified(exactTask);

  writeFileSync(join(fixture.worktree, "docs-md", "readiness.md"), "dirty\n");
  assert.equal(await verifier().verify(exactTask), false);
  writeFileSync(join(fixture.worktree, "docs-md", "readiness.md"), "fixture\n");
  await assertVerified(exactTask);

  writeFileSync(join(fixture.worktree, ".ignored-check"), "ignored fixture\n");
  assert.equal(await verifier().verify(exactTask), false);
  rmSync(join(fixture.worktree, ".ignored-check"));
  writeFileSync(join(fixture.worktree, "docs-md", "untracked.md"), "untracked\n");
  assert.equal(await verifier().verify(exactTask), false);
  rmSync(join(fixture.worktree, "docs-md", "untracked.md"));
  writeFileSync(join(fixture.worktree, "docs-md", "readiness.md"), "staged\n");
  git(fixture.worktree, "add", "docs-md/readiness.md");
  assert.equal(await verifier().verify(exactTask), false);
  git(fixture.worktree, "reset", "--hard", "-q", "HEAD");
  await assertVerified(exactTask);

  git(
    fixture.repository,
    "remote",
    "set-url",
    "origin",
    "https://example.invalid/attacker/repo.git",
  );
  assert.equal(await verifier().verify(exactTask), false);
  git(
    fixture.repository,
    "remote",
    "set-url",
    "origin",
    "https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint.git",
  );

  assert.equal(
    await verifier().verify(signedTask(
      fixture.worktree,
      "docs/another-branch",
      fixture.baseSha,
    )),
    false,
  );
  assert.equal(
    await verifier().verify(signedTask(
      fixture.worktree,
      "docs/verifier-fixture",
      "f".repeat(40),
    )),
    false,
  );
  assert.equal(
    await verifier().verify(signedTask(
      fixture.worktree,
      "docs/verifier-fixture",
      fixture.baseSha,
      { permittedPaths: ["docs-md/not-committed.md"] },
    )),
    false,
  );

  const mainWorktreeTask = signedTask(
    fixture.repository,
    git(fixture.repository, "branch", "--show-current"),
    fixture.baseSha,
  );
  assert.equal(await verifier().verify(mainWorktreeTask), false);
});

test("verifier rejects Git symlinks and untrusted or stale review signatures", async (t) => {
  const fixture = createFixture();
  t.after(() => rmSync(fixture.fixture, { recursive: true, force: true }));
  const exactTask = signedTask(
    fixture.worktree,
    "docs/verifier-fixture",
    fixture.baseSha,
  );
  await assertVerified(exactTask);

  const escapedPath = join(fixture.worktree, "docs-md", "escape.md");
  writeFileSync(escapedPath, "../../outside.md");
  const symlinkBlob = execFileSync(
    "git",
    ["hash-object", "-w", "--stdin"],
    {
      cwd: fixture.worktree,
      encoding: "utf8",
      input: "../../outside.md",
      windowsHide: true,
    },
  ).trim();
  git(
    fixture.worktree,
    "update-index",
    "--add",
    "--cacheinfo",
    `120000,${symlinkBlob},docs-md/escape.md`,
  );
  git(fixture.worktree, "commit", "-q", "-m", "symlink fixture");
  const linkedSha = git(fixture.worktree, "rev-parse", "HEAD");
  const symlinkTask = signedTask(
    fixture.worktree,
    "docs/verifier-fixture",
    linkedSha,
    { permittedPaths: ["docs-md/escape.md"] },
  );
  assert.equal(await verifier().verify(symlinkTask), false);

  const unsignedInput = {
    ...exactTask,
    reviewSignature: Buffer.alloc(64).toString("base64"),
  };
  assert.equal(await verifier().verify(unsignedInput), false);

  const alteredScope = {
    ...exactTask,
    permittedPaths: ["docs-md/other.md"],
  };
  assert.equal(await verifier().verify(alteredScope), false);
  assert.notEqual(computeReviewDigest(alteredScope), exactTask.reviewDigest);
  assert.equal(canonicalReviewPayload(exactTask).length > 0, true);
});

test("verifier fails closed on cancellation, timeout, and missing trusted keys", async (t) => {
  const fixture = createFixture();
  t.after(() => rmSync(fixture.fixture, { recursive: true, force: true }));
  const exactTask = signedTask(
    fixture.worktree,
    "docs/verifier-fixture",
    fixture.baseSha,
  );
  const neverCompletes = (
    _cwd: string,
    _args: readonly string[],
    _timeoutMs: number,
    signal: AbortSignal,
  ) =>
    new Promise<{ stdout: string }>((_resolve, reject) => {
      signal.addEventListener("abort", () => reject(new Error("aborted")), { once: true });
    });

  assert.equal(
    await verifier({ timeoutMs: 20, runGit: neverCompletes }).verify(exactTask),
    false,
  );
  const controller = new AbortController();
  const cancelled = verifier({ runGit: neverCompletes }).verify(exactTask, controller.signal);
  controller.abort();
  assert.equal(await cancelled, false);

  const untrustedVerifier = new ReadOnlyGitExactHeadVerifier({
    trustedReviewKeys: {},
    clock: () => now,
  });
  assert.equal(await untrustedVerifier.verify(exactTask), false);

  const expiringTask = signedTask(
    fixture.worktree,
    "docs/verifier-fixture",
    fixture.baseSha,
    { expiresAt: new Date(now + 10 * 60_000).toISOString() },
  );
  const expiredClockVerifier = new ReadOnlyGitExactHeadVerifier({
    trustedReviewKeys,
    clock: () => now + 11 * 60_000,
  });
  assert.equal(await expiredClockVerifier.verify(expiringTask), false);
});

test("Windows case aliases remain valid while junction/reparse path aliases are rejected", async (t) => {
  const fixture = createFixture();
  t.after(() => rmSync(fixture.fixture, { recursive: true, force: true }));

  if (process.platform === "win32") {
    const caseAlias = fixture.worktree.toUpperCase();
    await assertVerified(signedTask(
      caseAlias,
      "docs/verifier-fixture",
      fixture.baseSha,
    ));
  }

  const junction = join(fixture.fixture, "worktree-alias");
  symlinkSync(
    fixture.fixture,
    junction,
    process.platform === "win32" ? "junction" : "dir",
  );
  const aliasedWorktree = join(junction, basename(fixture.worktree));
  const result = await verifier().verifyWithDiagnostics(signedTask(
    aliasedWorktree,
    "docs/verifier-fixture",
    fixture.baseSha,
  ));
  assert.deepEqual(result, { verified: false, stage: "worktree-path" });
});
