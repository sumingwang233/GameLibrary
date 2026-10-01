import { act, cleanup, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { useGamesQuery } from "./useGamesQuery";
import { useLibraryEvents, affectedDomains } from "./useLibraryEvents";
import { useCandidateReview } from "./useCandidateReview";
import { useTagActions } from "./useTagActions";
import { useLibraryMetadata } from "./useLibraryMetadata";
import { emptyFilters } from "../state";
import { OperationError } from "../api";
import type { CandidateItem, GameItem, LibraryEvent } from "../types";

const { call, invalidate, show } = vi.hoisted(() => ({
  call: vi.fn(), invalidate: vi.fn(), show: vi.fn(),
}));
vi.mock("../api", async importOriginal => ({
  ...await importOriginal<typeof import("../api")>(), operation: call, invalidateAssets: invalidate, showMainWindow: show,
}));
const envelope = (data: unknown) => ({ ok: true, data, requestId: "test", status: "completed" });
const game = (id: number): GameItem => ({
  gameId: String(id), title: "game", rootPath: "C:/games", kind: "manualDirectory", favorite: false, revision: 1,
});
const candidate = (id: string): CandidateItem => ({
  candidateId: id, relativePath: id, physicalPath: id, kind: "gameRoot", reviewState: "pendingReview", revision: 1,
});
beforeEach(() => { vi.useFakeTimers(); call.mockReset(); invalidate.mockClear(); show.mockClear(); });
afterEach(() => { cleanup(); vi.useRealTimers(); });

it("refreshes only domains affected by events", () => {
  expect([...affectedDomains([{ type: "candidate.updated", sequence: 1 }])]).toEqual(["candidates", "notifications"]);
  expect([...affectedDomains([{ type: "tag.updated", sequence: 2 }])]).toEqual(["tags", "games"]);
});

it("establishes a tail cursor, merges events for 250ms, and cleans up on unmount", async () => {
  let pending!: (value: unknown) => void;
  let sequence = 10;
  call.mockImplementation(async (operation: string) => {
    if (operation === "events.read") return envelope({ nextCursor: 1, latestCursor: 10, items: [] });
    return new Promise(resolve => { pending = resolve; });
  });
  const refresh = vi.fn(async (_domains?: readonly string[]) => {});
  const bump = vi.fn();
  const initialize = vi.fn(async () => {});
  const supports = () => true;
  const setExit = vi.fn();
  const hook = renderHook(() => useLibraryEvents(initialize, supports, refresh, bump, setExit));
  await act(async () => {});
  expect(call.mock.calls[1][1].cursor).toBe(10);
  refresh.mockClear(); bump.mockClear();
  const publish = async (events: LibraryEvent[]) => {
    await act(async () => pending(envelope({ nextCursor: ++sequence, items: events })));
    await act(async () => { await vi.advanceTimersByTimeAsync(1); });
  };
  await publish([{ type: "game.updated", sequence: 11 }]);
  await publish([{ type: "tag.updated", sequence: 12 }]);
  expect(refresh).not.toHaveBeenCalled();
  await act(async () => { await vi.advanceTimersByTimeAsync(250); });
  expect(refresh).toHaveBeenCalledOnce();
  expect(new Set(refresh.mock.calls[0][0])).toEqual(new Set(["games", "tags"]));
  expect(bump).toHaveBeenCalledOnce();
  hook.unmount();
  await publish([{ type: "game.updated", sequence: 13 }]);
  expect(refresh).toHaveBeenCalledOnce();
});

it("rebuilds the cursor baseline after an epoch or cursor error", async () => {
  call.mockResolvedValueOnce(envelope({ nextCursor: 1, latestCursor: 5, items: [] }))
    .mockRejectedValueOnce(new OperationError("CursorExpired", "expired", { ok: false, data: null, requestId: "x", status: "failed" }))
    .mockResolvedValueOnce(envelope({ nextCursor: 1, latestCursor: 20, items: [] }));
  const initialize = async () => {};
  const supports = () => false;
  const refresh = vi.fn(async () => {});
  const bump = () => {};
  const exit = () => {};
  renderHook(() => useLibraryEvents(initialize, supports, refresh, bump, exit));
  await act(async () => {});
  expect(invalidate).toHaveBeenCalled();
  expect(call.mock.calls[2][1].cursor).toBeUndefined();
  expect(refresh).toHaveBeenCalledTimes(2);
});

it("preserves loaded pages on invalidation and drops responses for replaced filters", async () => {
  call.mockImplementation(async (_operation: string, params: { offset: number; sort: string }) =>
    envelope({ total: 120, items: Array.from({ length: 60 }, (_, i) => game(params.offset + i)) }));
  const hook = renderHook(({ token, sort }) => useGamesQuery({ ...emptyFilters, sort }, token, false),
    { initialProps: { token: 0, sort: "title" } });
  await act(async () => {});
  await act(async () => hook.result.current.loadMore());
  expect(hook.result.current.games).toHaveLength(120);
  hook.rerender({ token: 1, sort: "title" });
  await act(async () => {});
  expect(hook.result.current.games).toHaveLength(120);
  let old!: (value: unknown) => void;
  call.mockImplementationOnce(() => new Promise(resolve => { old = resolve; }));
  hook.rerender({ token: 2, sort: "title" });
  await act(async () => {});
  hook.rerender({ token: 2, sort: "recent" });
  await act(async () => old(envelope({ total: 1, items: [game(999)] })));
  expect(hook.result.current.games.some(item => item.gameId === "999")).toBe(false);
});

it("sends one candidate batch, reports partial failures, and refreshes retained successes", async () => {
  call.mockResolvedValue(envelope({ items: [
    { candidateId: "a", result: envelope({ reviewState: "accepted", revision: 2, gameId: "a" }) },
    { candidateId: "b", result: { ok: false, error: { message: "conflict" } } },
  ] }));
  const supports = () => true;
  const refresh = vi.fn(async () => {});
  const bump = vi.fn();
  const hook = renderHook(() => useCandidateReview(supports, refresh, bump));
  await expect(hook.result.current([candidate("a"), candidate("b")], "accept")).rejects.toThrow("1 项失败");
  expect(call).toHaveBeenCalledOnce();
  expect(call.mock.calls[0][0]).toBe("candidates.review_batch");
  expect(refresh).toHaveBeenCalledOnce();
  expect(bump).toHaveBeenCalledOnce();
});

it("metadata ignores an older response for the same domain", async () => {
  let old!: (value: unknown) => void;
  call.mockImplementation(async (operation: string) => {
    if (operation === "host.status") return envelope({ libraryInitialized: true });
    if (operation === "capabilities.get") return envelope({ availableOperations: [] });
    return envelope({ total: 2 });
  });
  const hook = renderHook(() => useLibraryMetadata());
  await act(async () => hook.result.current.initialize());
  call.mockImplementationOnce(() => new Promise(resolve => { old = resolve; }));
  let first!: Promise<void>;
  await act(async () => { first = hook.result.current.refreshDomains(["games"]); });
  await act(async () => hook.result.current.refreshDomains(["games"]));
  await act(async () => { old(envelope({ total: 99 })); await first; });
  expect(hook.result.current.gameTotal).toBe(2);
});

it("deduplicates launch exit attempts while polling an older Host", async () => {
  const exitEvent = { type: "launch.exited", sequence: 2, payload: { gameId: "g", attemptId: "a" } };
  call.mockResolvedValueOnce(envelope({ nextCursor: 1, latestCursor: 1, items: [] }))
    .mockResolvedValueOnce(envelope({ nextCursor: 2, items: [exitEvent, { ...exitEvent, sequence: 3 }] }))
    .mockResolvedValue(envelope({ nextCursor: 3, items: [exitEvent] }));
  const initialize = async () => {};
  const supports = () => false;
  const refresh = async () => {};
  const bump = () => {};
  const exit = vi.fn();
  renderHook(() => useLibraryEvents(initialize, supports, refresh, bump, exit));
  await act(async () => {});
  await act(async () => { await vi.advanceTimersByTimeAsync(2500); });
  expect(exit).toHaveBeenCalledOnce();
  expect(show).toHaveBeenCalledOnce();
  expect(call.mock.calls.every(([name]) => name === "events.read")).toBe(true);
});

it("falls back to individual candidate reviews on an older Host", async () => {
  call.mockResolvedValue(envelope({}));
  const supports = () => false;
  const refresh = vi.fn(async () => {});
  const bump = vi.fn();
  const hook = renderHook(() => useCandidateReview(supports, refresh, bump));
  await hook.result.current([candidate("a"), candidate("b")], "defer");
  expect(call.mock.calls.map(([name]) => name)).toEqual(["candidates.defer", "candidates.defer"]);
  expect(refresh).toHaveBeenCalledOnce();
});

it("reports atomic tag reorder failure and refreshes revisions", async () => {
  call.mockRejectedValue(new Error("revision conflict"));
  const supports = () => true;
  const refresh = vi.fn(async () => {});
  const hook = renderHook(() => useTagActions(supports, refresh, () => {}));
  await expect(hook.result.current.reorderTags([
    { tag: { tagId: "a", name: "a", kind: "user", revision: 1 }, sortOrder: 2 },
  ])).rejects.toThrow("revision conflict");
  expect(call.mock.calls[0][0]).toBe("tags.reorder");
  expect(refresh).toHaveBeenCalledOnce();
});
