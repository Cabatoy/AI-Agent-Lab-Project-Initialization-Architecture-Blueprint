import { isAbsolute, resolve } from "node:path";
import {
  ALLOWED_ISSUE_NUMBER,
  ALLOWED_REPOSITORY,
  MAX_SESSION_DURATION_MS,
  type ReviewedTask,
} from "./contracts.ts";

const taskKeys = new Set([
  "issueNumber",
  "taskId",
  "repository",
  "worktreePath",
  "branch",
  "baseSha",
  "permittedPaths",
  "reviewDigest",
  "expiresAt",
  "maxDurationMs",
  "maxTurns",
  "oneShotOptIn",
]);
const uuidPattern =
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const shaPattern = /^[0-9a-f]{40}$/i;
const digestPattern = /^[0-9a-f]{64}$/i;
const branchPattern = /^[A-Za-z0-9._/-]{1,128}$/;

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function validatePathList(value: unknown): readonly string[] {
  if (!Array.isArray(value) || value.length === 0 || value.length > 32) {
    throw new Error("permittedPaths must contain between 1 and 32 paths");
  }

  const paths = new Set<string>();
  for (const candidate of value) {
    if (
      typeof candidate !== "string" ||
      candidate.includes("\\") ||
      candidate.startsWith("/") ||
      candidate.includes(":") ||
      !/^[A-Za-z0-9._/-]+$/.test(candidate)
    ) {
      throw new Error("permittedPaths contains a non-canonical path");
    }

    const segments = candidate.split("/");
    if (
      segments.length < 2 ||
      !["docs", "docs-md"].includes(segments[0]) ||
      segments.some((segment) => segment === "" || segment === "." || segment === "..") ||
      !candidate.endsWith(".md")
    ) {
      throw new Error("permittedPaths must be Markdown files below docs/ or docs-md/");
    }

    if (paths.has(candidate)) {
      throw new Error("permittedPaths must not contain duplicates");
    }
    paths.add(candidate);
  }

  return [...paths];
}

export function parseReviewedTask(
  input: unknown,
  now: number,
): ReviewedTask {
  if (!isRecord(input)) {
    throw new Error("reviewed task must be an object");
  }

  if (Object.keys(input).some((key) => !taskKeys.has(key))) {
    throw new Error("reviewed task contains unsupported fields");
  }

  if (
    input.issueNumber !== ALLOWED_ISSUE_NUMBER ||
    input.repository !== ALLOWED_REPOSITORY
  ) {
    throw new Error("task is outside the HIC #75/repository allowlist");
  }
  if (typeof input.taskId !== "string" || !uuidPattern.test(input.taskId)) {
    throw new Error("taskId must be a UUID");
  }
  if (
    typeof input.worktreePath !== "string" ||
    !isAbsolute(input.worktreePath) ||
    resolve(input.worktreePath) !== input.worktreePath
  ) {
    throw new Error("worktreePath must be an absolute canonical path");
  }
  if (
    typeof input.branch !== "string" ||
    !branchPattern.test(input.branch) ||
    input.branch.startsWith("-") ||
    input.branch.startsWith("/") ||
    input.branch.endsWith("/") ||
    input.branch.includes("..") ||
    input.branch.includes("//") ||
    input.branch.includes("@{") ||
    input.branch.endsWith(".lock") ||
    input.branch.split("/").some((part) => part === "." || part === "..")
  ) {
    throw new Error("branch is invalid");
  }
  if (typeof input.baseSha !== "string" || !shaPattern.test(input.baseSha)) {
    throw new Error("baseSha must be a full Git SHA");
  }
  if (typeof input.reviewDigest !== "string" || !digestPattern.test(input.reviewDigest)) {
    throw new Error("reviewDigest must be a SHA-256 digest");
  }
  if (
    typeof input.expiresAt !== "string" ||
    !Number.isFinite(Date.parse(input.expiresAt)) ||
    Date.parse(input.expiresAt) <= now ||
    Date.parse(input.expiresAt) > now + 15 * 60_000
  ) {
    throw new Error("review approval is expired or has an excessive lifetime");
  }
  if (
    typeof input.maxDurationMs !== "number" ||
    !Number.isInteger(input.maxDurationMs) ||
    input.maxDurationMs < 1_000 ||
    input.maxDurationMs > MAX_SESSION_DURATION_MS
  ) {
    throw new Error("maxDurationMs exceeds the bounded session limit");
  }
  if (input.maxTurns !== 1 || input.oneShotOptIn !== true) {
    throw new Error("exactly one explicitly opted-in turn is required");
  }

  return Object.freeze({
    issueNumber: ALLOWED_ISSUE_NUMBER,
    taskId: input.taskId,
    repository: ALLOWED_REPOSITORY,
    worktreePath: input.worktreePath,
    branch: input.branch,
    baseSha: input.baseSha,
    permittedPaths: validatePathList(input.permittedPaths),
    reviewDigest: input.reviewDigest,
    expiresAt: input.expiresAt,
    maxDurationMs: input.maxDurationMs,
    maxTurns: 1,
    oneShotOptIn: true,
  });
}
