import { act, cleanup, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { useCandidateReview } from "./useCandidateReview";
import type { CandidateItem } from "../types";

const { call } = vi.hoisted(() => ({ call: vi.fn() }));
vi.mock("../api", async importOriginal => ({
  ...await importOriginal<typeof import("../api")>(), operation: call,
}));
const envelope = (data: unknown) => ({ ok: true, data, requestId: "test", status: "completed" });
const candidate = (id: string): CandidateItem => ({
  candidateId: id, relativePath: id, physicalPath: id, kind: "gameRoot", reviewState: "pendingReview", revision: 1,
});
beforeEach(() => { call.mockReset(); });
afterEach(() => cleanup());

it("returns only the ids that succeeded when part of a batch fails", async () => {
  call.mockResolvedValue(envelope({ items: [
    { candidateId: "a", result: envelope({ reviewState: "accepted", revision: 2, gameId: "a" }) },
    { candidateId: "b", result: { ok: false, error: { message: "conflict" } } },
  ] }));
  const refresh = vi.fn(async () => {});
  const bump = vi.fn();
  const hook = renderHook(() => useCandidateReview(() => true, refresh, bump));
  const outcome = await hook.result.current([candidate("a"), candidate("b")], "accept");
  // 关键契约：部分失败不再整批抛出，成功项与失败项分别可见。
  expect(outcome.succeededIds).toEqual(["a"]);
  expect(outcome.accepted.map(item => item.candidate.candidateId)).toEqual(["a"]);
  expect(outcome.failureMessage).toContain("1 项失败");
  expect(outcome.failureMessage).toContain("conflict");
  expect(call).toHaveBeenCalledOnce();
  expect(refresh).toHaveBeenCalledOnce();
  expect(bump).toHaveBeenCalledOnce();
});

it("aligns batch results by candidateId, not by array index", async () => {
  call.mockResolvedValue(envelope({ items: [
    { candidateId: "b", result: envelope({ reviewState: "accepted", revision: 7, gameId: "b" }) },
  ] }));
  const hook = renderHook(() => useCandidateReview(() => true, async () => {}, () => {}));
  const outcome = await hook.result.current([candidate("a"), candidate("b")], "accept");
  // 后端重排且丢掉 a 的条目：b 必须记在 b 头上，a 记失败。按下标对齐会反过来。
  expect(outcome.succeededIds).toEqual(["b"]);
  expect(outcome.accepted.map(item => item.candidate.candidateId)).toEqual(["b"]);
  expect(outcome.failureMessage).toContain("1 项失败");
});

it("reports a missing batch entry as a failure instead of silently succeeding", async () => {
  call.mockResolvedValue(envelope({ items: [] }));
  const hook = renderHook(() => useCandidateReview(() => true, async () => {}, () => {}));
  const outcome = await hook.result.current([candidate("a")], "accept");
  expect(outcome.succeededIds).toEqual([]);
  expect(outcome.failureMessage).toContain("1 项失败");
});

it("collects successes item by item on an older Host", async () => {
  call.mockImplementation(async (_operation: string, params: { candidateId: string }) => {
    if (params.candidateId === "b") throw new Error("disk busy");
    return envelope({ reviewState: "ignored", revision: 3 });
  });
  const refresh = vi.fn(async () => {});
  const bump = vi.fn();
  const hook = renderHook(() => useCandidateReview(() => false, refresh, bump));
  const outcome = await hook.result.current([candidate("a"), candidate("b")], "ignore");
  expect(call.mock.calls.map(([name]) => name)).toEqual(["candidates.ignore", "candidates.ignore"]);
  expect(outcome.succeededIds).toEqual(["a"]);
  expect(outcome.accepted).toEqual([]);
  expect(outcome.failureMessage).toContain("1 项失败");
  // 非 accept 不 bump（相似提示只在加入时聚合）。
  expect(bump).not.toHaveBeenCalled();
  expect(refresh).toHaveBeenCalledOnce();
});

it("reports no failure message when every candidate succeeds", async () => {
  call.mockResolvedValue(envelope({ items: [
    { candidateId: "a", result: envelope({ reviewState: "deferred", revision: 2 }) },
    { candidateId: "b", result: envelope({ reviewState: "deferred", revision: 2 }) },
  ] }));
  const hook = renderHook(() => useCandidateReview(() => true, async () => {}, () => {}));
  const outcome = await act(() => hook.result.current([candidate("a"), candidate("b")], "defer"));
  expect(outcome.succeededIds).toEqual(["a", "b"]);
  expect(outcome.failureMessage).toBeNull();
});
