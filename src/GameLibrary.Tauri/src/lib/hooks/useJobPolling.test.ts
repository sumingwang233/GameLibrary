import { expect, it } from "vitest";
import { OperationError } from "../api";
import {
  MAX_CONSECUTIVE_POLL_FAILURES,
  createPollFailureTracker,
  isPermanentPollFailure,
  isTerminalJobState,
  pollFailureMessage,
} from "./useJobPolling";

const notFound = () => new OperationError("NotFound", "作业不存在",
  { requestId: "x", ok: false, status: "failed", data: null });
const network = () => new Error("network down");

it("treats NotFound as permanent and other errors as retryable", () => {
  expect(isPermanentPollFailure(notFound())).toBe(true);
  // 跨模块副本：用结构化 code 判断，不能依赖 instanceof。
  expect(isPermanentPollFailure({ code: "NotFound" })).toBe(true);
  expect(isPermanentPollFailure(network())).toBe(false);
  expect(isPermanentPollFailure(null)).toBe(false);
  expect(isPermanentPollFailure(undefined)).toBe(false);
});

it("recognises the terminal job states", () => {
  expect(["succeeded", "failed", "cancelled"].every(isTerminalJobState)).toBe(true);
  expect(isTerminalJobState("running")).toBe(false);
  expect(isTerminalJobState(undefined)).toBe(false);
});

it("stops after five consecutive failures and resets the budget on any success", () => {
  const tracker = createPollFailureTracker();
  for (let attempt = 1; attempt < MAX_CONSECUTIVE_POLL_FAILURES; attempt++) {
    expect(tracker.recordFailure(network())).toBe("transient");
  }
  expect(tracker.recordFailure(network())).toBe("exhausted");
  tracker.recordSuccess();
  expect(tracker.consecutive).toBe(0);
  for (let attempt = 1; attempt < MAX_CONSECUTIVE_POLL_FAILURES; attempt++) {
    expect(tracker.recordFailure(network())).toBe("transient");
  }
  expect(tracker.recordFailure(network())).toBe("exhausted");
});

it("returns permanent immediately without spending the retry budget", () => {
  const tracker = createPollFailureTracker(2);
  expect(tracker.recordFailure(network())).toBe("transient");
  expect(tracker.recordFailure(notFound())).toBe("permanent");
  expect(tracker.consecutive).toBe(0);
});

it("explains both stop reasons in readable Chinese", () => {
  expect(pollFailureMessage("permanent")).toContain("不存在");
  expect(pollFailureMessage("exhausted")).toContain(String(MAX_CONSECUTIVE_POLL_FAILURES));
});
