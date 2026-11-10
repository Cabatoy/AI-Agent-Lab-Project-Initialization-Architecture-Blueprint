import type {
  CopilotClient,
  CopilotSession,
  SessionConfig,
  SessionEvent,
} from "@github/copilot-sdk";
import {
  PINNED_SDK_VERSION,
  type CopilotTransport,
  type TransportEvent,
  type TransportSessionOptions,
} from "./contracts.ts";

type SdkSession = Pick<
  CopilotSession,
  "sessionId" | "sendAndWait" | "abort" | "disconnect" | "on"
>;
type SdkClient = {
  createSession(config: SessionConfig): Promise<SdkSession>;
};

function normalizeType(type: SessionEvent["type"]): TransportEvent["type"] {
  switch (type) {
    case "assistant.message":
    case "session.idle":
    case "session.error":
    case "permission.requested":
      return type;
    default:
      return "unknown";
  }
}

function normalizeEvent(sessionId: string, event: SessionEvent): TransportEvent {
  return Object.freeze({
    id: event.id,
    parentId: event.parentId,
    type: normalizeType(event.type),
    sessionId,
    timestamp: event.timestamp,
  });
}

export class CopilotSdkTransport implements CopilotTransport {
  readonly sdkVersion = PINNED_SDK_VERSION;
  private readonly sessions = new Map<string, SdkSession>();
  private readonly events = new Map<string, TransportEvent[]>();
  private readonly client: SdkClient;

  constructor(client: SdkClient) {
    this.client = client;
  }

  async startSession(options: TransportSessionOptions): Promise<string> {
    const config: SessionConfig = {
      workingDirectory: options.worktreePath,
      availableTools: [],
      skipCustomInstructions: true,
      onPermissionRequest: () => ({ kind: "reject" }),
    };
    const session = await this.client.createSession(config);
    this.sessions.set(session.sessionId, session);
    this.events.set(session.sessionId, []);
    return session.sessionId;
  }

  async submitReviewedTask(
    sessionId: string,
    prompt: string,
    timeoutMs: number,
    signal?: AbortSignal,
  ): Promise<void> {
    if (signal?.aborted) {
      throw new Error("SDK task submission cancelled");
    }

    const session = this.requireSession(sessionId);
    let abortOperation: Promise<void> | undefined;
    const requestAbort = () => {
      abortOperation ??= session.abort();
      return abortOperation;
    };

    let removeAbortListener: (() => void) | undefined;
    const cancellation = signal === undefined
      ? undefined
      : new Promise<never>((_resolve, reject) => {
          const onAbort = () => {
            void requestAbort().then(
              () => reject(new Error("SDK task submission cancelled")),
              reject,
            );
          };
          signal.addEventListener("abort", onAbort, { once: true });
          removeAbortListener = () => signal.removeEventListener("abort", onAbort);
          if (signal.aborted) {
            onAbort();
          }
        });

    try {
      const request = session.sendAndWait({ prompt }, timeoutMs);
      if (cancellation === undefined) {
        await request;
        return;
      }

      await Promise.race([request, cancellation]);
      if (signal?.aborted) {
        await requestAbort();
        throw new Error("SDK task submission cancelled");
      }
    } finally {
      removeAbortListener?.();
    }
  }

  subscribeEvents(
    sessionId: string,
    handler: (event: TransportEvent) => void,
  ): () => void {
    return this.requireSession(sessionId).on((event) => {
      const normalized = normalizeEvent(sessionId, event);
      const log = this.events.get(sessionId);
      if (log === undefined) {
        throw new Error("SDK evidence buffer is unavailable");
      }
      if (!log.some((saved) => saved.id === normalized.id)) {
        log.push(normalized);
      }
      handler(normalized);
    });
  }

  async cancel(sessionId: string): Promise<void> {
    await this.requireSession(sessionId).abort();
  }

  async finalize(sessionId: string): Promise<void> {
    const session = this.requireSession(sessionId);
    this.sessions.delete(sessionId);
    await session.disconnect();
  }

  async getEvidence(sessionId: string): Promise<readonly TransportEvent[]> {
    const events = this.events.get(sessionId);
    if (events === undefined) {
      throw new Error("SDK evidence is unavailable for this session");
    }
    return events.slice();
  }

  private requireSession(sessionId: string): SdkSession {
    const session = this.sessions.get(sessionId);
    if (session === undefined) {
      throw new Error("SDK session is not active");
    }
    return session;
  }
}
