import { createHash, verify as verifySignature } from "node:crypto";
import type { ReviewedTask } from "./contracts.ts";

export function canonicalReviewPayload(task: ReviewedTask): Buffer {
  return Buffer.from(
    JSON.stringify({
      schema: "hic-reviewed-task-v1",
      issueNumber: task.issueNumber,
      taskId: task.taskId,
      repository: task.repository,
      worktreePath: task.worktreePath,
      branch: task.branch,
      baseSha: task.baseSha.toLowerCase(),
      permittedPaths: [...task.permittedPaths].sort(),
      reviewKeyId: task.reviewKeyId,
      expiresAt: task.expiresAt,
      maxDurationMs: task.maxDurationMs,
      maxTurns: task.maxTurns,
      oneShotOptIn: task.oneShotOptIn,
    }),
    "utf8",
  );
}

export function computeReviewDigest(task: ReviewedTask): string {
  return createHash("sha256").update(canonicalReviewPayload(task)).digest("hex");
}

export function isReviewSignatureValid(
  task: ReviewedTask,
  trustedPublicKeys: Readonly<Record<string, string>>,
): boolean {
  const publicKey = trustedPublicKeys[task.reviewKeyId];
  if (publicKey === undefined || computeReviewDigest(task) !== task.reviewDigest.toLowerCase()) {
    return false;
  }

  try {
    return verifySignature(
      null,
      Buffer.from(task.reviewDigest, "hex"),
      publicKey,
      Buffer.from(task.reviewSignature, "base64"),
    );
  } catch {
    return false;
  }
}
