export const PINNED_SDK_VERSION = "1.0.30";
export const ALLOWED_ISSUE_NUMBER = 75;
export const ALLOWED_REPOSITORY =
  "Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint";
export const MAX_SESSION_DURATION_MS = 120_000;

export interface ReviewedTask {
  readonly issueNumber: number;
  readonly taskId: string;
  readonly repository: string;
  readonly worktreePath: string;
  readonly branch: string;
  readonly baseSha: string;
  readonly permittedPaths: readonly string[];
  readonly reviewKeyId: string;
  readonly reviewDigest: string;
  readonly reviewSignature: string;
  readonly expiresAt: string;
  readonly maxDurationMs: number;
  readonly maxTurns: 1;
  readonly oneShotOptIn: true;
}

export type SessionEventType =
  | "assistant.message"
  | "session.idle"
  | "session.error"
  | "permission.requested"
  | "unknown";

export interface TransportEvent {
  readonly id: string;
  readonly parentId: string | null;
  readonly sequence?: number;
  readonly type: SessionEventType;
  readonly sessionId: string;
  readonly taskId?: string;
  readonly timestamp: string;
}

export interface TransportSessionOptions {
  readonly worktreePath: string;
}

export interface Evidence {
  readonly sdkVersion: string;
  readonly sessionId: string;
  readonly taskId: string;
  readonly baseSha: string;
  readonly events: readonly Pick<
    TransportEvent,
    "id" | "parentId" | "sequence" | "type" | "timestamp"
  >[];
  readonly rejectedEventCount: number;
  readonly idleObserved: boolean;
  readonly stopReason: "running" | "idle" | "failed" | "cancelled" | "finalized";
  readonly executionVerified: false;
}

export interface CopilotTransport {
  readonly sdkVersion: string;
  startSession(options: TransportSessionOptions): Promise<string>;
  submitReviewedTask(
    sessionId: string,
    prompt: string,
    timeoutMs: number,
    signal?: AbortSignal,
  ): Promise<void>;
  subscribeEvents(
    sessionId: string,
    handler: (event: TransportEvent) => void,
  ): () => void;
  cancel(sessionId: string): Promise<void>;
  finalize(sessionId: string): Promise<void>;
  getEvidence(sessionId: string): Promise<readonly TransportEvent[]>;
}

export interface ExactHeadVerifier {
  verify(task: ReviewedTask, signal?: AbortSignal): Promise<boolean>;
}
