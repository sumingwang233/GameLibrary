import { act, cleanup, renderHook } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { retryTitleIds, useTitleTranslation, type TitleProgress } from "./useTitleTranslation";

const { call } = vi.hoisted(() => ({ call: vi.fn() }));
vi.mock("../api", () => ({ operation: call, describeFailure: String }));
afterEach(() => { cleanup(); call.mockReset(); vi.useRealTimers(); });
const progress: TitleProgress = {
  total: 4, completed: 3, succeeded: 1, failed: 1, skipped: 1, pending: 1,
  items: [{ gameId: "a", status: "succeeded", patch: { gameId: "a", revision: 2, title: "夏日回忆" } },
    { gameId: "b", status: "failed", reason: "networkError" }, { gameId: "c", status: "skipped", reason: "alreadyTranslated" }],
  unprocessedGameIds: ["d"],
};
it("patches each success immediately, retains progress, and retries only failures and pending", async () => {
  vi.useFakeTimers();
  let polls = 0;
  call.mockImplementation(async name => ({ data: name === "titles.translate" ? { jobId: "job" } : { state: ++polls === 1 ? "running" : "failed", result: progress } }));
  const patch = vi.fn(); const finish = vi.fn(async () => {});
  const hook = renderHook(() => useTitleTranslation(patch, finish));
  let running!: Promise<void>;
  await act(async () => { running = hook.result.current.start(["a", "b", "c", "d"]); });
  expect(patch).toHaveBeenCalledWith(progress.items[0].patch);
  expect(hook.result.current.busy).toBe(true);
  expect(finish).not.toHaveBeenCalled();
  await act(async () => { await vi.advanceTimersByTimeAsync(500); await running; });
  expect(patch).toHaveBeenCalledOnce(); expect(finish).toHaveBeenCalledOnce();
  expect(retryTitleIds(progress)).toEqual(["b", "d"]);
  await act(async () => { await hook.result.current.retry(); });
  expect(call).toHaveBeenCalledWith("titles.translate", { gameIds: ["b", "d"], force: false }, "titles.translate:b,d:false");
});
it("cancels the active job and retains already committed successes", async () => {
  vi.useFakeTimers(); let cancelled = false;
  call.mockImplementation(async name => {
    if (name === "titles.translate") return { data: { jobId: "job" } };
    if (name === "jobs.cancel") { cancelled = true; return { data: {} }; }
    return { data: { state: cancelled ? "cancelled" : "running", result: progress } };
  });
  const patch = vi.fn();
  const hook = renderHook(() => useTitleTranslation(patch, async () => {}));
  let running!: Promise<void>;
  await act(async () => { running = hook.result.current.start(["a", "b", "c", "d"]); });
  await act(async () => { await hook.result.current.cancel(); });
  await act(async () => { await vi.advanceTimersByTimeAsync(500); await running; });
  expect(call).toHaveBeenCalledWith("jobs.cancel", { jobId: "job" }, "jobs.cancel:job");
  expect(hook.result.current.state).toBe("cancelled");
  expect(patch).toHaveBeenCalledOnce();
});
