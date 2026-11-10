import { ALLOWED_REPOSITORY } from "./contracts.ts";

export type GitHubCheckConclusion =
  | "success"
  | "failure"
  | "neutral"
  | "cancelled"
  | "skipped"
  | "stale"
  | "timed_out"
  | "action_required"
  | null;

export interface GitHubCheckRun {
  readonly id: number;
  readonly name: string;
  readonly headSha: string;
  readonly status: "queued" | "in_progress" | "completed";
  readonly conclusion: GitHubCheckConclusion;
}

export interface GitHubExactHeadStatus {
  readonly schemaVersion: 1;
  readonly provider: "github-check-runs-read-only";
  readonly repository: string;
  readonly requestedSha: string;
  readonly observedSha: string | null;
  readonly checks: readonly GitHubCheckRun[];
  readonly overall: "success" | "failure" | "pending" | "unknown";
}

export interface GitHubReadOnlyChecksApi {
  listCheckRuns(
    repository: string,
    exactSha: string,
    signal?: AbortSignal,
  ): Promise<unknown>;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function validConclusion(value: unknown): value is GitHubCheckConclusion {
  return (
    value === null ||
    [
      "success",
      "failure",
      "neutral",
      "cancelled",
      "skipped",
      "stale",
      "timed_out",
      "action_required",
    ].includes(value as string)
  );
}

function parseCheckRuns(
  input: unknown,
  exactSha: string,
): readonly GitHubCheckRun[] | undefined {
  if (!isRecord(input) || !Array.isArray(input.check_runs)) {
    return undefined;
  }

  const checks: GitHubCheckRun[] = [];
  const checkIds = new Set<number>();
  for (const candidate of input.check_runs) {
    if (
      !isRecord(candidate) ||
      !Number.isSafeInteger(candidate.id) ||
      typeof candidate.name !== "string" ||
      candidate.name.length === 0 ||
      typeof candidate.head_sha !== "string" ||
      !/^[0-9a-f]{40}$/i.test(candidate.head_sha) ||
      candidate.head_sha.toLowerCase() !== exactSha.toLowerCase() ||
      !["queued", "in_progress", "completed"].includes(candidate.status as string) ||
      !validConclusion(candidate.conclusion)
    ) {
      return undefined;
    }
    if (checkIds.has(candidate.id as number)) {
      return undefined;
    }
    checkIds.add(candidate.id as number);
    checks.push({
      id: candidate.id as number,
      name: candidate.name,
      headSha: candidate.head_sha.toLowerCase(),
      status: candidate.status as GitHubCheckRun["status"],
      conclusion: candidate.conclusion,
    });
  }

  if (!Number.isSafeInteger(input.total_count) || input.total_count !== checks.length) {
    return undefined;
  }
  return checks;
}

function aggregate(
  checks: readonly GitHubCheckRun[],
): GitHubExactHeadStatus["overall"] {
  if (checks.length === 0) {
    return "unknown";
  }
  if (checks.some((check) => check.status !== "completed")) {
    return "pending";
  }
  if (checks.some((check) => ["failure", "timed_out", "stale"].includes(check.conclusion ?? ""))) {
    return "failure";
  }
  return checks.every((check) => check.conclusion === "success")
    ? "success"
    : "unknown";
}

export class GitHubExactHeadStatusProvider {
  private readonly api: GitHubReadOnlyChecksApi;

  constructor(api: GitHubReadOnlyChecksApi) {
    this.api = api;
  }

  async read(
    repository: string,
    exactSha: string,
    signal?: AbortSignal,
  ): Promise<GitHubExactHeadStatus | undefined> {
    if (
      repository !== ALLOWED_REPOSITORY ||
      !/^[0-9a-f]{40}$/i.test(exactSha) ||
      signal?.aborted
    ) {
      return undefined;
    }
    const raw = await this.api.listCheckRuns(repository, exactSha, signal);
    if (signal?.aborted) {
      return undefined;
    }
    const checks = parseCheckRuns(raw, exactSha);
    if (checks === undefined) {
      return undefined;
    }
    const observedSha =
      checks.length === 0 ? null : checks[0].headSha;
    return Object.freeze({
      schemaVersion: 1,
      provider: "github-check-runs-read-only",
      repository,
      requestedSha: exactSha.toLowerCase(),
      observedSha,
      checks: Object.freeze(checks),
      overall: aggregate(checks),
    });
  }
}
