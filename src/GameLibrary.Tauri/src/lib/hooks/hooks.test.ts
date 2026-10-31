import { act, cleanup, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { useGamesQuery } from "./useGamesQuery";
import { useScanJobs } from "./useScanJobs";
import { useLibraryEvents, affectedDomains } from "./useLibraryEvents";
import { useCandidateReview } from "./useCandidateReview";
import { useTagActions } from "./useTagActions";
import { useLibraryMetadata } from "./useLibraryMetadata";
import { emptyFilters } from "../state";
import { OperationError } from "../api";
import type { CandidateItem, GameItem, LibraryEvent, RootItem } from "../types";

const { call, invalidate, show, visibility } = vi.hoisted(() => ({
  call: vi.fn(), invalidate: vi.fn(), show: vi.fn(),
  visibility: { current: undefined as ((visible: boolean) => void) | undefined },
}));
vi.mock("../windowVisibility", () => ({
  watchWindowVisibility: (changed: (visible: boolean) => void) => {
    visibility.current = changed;
    return () => { visibility.current = undefined; };
  },
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

it("sends both cover states to every page and resets paging when the cover filter changes", async () => {
  call.mockImplementation(async (_operation: string, params: { offset: number; hasCover?: boolean }) =>
    envelope({ total: params.hasCover === false ? 1 : 120,
      items: params.hasCover === false ? [game(200)] : Array.from({ length: 60 }, (_, i) => game(params.offset + i)) }));
  const hook = renderHook(({ hasCover }: { hasCover: boolean | null }) => useGamesQuery({ ...emptyFilters, hasCover }, 0, false),
    { initialProps: { hasCover: true as boolean | null } });
  await act(async () => {});
  await act(async () => hook.result.current.loadMore());
  expect(hook.result.current.games).toHaveLength(120);
  expect(call.mock.lastCall?.[1]).toMatchObject({ hasCover: true, offset: 60 });
  hook.rerender({ hasCover: false });
  await act(async () => {});
  expect(call.mock.lastCall?.[1]).toMatchObject({ hasCover: false, offset: 0 });
  expect(hook.result.current.games.map(item => item.gameId)).toEqual(["200"]);
  expect(hook.result.current.total).toBe(1);
  expect(hook.result.current.hasMore).toBe(false);
  hook.rerender({ hasCover: null });
  await act(async () => {});
  expect(call.mock.lastCall?.[1].hasCover).toBeUndefined();
  expect(hook.result.current.games).toHaveLength(60);
});

it("filters every page by played state, clears the old library during switches, and refreshes marked games", async () => {
  let unplayed = Array.from({ length: 61 }, (_, i) => ({ ...game(i), played: false }));
  let finishPlayed!: (value: unknown) => void;
  call.mockImplementation(async (_operation, params) => params.played
    ? new Promise(resolve => { finishPlayed = resolve; })
    : envelope({ total: unplayed.length, items: unplayed.slice(params.offset, params.offset + params.limit) }));
  const hook = renderHook(({ playedOnly, token }) => useGamesQuery({ ...emptyFilters, playedOnly }, token, false),
    { initialProps: { playedOnly: false, token: 0 } });
  await act(async () => {});
  await act(async () => hook.result.current.loadMore());
  expect(hook.result.current.games).toHaveLength(61);
  expect(call.mock.lastCall?.[1]).toMatchObject({ played: false, offset: 60 });
  hook.rerender({ playedOnly: true, token: 0 });
  await act(async () => {});
  expect(call.mock.lastCall?.[1]).toMatchObject({ played: true, offset: 0 });
  expect(hook.result.current.games).toEqual([]);
  expect(hook.result.current.loading).toBe(true);
  await act(async () => finishPlayed(envelope({ total: 1, items: [{ ...game(200), played: true }] })));
  expect(hook.result.current.games.map(item => item.gameId)).toEqual(["200"]);
  expect(hook.result.current.total).toBe(1);
  expect(hook.result.current.hasMore).toBe(false);
  hook.rerender({ playedOnly: false, token: 0 });
  await act(async () => {});
  unplayed = unplayed.slice(1); // The first game was marked played by a successful session or a detail edit.
  hook.rerender({ playedOnly: false, token: 1 });
  await act(async () => {});
  expect(hook.result.current.total).toBe(60);
  expect(hook.result.current.games.some(item => item.gameId === "0")).toBe(false);
});

it("discards a late played page after returning to the main library", async () => {
  let stale!: (value: unknown) => void;
  call.mockImplementation(async (_operation, params) => params.played
    ? new Promise(resolve => { stale = resolve; })
    : envelope({ total: 1, items: [game(1)] }));
  const hook = renderHook(({ playedOnly }) => useGamesQuery({ ...emptyFilters, playedOnly }, 0, false),
    { initialProps: { playedOnly: false } });
  await act(async () => {});
  hook.rerender({ playedOnly: true });
  await act(async () => {});
  hook.rerender({ playedOnly: false });
  await act(async () => stale(envelope({ total: 1, items: [game(200)] })));
  expect(hook.result.current.games.map(item => item.gameId)).toEqual(["1"]);
  expect(hook.result.current.total).toBe(1);
});

it("patches translated titles in loaded pages while refresh is suppressed and ignores stale revisions", async () => {
  call.mockResolvedValue(envelope({ total: 120, items: Array.from({ length: 60 }, (_, i) => game(i)) }));
  const hook = renderHook(({ suppress }) => useGamesQuery(emptyFilters, 0, suppress), { initialProps: { suppress: false } });
  await act(async () => {});
  hook.rerender({ suppress: true });
  act(() => hook.result.current.patch({ gameId: "20", revision: 2, title: "中文名" }));
  expect(hook.result.current.games).toHaveLength(60);
  expect(hook.result.current.games[20].title).toBe("中文名");
  act(() => hook.result.current.patch({ gameId: "20", revision: 1, title: "stale" }));
  expect(hook.result.current.games[20].title).toBe("中文名");
  expect(call).toHaveBeenCalledOnce();
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

it("coalesces hidden-window refreshes while continuing events and launch-exit wakeup", async () => {
  let reply!: (value: unknown) => void;
  let cursor = 10;
  call.mockImplementation(async (name: string) => name === "events.read"
    ? envelope({ nextCursor: 1, latestCursor: cursor, items: [] })
    : new Promise(resolve => { reply = resolve; }));
  const initialize = vi.fn(async () => {});
  const supports = () => true;
  const refresh = vi.fn(async (_domains?: readonly string[]) => {});
  const bump = vi.fn();
  const exit = vi.fn();
  renderHook(() => useLibraryEvents(initialize, supports, refresh, bump, exit));
  await act(async () => {});
  refresh.mockClear(); bump.mockClear(); show.mockClear();
  act(() => visibility.current?.(false));
  const publish = async (items: LibraryEvent[]) => {
    await act(async () => reply(envelope({ nextCursor: ++cursor, items })));
    await act(async () => { await vi.advanceTimersByTimeAsync(1); });
  };
  await publish([{ type: "game.updated", sequence: 11 }]);
  await publish([{ type: "tag.updated", sequence: 12 }]);
  await act(async () => { await vi.advanceTimersByTimeAsync(1000); });
  expect(refresh).not.toHaveBeenCalled();
  expect(bump).not.toHaveBeenCalled();
  await publish([{ type: "launch.exited", sequence: 13, payload: { gameId: "game", attemptId: "attempt" } }]);
  expect(exit).toHaveBeenCalledOnce();
  expect(show).toHaveBeenCalledOnce();
  expect(refresh).not.toHaveBeenCalled();
  await act(async () => visibility.current?.(true));
  expect(refresh).toHaveBeenCalledOnce();
  expect(new Set(refresh.mock.calls[0][0])).toEqual(new Set(["games", "tags"]));
  expect(bump).toHaveBeenCalledOnce();
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
  // v1.7.23 起批量审核不再整批抛错：部分失败通过 failureMessage 返回，
  // 成功项由 succeededIds 交给 ReviewList 只清除真正成功的勾选。
  const outcome = await hook.result.current([candidate("a"), candidate("b")], "accept");
  expect(outcome.succeededIds).toEqual(["a"]);
  expect(outcome.failureMessage).toContain("1 项失败");
  expect(outcome.accepted.map(item => item.candidate.candidateId)).toEqual(["a"]);
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
  expect(call.mock.calls.filter(([name]) => name === "games.list").every(([, params]) => params.played === false)).toBe(true);
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

it("loads candidate 1001 and refreshes the consumed window after insertion and deletion", async () => {
  let items = Array.from({ length: 1001 }, (_, i) => candidate(String(i)));
  call.mockImplementation(async (name, params) => {
    if (name === "host.status") return envelope({ libraryInitialized: true });
    if (name === "capabilities.get") return envelope({ availableOperations: [] });
    return envelope({ total: items.length, items: items.slice(params.offset, params.offset + params.limit) });
  });
  const hook = renderHook(() => useLibraryMetadata());
  await act(async () => hook.result.current.refreshDomains(["candidates"]));
  expect(hook.result.current.candidates).toHaveLength(1000);
  expect(hook.result.current.candidateTotal).toBe(1001);
  await act(async () => hook.result.current.loadMoreCandidates());
  expect(hook.result.current.candidates[hook.result.current.candidates.length - 1]?.candidateId).toBe("1000");
  expect(hook.result.current.candidateHasMore).toBe(false);
  items = [candidate("new"), ...items.slice(1)];
  await act(async () => hook.result.current.refreshDomains(["candidates"]));
  expect(hook.result.current.candidates).toHaveLength(1001);
  expect(hook.result.current.candidates[0].candidateId).toBe("new");
  expect(hook.result.current.candidates.some(item => item.candidateId === "0")).toBe(false);
  expect(hook.result.current.candidates[hook.result.current.candidates.length - 1]?.candidateId).toBe("1000");
});

it("stops candidate pagination when a repeated page makes no progress", async () => {
  const items = Array.from({ length: 1000 }, (_, i) => candidate(String(i)));
  call.mockImplementation(async name => name === "host.status" ? envelope({ libraryInitialized: true })
    : name === "capabilities.get" ? envelope({ availableOperations: [] }) : envelope({ total: 3000, items }));
  const hook = renderHook(() => useLibraryMetadata());
  await act(async () => hook.result.current.refreshDomains(["candidates"]));
  await act(async () => hook.result.current.loadMoreCandidates());
  expect(hook.result.current.candidates).toHaveLength(1000);
  expect(hook.result.current.candidateHasMore).toBe(false);
  await act(async () => hook.result.current.loadMoreCandidates());
  expect(call.mock.calls.filter(([name]) => name === "candidates.list")).toHaveLength(2);
});

it("ignores an older load-more page after a candidate refresh", async () => {
  let items = Array.from({ length: 1001 }, (_, i) => candidate(String(i)));
  let finish!: (value: unknown) => void;
  call.mockImplementation(async (name, params) => {
    if (name === "host.status") return envelope({ libraryInitialized: true });
    if (name === "capabilities.get") return envelope({ availableOperations: [] });
    if (params.offset === 1000) return new Promise(resolve => { finish = resolve; });
    return envelope({ total: items.length, items: items.slice(0, params.limit) });
  });
  const hook = renderHook(() => useLibraryMetadata());
  await act(async () => hook.result.current.refreshDomains(["candidates"]));
  let older!: Promise<void>;
  await act(async () => { older = hook.result.current.loadMoreCandidates(); });
  expect(hook.result.current.candidateLoading).toBe(true);
  items = [candidate("fresh")];
  await act(async () => hook.result.current.refreshDomains(["candidates"]));
  await act(async () => { finish(envelope({ total: 1001, items: [candidate("stale")] })); await older; });
  expect(hook.result.current.candidates.map(item => item.candidateId)).toEqual(["fresh"]);
  expect(hook.result.current.candidateTotal).toBe(1);
  expect(hook.result.current.candidateLoading).toBe(false);
  expect(hook.result.current.candidateHasMore).toBe(false);
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

it("preserves explicit null color patches and omits untouched colors", async () => {
  call.mockResolvedValue(envelope({}));
  const hook = renderHook(() => useTagActions(() => true, vi.fn(async () => {}), () => {}));
  const tag = { tagId: "white", name: "white", kind: "user", revision: 1, color: "#ffffff" };
  await hook.result.current.updateTag(tag, { color: null });
  expect(call.mock.calls[0][1]).toEqual({ tagId: "white", expectedRevision: 1, color: null });
  await hook.result.current.updateTag(tag, { starred: 1 });
  expect(call.mock.calls[1][1]).toEqual({ tagId: "white", expectedRevision: 1, starred: 1 });
});

// ---- 分页游标（v1.7.23）：服务端按原始行分页，前端去重后条数不能当 offset ----

it("advances the paging cursor by nextOffset instead of the deduplicated game count", async () => {
  // 第 2 页整页重复：去重后 games 仍是 5 条，远小于下一页的原始行 offset 10。
  const idsAt = (offset: number): number[] =>
    offset === 5 ? [0, 1] : Array.from({ length: 5 }, (_, i) => offset + i);
  call.mockImplementation(async (_operation: string, params: { offset: number }) =>
    envelope({ total: 20, nextOffset: params.offset + 5, hasMore: params.offset + 5 < 20,
      items: idsAt(params.offset).map(game) }));
  const hook = renderHook(() => useGamesQuery(emptyFilters, 0, false));
  await act(async () => {});
  expect(hook.result.current.games.map(item => item.gameId)).toEqual(["0", "1", "2", "3", "4"]);
  await act(async () => hook.result.current.loadMore());
  expect(call.mock.lastCall?.[1].offset).toBe(5);
  expect(hook.result.current.games).toHaveLength(5);
  expect(hook.result.current.hasMore).toBe(true);
  // 关键：必须请求 10，而不是拿去重后的 games.length(5) 再拉一次第 2 页。
  await act(async () => hook.result.current.loadMore());
  expect(call.mock.lastCall?.[1].offset).toBe(10);
  expect(hook.result.current.games).toHaveLength(10);
  await act(async () => hook.result.current.loadMore());
  expect(call.mock.lastCall?.[1].offset).toBe(15);
  expect(hook.result.current.games).toHaveLength(15);
  expect(hook.result.current.hasMore).toBe(false);
  const requests = call.mock.calls.filter(([name]) => name === "games.list").length;
  await act(async () => hook.result.current.loadMore());
  expect(call.mock.calls.filter(([name]) => name === "games.list")).toHaveLength(requests);
});

it("rehydrates loaded pages along the server cursor when a page repeats", async () => {
  const idsAt = (offset: number): number[] =>
    offset === 5 ? [0, 1, 2, 3, 4] : Array.from({ length: 5 }, (_, i) => offset + i);
  call.mockImplementation(async (_operation: string, params: { offset: number }) =>
    envelope({ total: 100, nextOffset: params.offset + 5, hasMore: true, items: idsAt(params.offset).map(game) }));
  const hook = renderHook(({ token }: { token: number }) => useGamesQuery(emptyFilters, token, false),
    { initialProps: { token: 0 } });
  await act(async () => {});
  await act(async () => hook.result.current.loadMore()); // offset 5 整页重复
  await act(async () => hook.result.current.loadMore()); // offset 10
  expect(hook.result.current.games).toHaveLength(10);
  hook.rerender({ token: 1 });
  await act(async () => {});
  // 补页：0 → 5（重复，条数不涨）→ 10；旧实现会用去重后的 5 反复请求 offset 5。
  const offsets = call.mock.calls.filter(([name]) => name === "games.list").map(([, params]) => params.offset);
  expect(offsets).toEqual([0, 5, 10, 0, 5, 10]);
  expect(hook.result.current.games).toHaveLength(10);
  expect(hook.result.current.hasMore).toBe(true);
});

it("trusts the server hasMore flag when deduplication hides rows", async () => {
  call.mockImplementation(async (_operation: string, params: { offset: number }) => params.offset === 0
    ? envelope({ total: 1000, nextOffset: 60, hasMore: true, items: [game(1)] })
    : envelope({ total: 1000, nextOffset: null, hasMore: false, items: [game(2)] }));
  const hook = renderHook(() => useGamesQuery(emptyFilters, 0, false));
  await act(async () => {});
  // 本地 games.length(1) 远小于 total(1000)，但 hasMore 必须以服务端为准。
  expect(hook.result.current.hasMore).toBe(true);
  await act(async () => hook.result.current.loadMore());
  expect(call.mock.lastCall?.[1].offset).toBe(60);
  expect(hook.result.current.games).toHaveLength(2);
  expect(hook.result.current.hasMore).toBe(false);
});

// ---- 扫描轮询（v1.7.23）：NotFound 只移除单个作业；连续失败有上限 ----

const scanRoot = (id: string): RootItem => ({ rootId: id, path: `C:/${id}`, revision: 1 });
const vanishedJob = () => Object.assign(new Error("job gone"), { code: "NotFound", name: "OperationError" });
const advanceScanPoll = async () => { await act(async () => { await vi.advanceTimersByTimeAsync(700); }); };

it("drops only the scan job the host no longer knows and finishes when all are gone", async () => {
  call.mockImplementation(async (name: string, params: { root?: string; jobId?: string }) => {
    if (name === "scan.start") return envelope({ jobId: params.root === "C:/a" ? "job-a" : "job-b" });
    if (params.jobId === "job-a") throw vanishedJob();
    return envelope({ state: "running", coverage: { scannedDirectories: 3, candidatesFound: 1 } });
  });
  const refreshMeta = vi.fn(async () => {});
  const bump = vi.fn();
  const setError = vi.fn();
  const hook = renderHook(() => useScanJobs([scanRoot("a"), scanRoot("b")], refreshMeta, bump, setError));
  await act(async () => { await hook.result.current.startScan(); });
  expect(hook.result.current.scanning).toBe(true);
  await advanceScanPoll();
  expect(hook.result.current.scanning).toBe(true); // 只移除了 job-a，job-b 仍被轮询
  expect(hook.result.current.scanProgress?.activeRoots).toBe(1);
  expect(setError).not.toHaveBeenCalled();
  // job-b 也到终态 ⇒ 正常收尾（清进度、刷新库）。
  call.mockImplementation(async (name: string) => name === "scan.start"
    ? envelope({})
    : envelope({ state: "succeeded", coverage: { scannedDirectories: 5, candidatesFound: 2 } }));
  await advanceScanPoll();
  expect(hook.result.current.scanning).toBe(false);
  expect(hook.result.current.scanProgress).toBeNull();
  expect(refreshMeta).toHaveBeenCalledOnce();
  expect(bump).toHaveBeenCalledOnce();
});

it("stops scan polling after five consecutive failures and reports a readable reason", async () => {
  call.mockImplementation(async (name: string) => {
    if (name === "scan.start") return envelope({ jobId: "job" });
    throw new Error("backend down");
  });
  const refreshMeta = vi.fn(async () => {});
  const bump = vi.fn();
  const setError = vi.fn();
  const hook = renderHook(() => useScanJobs([scanRoot("a")], refreshMeta, bump, setError));
  await act(async () => { await hook.result.current.startScan(); });
  for (let i = 0; i < 5; i++) await advanceScanPoll();
  expect(call.mock.calls.filter(([name]) => name === "scan.coverage")).toHaveLength(5);
  expect(setError).toHaveBeenCalledOnce();
  expect(String(setError.mock.calls[0][0])).toContain("5");
  expect(hook.result.current.scanning).toBe(false);
  expect(hook.result.current.scanProgress).toBeNull();
  const issued = call.mock.calls.length;
  await advanceScanPoll();
  await advanceScanPoll();
  expect(call.mock.calls).toHaveLength(issued); // 停止后不再每 700ms 打后端
});

it("resets the scan failure budget on a single successful poll", async () => {
  let polls = 0;
  call.mockImplementation(async (name: string) => {
    if (name === "scan.start") return envelope({ jobId: "job" });
    polls += 1;
    // 前 4 次失败、第 5 次成功清零、随后再失败 4 次：仍未到连续 5 次上限。
    if (polls === 5) return envelope({ state: "running", coverage: { scannedDirectories: 1, candidatesFound: 0 } });
    throw new Error("flaky");
  });
  const refreshMeta = vi.fn(async () => {});
  const bump = vi.fn();
  const setError = vi.fn();
  const hook = renderHook(() => useScanJobs([scanRoot("a")], refreshMeta, bump, setError));
  await act(async () => { await hook.result.current.startScan(); });
  for (let i = 0; i < 9; i++) await advanceScanPoll();
  expect(polls).toBe(9);
  expect(setError).not.toHaveBeenCalled();
  expect(hook.result.current.scanning).toBe(true);
});
