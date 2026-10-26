import { act, cleanup, renderHook } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { retryTitleIds, useTitleTranslation, type TitleProgress } from "./useTitleTranslation";
import { OperationError } from "../api";

const { call } = vi.hoisted(() => ({ call: vi.fn() }));
// 通过 importOriginal 保留 OperationError 等真实导出：轮询按真实错误类型识别 NotFound。
vi.mock("../api", async importOriginal => ({
  ...await importOriginal<typeof import("../api")>(),
  operation: call,
  describeFailure: String,
}));
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
it("stops immediately when the job is gone and still runs onFinished", async () => {
  vi.useFakeTimers();
  call.mockImplementation(async name => {
    if (name === "titles.translate") return { data: { jobId: "job" } };
    throw new OperationError("NotFound", "作业不存在",
      { requestId: "x", ok: false, status: "failed", data: null });
  });
  const patch = vi.fn(); const finish = vi.fn(async () => {});
  const hook = renderHook(() => useTitleTranslation(patch, finish));
  await act(async () => { await hook.result.current.start(["a"]); });
  expect(call.mock.calls.filter(([name]) => name === "jobs.get")).toHaveLength(1);
  expect(finish).toHaveBeenCalledOnce();
  expect(hook.result.current.busy).toBe(false);
  expect(hook.result.current.error).toContain("不存在");
  await act(async () => { await vi.advanceTimersByTimeAsync(5000); });
  expect(call.mock.calls.filter(([name]) => name === "jobs.get")).toHaveLength(1);
});
it("stops after five consecutive poll failures instead of looping forever", async () => {
  vi.useFakeTimers();
  let polls = 0;
  call.mockImplementation(async name => {
    if (name === "titles.translate") return { data: { jobId: "job" } };
    polls += 1;
    throw new Error("network down");
  });
  const finish = vi.fn(async () => {});
  const hook = renderHook(() => useTitleTranslation(vi.fn(), finish));
  let running!: Promise<void>;
  await act(async () => { running = hook.result.current.start(["a"]); });
  await act(async () => { await vi.advanceTimersByTimeAsync(500 * 6); await running; });
  expect(polls).toBe(5);
  expect(finish).toHaveBeenCalledOnce();
  expect(hook.result.current.busy).toBe(false);
  expect(hook.result.current.error).toContain("5");
});
