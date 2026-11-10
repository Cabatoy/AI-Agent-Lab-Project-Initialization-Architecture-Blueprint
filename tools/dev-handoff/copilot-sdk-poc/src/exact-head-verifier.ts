import { execFile } from "node:child_process";
import { createHash } from "node:crypto";
import { lstat, readdir, readFile, realpath } from "node:fs/promises";
import { isAbsolute, join, parse, relative, resolve, sep } from "node:path";
import { promisify } from "node:util";
import {
  ALLOWED_REPOSITORY,
  type ExactHeadVerifier,
  type ReviewedTask,
} from "./contracts.ts";
import { isReviewSignatureValid } from "./review-signature.ts";
import { parseReviewedTask } from "./validation.ts";

const execFileAsync = promisify(execFile);
const MAX_GIT_OUTPUT_BYTES = 256 * 1024;
const DEFAULT_GIT_TIMEOUT_MS = 15_000;
const MAX_GIT_TIMEOUT_MS = 30_000;

export interface GitCommandResult {
  readonly stdout: string;
}

export type GitCommandRunner = (
  cwd: string,
  args: readonly string[],
  timeoutMs: number,
  signal: AbortSignal,
) => Promise<GitCommandResult>;

export interface ReadOnlyGitVerifierOptions {
  readonly trustedReviewKeys: Readonly<Record<string, string>>;
  readonly timeoutMs?: number;
  readonly clock?: () => number;
  readonly runGit?: GitCommandRunner;
}

export type ExactHeadRejectionStage =
  | "cancelled"
  | "task-validation"
  | "review-signature"
  | "worktree-path"
  | "git-root"
  | "origin"
  | "head"
  | "branch"
  | "worktree-registration"
  | "tree-index"
  | "worktree-bytes"
  | "permitted-path"
  | "git-or-filesystem-error";

export type ExactHeadVerificationResult =
  | { readonly verified: true }
  | { readonly verified: false; readonly stage: ExactHeadRejectionStage };

function rejected(stage: ExactHeadRejectionStage): ExactHeadVerificationResult {
  return Object.freeze({ verified: false, stage });
}

function safeGitEnvironment(): NodeJS.ProcessEnv {
  return {
    PATH: process.env.PATH,
    PATHEXT: process.platform === "win32" ? process.env.PATHEXT : undefined,
    SYSTEMROOT: process.env.SystemRoot ?? process.env.SYSTEMROOT,
    WINDIR: process.env.WINDIR,
    GIT_CONFIG_NOSYSTEM: "1",
    GIT_CONFIG_GLOBAL: process.platform === "win32" ? "NUL" : "/dev/null",
    GIT_OPTIONAL_LOCKS: "0",
    GIT_NO_REPLACE_OBJECTS: "1",
    GIT_TERMINAL_PROMPT: "0",
  };
}

const runGitProcess: GitCommandRunner = async (cwd, args, timeoutMs, signal) => {
  const { stdout } = await execFileAsync(
    "git",
    [
      "-c",
      "core.fsmonitor=false",
      "-c",
      "core.untrackedCache=false",
      ...args,
    ],
    {
      cwd,
      env: safeGitEnvironment(),
      encoding: "utf8",
      windowsHide: true,
      maxBuffer: MAX_GIT_OUTPUT_BYTES,
      timeout: timeoutMs,
      signal,
    },
  );
  return { stdout };
};

function samePath(left: string, right: string): boolean {
  const normalizedLeft = resolve(left);
  const normalizedRight = resolve(right);
  return process.platform === "win32"
    ? normalizedLeft.toLowerCase() === normalizedRight.toLowerCase()
    : normalizedLeft === normalizedRight;
}

async function hasSymlinkPathComponent(path: string): Promise<boolean> {
  const { root } = parse(path);
  let current = root;
  for (const segment of path.slice(root.length).split(sep).filter(Boolean)) {
    current = join(current, segment);
    if ((await lstat(current)).isSymbolicLink()) {
      return true;
    }
  }
  return false;
}

function isWithin(root: string, candidate: string): boolean {
  const pathFromRoot = relative(root, candidate);
  return (
    pathFromRoot === "" ||
    (pathFromRoot !== ".." &&
      !pathFromRoot.startsWith(`..${sep}`) &&
      !isAbsolute(pathFromRoot))
  );
}

function isAllowedOrigin(value: string): boolean {
  const repositoryPath = ALLOWED_REPOSITORY.toLowerCase();
  const candidates = [
    `https://github.com/${repositoryPath}`,
    `ssh://git@github.com/${repositoryPath}`,
    `git@github.com:${repositoryPath}`,
  ];
  const normalized = value.trim().replace(/\.git$/i, "").toLowerCase();
  return candidates.some((candidate) => normalized === candidate);
}

function parseWorktrees(output: string): Array<{
  path: string;
  head: string | undefined;
  branch: string | undefined;
  locked: boolean;
  prunable: boolean;
}> {
  return output
    .trim()
    .split(/\r?\n\r?\n/)
    .filter(Boolean)
    .map((block) => {
      const path = block.match(/^worktree (.*)$/m)?.[1];
      return {
        path: path ?? "",
        head: block.match(/^HEAD ([0-9a-f]{40,64})$/im)?.[1]?.toLowerCase(),
        branch: block.match(/^branch refs\/heads\/(.+)$/m)?.[1],
        locked: /^locked(?: |$)/m.test(block),
        prunable: /^prunable(?: |$)/m.test(block),
      };
    });
}

async function runWithDeadline(
  runner: GitCommandRunner,
  cwd: string,
  args: readonly string[],
  timeoutMs: number,
  signal?: AbortSignal,
): Promise<string> {
  if (signal?.aborted) {
    throw new Error("verification cancelled");
  }

  const controller = new AbortController();
  let timeout: ReturnType<typeof setTimeout> | undefined;
  let abortHandler: (() => void) | undefined;
  const deadline = new Promise<never>((_, reject) => {
    timeout = setTimeout(() => {
      controller.abort();
      reject(new Error("Git verification timed out"));
    }, timeoutMs);
    if (signal !== undefined) {
      abortHandler = () => {
        controller.abort();
        reject(new Error("verification cancelled"));
      };
      signal.addEventListener("abort", abortHandler, { once: true });
    }
  });

  try {
    const result = await Promise.race([
      runner(cwd, args, timeoutMs, controller.signal),
      deadline,
    ]);
    if (signal?.aborted) {
      throw new Error("verification cancelled");
    }
    return result.stdout.trimEnd();
  } finally {
    if (timeout !== undefined) {
      clearTimeout(timeout);
    }
    if (abortHandler !== undefined) {
      signal?.removeEventListener("abort", abortHandler);
    }
  }
}

async function hasSymlinkOrPathEscape(root: string, relativePath: string): Promise<boolean> {
  let current = root;
  const segments = relativePath.split("/");
  for (const segment of segments) {
    current = join(current, segment);
    const fileStat = await lstat(current);
    if (fileStat.isSymbolicLink()) {
      return true;
    }
    const actualPath = await realpath(current);
    if (!isWithin(root, actualPath) || !samePath(actualPath, current)) {
      return true;
    }
  }
  return false;
}

interface TreeFile {
  readonly mode: string;
  readonly objectId: string;
}

function parseHeadTree(output: string): Map<string, TreeFile> | undefined {
  const files = new Map<string, TreeFile>();
  for (const record of output.split("\0").filter(Boolean)) {
    const separator = record.indexOf("\t");
    if (separator < 0) {
      return undefined;
    }
    const [mode, type, objectId] = record.slice(0, separator).split(" ");
    const path = record.slice(separator + 1);
    if (
      type !== "blob" ||
      !["100644", "100755"].includes(mode) ||
      !/^[0-9a-f]{40,64}$/i.test(objectId) ||
      path.length === 0 ||
      files.has(path)
    ) {
      return undefined;
    }
    files.set(path, { mode, objectId: objectId.toLowerCase() });
  }
  return files;
}

function parseIndex(output: string): Map<string, TreeFile> | undefined {
  const files = new Map<string, TreeFile>();
  for (const record of output.split("\0").filter(Boolean)) {
    const separator = record.indexOf("\t");
    if (separator < 0) {
      return undefined;
    }
    const [mode, objectId, stage] = record.slice(0, separator).split(" ");
    const path = record.slice(separator + 1);
    if (
      stage !== "0" ||
      !["100644", "100755"].includes(mode) ||
      !/^[0-9a-f]{40,64}$/i.test(objectId) ||
      path.length === 0 ||
      files.has(path)
    ) {
      return undefined;
    }
    files.set(path, { mode, objectId: objectId.toLowerCase() });
  }
  return files;
}

async function worktreeMatchesHead(
  root: string,
  headFiles: ReadonlyMap<string, TreeFile>,
  objectFormat: string,
  signal: AbortSignal,
): Promise<boolean> {
  if (!["sha1", "sha256"].includes(objectFormat)) {
    return false;
  }
  const gitEntry = await lstat(join(root, ".git"));
  if (gitEntry.isSymbolicLink() || !gitEntry.isFile()) {
    return false;
  }

  const seen = new Set<string>();
  const visit = async (directory: string, prefix: string): Promise<boolean> => {
    for (const entry of await readdir(directory, { withFileTypes: true })) {
      if (signal.aborted) {
        return false;
      }
      if (prefix === "" && entry.name === ".git") {
        continue;
      }
      const currentPath = join(directory, entry.name);
      const relativePath = prefix === "" ? entry.name : `${prefix}/${entry.name}`;
      const fileStat = await lstat(currentPath);
      if (fileStat.isSymbolicLink()) {
        return false;
      }
      const actualPath = await realpath(currentPath);
      if (!isWithin(root, actualPath) || !samePath(actualPath, currentPath)) {
        return false;
      }
      if (fileStat.isDirectory()) {
        if (!(await visit(currentPath, relativePath))) {
          return false;
        }
        continue;
      }
      if (!fileStat.isFile()) {
        return false;
      }

      const expected = headFiles.get(relativePath);
      if (expected === undefined) {
        return false;
      }
      const content = await readFile(currentPath, { signal });
      if (signal.aborted) {
        return false;
      }
      const gitBlobHash = createHash(objectFormat)
        .update(`blob ${content.length}\0`)
        .update(content)
        .digest("hex");
      if (gitBlobHash !== expected.objectId) {
        return false;
      }
      seen.add(relativePath);
    }
    return true;
  };

  return (await visit(root, "")) && seen.size === headFiles.size;
}

export class ReadOnlyGitExactHeadVerifier implements ExactHeadVerifier {
  private readonly trustedReviewKeys: Readonly<Record<string, string>>;
  private readonly timeoutMs: number;
  private readonly clock: () => number;
  private readonly runner: GitCommandRunner;

  constructor(options: ReadOnlyGitVerifierOptions) {
    this.trustedReviewKeys = options.trustedReviewKeys;
    this.timeoutMs = options.timeoutMs ?? DEFAULT_GIT_TIMEOUT_MS;
    this.clock = options.clock ?? Date.now;
    this.runner = options.runGit ?? runGitProcess;
    if (
      !Number.isInteger(this.timeoutMs) ||
      this.timeoutMs < 1 ||
      this.timeoutMs > MAX_GIT_TIMEOUT_MS
    ) {
      throw new Error(`timeoutMs must be between 1 and ${MAX_GIT_TIMEOUT_MS}`);
    }
  }

  async verify(task: ReviewedTask, signal?: AbortSignal): Promise<boolean> {
    return (await this.verifyWithDiagnostics(task, signal)).verified;
  }

  async verifyWithDiagnostics(
    task: ReviewedTask,
    signal?: AbortSignal,
  ): Promise<ExactHeadVerificationResult> {
    if (signal?.aborted) {
      return rejected("cancelled");
    }
    const controller = new AbortController();
    let timeout: ReturnType<typeof setTimeout> | undefined;
    let abortHandler: (() => void) | undefined;
    const interrupted = new Promise<ExactHeadVerificationResult>((resolveInterrupted) => {
      timeout = setTimeout(() => {
        controller.abort();
        resolveInterrupted(rejected("git-or-filesystem-error"));
      }, this.timeoutMs);
      if (signal !== undefined) {
        abortHandler = () => {
          controller.abort();
          resolveInterrupted(rejected("cancelled"));
        };
        signal.addEventListener("abort", abortHandler, { once: true });
      }
    });

    try {
      return await Promise.race([
        this.verifyInternal(task, controller.signal),
        interrupted,
      ]);
    } finally {
      if (timeout !== undefined) {
        clearTimeout(timeout);
      }
      if (abortHandler !== undefined) {
        signal?.removeEventListener("abort", abortHandler);
      }
      controller.abort();
    }
  }

  private async verifyInternal(
    task: ReviewedTask,
    signal: AbortSignal,
  ): Promise<ExactHeadVerificationResult> {
    let stage: ExactHeadRejectionStage = "task-validation";
    try {
      const validatedTask = parseReviewedTask(task, this.clock());
      stage = "review-signature";
      if (!isReviewSignatureValid(validatedTask, this.trustedReviewKeys)) {
        return rejected(stage);
      }
      if (signal.aborted) {
        return rejected("cancelled");
      }

      stage = "worktree-path";
      const worktreePath = resolve(validatedTask.worktreePath);
      if (!samePath(worktreePath, validatedTask.worktreePath)) {
        return rejected(stage);
      }
      if (
        process.platform === "win32" &&
        await hasSymlinkPathComponent(worktreePath)
      ) {
        return rejected(stage);
      }
      const canonicalRoot = await realpath(worktreePath);
      if (
        process.platform !== "win32" &&
        !samePath(worktreePath, canonicalRoot)
      ) {
        return rejected(stage);
      }
      const run = (args: readonly string[]) =>
        runWithDeadline(
          this.runner,
          canonicalRoot,
          args,
          this.timeoutMs,
          signal,
        );

      stage = "git-root";
      if ((await run(["rev-parse", "--is-inside-work-tree"])) !== "true") {
        return rejected(stage);
      }
      const gitRoot = await realpath(await run(["rev-parse", "--show-toplevel"]));
      if (!samePath(gitRoot, canonicalRoot)) {
        return rejected(stage);
      }

      const gitDirectory = resolve(canonicalRoot, await run(["rev-parse", "--git-dir"]));
      const commonDirectory = resolve(
        canonicalRoot,
        await run(["rev-parse", "--git-common-dir"]),
      );
      if (samePath(gitDirectory, commonDirectory)) {
        return rejected("worktree-registration");
      }

      stage = "origin";
      const origins = (await run(["remote", "get-url", "--all", "origin"]))
        .split(/\r?\n/)
        .filter(Boolean);
      if (origins.length !== 1 || !isAllowedOrigin(origins[0])) {
        return rejected(stage);
      }

      stage = "head";
      const headSha = (
        await run(["rev-parse", "--verify", "HEAD^{commit}"])
      ).toLowerCase();
      if (headSha !== validatedTask.baseSha.toLowerCase()) {
        return rejected(stage);
      }
      stage = "branch";
      const branch = await run(["symbolic-ref", "--quiet", "--short", "HEAD"]);
      if (branch !== validatedTask.branch) {
        return rejected(stage);
      }

      stage = "worktree-registration";
      const worktrees = parseWorktrees(await run(["worktree", "list", "--porcelain"]));
      const registeredWorktree = worktrees.find((worktree) =>
        samePath(worktree.path, canonicalRoot),
      );
      if (
        worktrees.length < 2 ||
        registeredWorktree === undefined ||
        registeredWorktree.branch !== validatedTask.branch ||
        registeredWorktree.head !== headSha ||
        registeredWorktree.locked ||
        registeredWorktree.prunable ||
        samePath(worktrees[0].path, canonicalRoot)
      ) {
        return rejected(stage);
      }

      stage = "tree-index";
      const headFiles = parseHeadTree(
        await run(["ls-tree", "-r", "-z", "--full-tree", "HEAD"]),
      );
      const indexFiles = parseIndex(await run(["ls-files", "--stage", "-z"]));
      if (
        headFiles === undefined ||
        indexFiles === undefined ||
        headFiles.size !== indexFiles.size ||
        [...headFiles].some(([path, file]) => {
          const indexFile = indexFiles.get(path);
          return (
            indexFile === undefined ||
            indexFile.mode !== file.mode ||
            indexFile.objectId !== file.objectId
          );
        })
      ) {
        return rejected(stage);
      }

      stage = "worktree-bytes";
      if (!(await worktreeMatchesHead(
          canonicalRoot,
          headFiles,
          await run(["rev-parse", "--show-object-format"]),
          signal,
        ))) {
        return rejected(stage);
      }

      stage = "permitted-path";
      for (const permittedPath of validatedTask.permittedPaths) {
        if (
          (await hasSymlinkOrPathEscape(canonicalRoot, permittedPath)) ||
          !headFiles.has(permittedPath)
        ) {
          return rejected(stage);
        }
      }
      return signal.aborted ? rejected("cancelled") : { verified: true };
    } catch {
      return rejected(stage);
    }
  }
}
