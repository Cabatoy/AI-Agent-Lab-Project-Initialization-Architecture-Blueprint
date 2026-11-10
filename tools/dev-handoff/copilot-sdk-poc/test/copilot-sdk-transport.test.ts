import assert from "node:assert/strict";
import test from "node:test";
import { CopilotSdkTransport } from "../src/copilot-sdk-transport.ts";

test("SDK transport aborts a pending send when its signal is cancelled", async () => {
  let abortCalls = 0;
  let finishSend: (() => void) | undefined;
  const session = {
    sessionId: "sdk-session-1",
    sendAndWait: () =>
      new Promise<undefined>((resolve) => {
        finishSend = () => resolve(undefined);
      }),
    abort: async () => {
      abortCalls += 1;
      finishSend?.();
    },
    disconnect: async () => {},
    on: () => () => {},
  };
  const transport = new CopilotSdkTransport({
    createSession: async () => session,
  });
  const sessionId = await transport.startSession({ worktreePath: "unused" });
  const controller = new AbortController();
  const submission = transport.submitReviewedTask(
    sessionId,
    "offline test",
    1_000,
    controller.signal,
  );

  controller.abort();

  await assert.rejects(submission, /cancelled/);
  assert.equal(abortCalls, 1);
});
