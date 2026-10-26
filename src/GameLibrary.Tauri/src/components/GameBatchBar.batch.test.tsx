import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { GameBatchBar } from "./GameBatchBar";
import type { GameItem, TagItem } from "../lib/types";

/**
 * optimize-v1.7.23 批量 IPC 回归：GameBatchBar 不再逐游戏 games.get + 单项操作，
 * 而是每类操作一次批量调用（单批 ≤1000，实现取 500 分块）。
 */
const { operation } = vi.hoisted(() => ({ operation: vi.fn() }));
vi.mock("../lib/api", async importOriginal => ({
  ...await importOriginal<typeof import("../lib/api")>(),
  operation,
  describeFailure: (cause: unknown) => cause instanceof Error ? cause.message : "操作失败",
}));
afterEach(() => { cleanup(); operation.mockReset(); });

const tags: TagItem[] = [
  { tagId: "tag-rpg", kind: "user", name: "RPG", revision: 1 },
  { tagId: "tag-action", kind: "user", name: "Action", revision: 1 },
];

function makeGames(count: number): GameItem[] {
  return Array.from({ length: count }, (_, index) => ({
    gameId: `game-${index + 1}`,
    title: `Game ${index + 1}`,
    rootPath: `D:/games/${index + 1}`,
    kind: "folderGame",
    favorite: false,
    revision: index + 1,
  }));
}

function allOk(params: { items: Array<{ gameId: string }> }) {
  return {
    data: {
      items: params.items.map(item => ({ gameId: item.gameId, ok: true })),
      updated: params.items.length,
      failed: 0,
    },
  };
}

function renderBar(games: GameItem[], onComplete = vi.fn(async (_failedIds: string[]) => {})) {
  render(<GameBatchBar games={games} tags={tags} onComplete={onComplete} onBusy={vi.fn()} />);
  return onComplete;
}

it("favorites three games with a single games.update_batch call using list revisions", async () => {
  operation.mockImplementation(async (_name, params) => allOk(params));
  const onComplete = renderBar(makeGames(3));

  fireEvent.click(screen.getByRole("button", { name: "批量收藏" }));

  await waitFor(() => expect(onComplete).toHaveBeenCalledWith([]));
  expect(operation).toHaveBeenCalledTimes(1);
  expect(operation).toHaveBeenCalledWith("games.update_batch", {
    items: [
      { gameId: "game-1", expectedRevision: 1, favorite: true },
      { gameId: "game-2", expectedRevision: 2, favorite: true },
      { gameId: "game-3", expectedRevision: 3, favorite: true },
    ],
  }, "games.update_batch:favorite");
  expect(operation.mock.calls.some(([name]) => name === "games.get")).toBe(false);
  expect(await screen.findByText("已完成 3 个")).toBeInTheDocument();
});

it("unfavorites with favorite:false in the same batch operation", async () => {
  operation.mockImplementation(async (_name, params) => allOk(params));
  const onComplete = renderBar(makeGames(2));

  fireEvent.click(screen.getByRole("button", { name: "取消收藏" }));

  await waitFor(() => expect(onComplete).toHaveBeenCalledWith([]));
  expect(operation).toHaveBeenCalledTimes(1);
  expect(operation).toHaveBeenCalledWith("games.update_batch", {
    items: [
      { gameId: "game-1", expectedRevision: 1, favorite: false },
      { gameId: "game-2", expectedRevision: 2, favorite: false },
    ],
  }, "games.update_batch:unfavorite");
});

it("assigns a tag with one tags.assign_batch call carrying tagId", async () => {
  operation.mockImplementation(async (_name, params) => allOk(params));
  const onComplete = renderBar(makeGames(2));

  fireEvent.click(screen.getByRole("button", { name: "添加标签" }));
  fireEvent.click(screen.getByRole("option", { name: /RPG/ }));

  await waitFor(() => expect(onComplete).toHaveBeenCalledWith([]));
  expect(operation).toHaveBeenCalledTimes(1);
  expect(operation).toHaveBeenCalledWith("tags.assign_batch", {
    tagId: "tag-rpg",
    items: [
      { gameId: "game-1", expectedRevision: 1 },
      { gameId: "game-2", expectedRevision: 2 },
    ],
  }, "tags.assign_batch:tag-rpg");
  expect(await screen.findByText("已完成 2 个")).toBeInTheDocument();
});

it("removes games with one games.remove_batch call after confirmation", async () => {
  operation.mockImplementation(async (_name, params) => allOk(params));
  const onComplete = renderBar(makeGames(2));

  fireEvent.click(screen.getByRole("button", { name: "批量移除" }));
  fireEvent.click(await screen.findByRole("button", { name: "移除游戏" }));

  await waitFor(() => expect(onComplete).toHaveBeenCalledWith([]));
  expect(operation).toHaveBeenCalledTimes(1);
  expect(operation).toHaveBeenCalledWith("games.remove_batch", {
    items: [
      { gameId: "game-1", expectedRevision: 1 },
      { gameId: "game-2", expectedRevision: 2 },
    ],
  }, "games.remove_batch");
});

it("collects per-item failures into failedIds and reports the failure count", async () => {
  operation.mockImplementation(async (_name, params) => ({
    data: {
      items: params.items.map((item: { gameId: string }) => item.gameId === "game-2"
        ? { gameId: item.gameId, ok: false, errorCode: "Conflict", message: "已被其他窗口修改" }
        : { gameId: item.gameId, ok: true }),
      updated: 2,
      failed: 1,
    },
  }));
  const onComplete = renderBar(makeGames(3));

  fireEvent.click(screen.getByRole("button", { name: "批量收藏" }));

  await waitFor(() => expect(onComplete).toHaveBeenCalledWith(["game-2"]));
  const status = await screen.findByText(/已完成 2 个/);
  expect(status).toHaveTextContent("失败 1 个。Game 2：已被其他窗口修改");
  expect(operation).toHaveBeenCalledTimes(1);
});

it("splits more than 1000 items into sequential chunks and merges the replies", async () => {
  operation.mockImplementation(async (_name, params) => allOk(params));
  const onComplete = renderBar(makeGames(1024));

  fireEvent.click(screen.getByRole("button", { name: "批量收藏" }));

  await waitFor(() => expect(onComplete).toHaveBeenCalledWith([]));
  expect(operation).toHaveBeenCalledTimes(3);
  const sizes = operation.mock.calls.map(([, params]) => params.items.length);
  expect(sizes).toEqual([500, 500, 24]);
  expect(operation.mock.calls[0][1].items[0]).toEqual({ gameId: "game-1", expectedRevision: 1, favorite: true });
  expect(operation.mock.calls[2][1].items[23]).toEqual({ gameId: "game-1024", expectedRevision: 1024, favorite: true });
  expect(await screen.findByText("已完成 1024 个")).toBeInTheDocument();
});

it("keeps later chunks running when one chunk fails at the envelope level", async () => {
  operation.mockImplementation(async (_name, params) => {
    if (params.items.some((item: { gameId: string }) => item.gameId === "game-1")) throw new Error("整批参数非法");
    return allOk(params);
  });
  const onComplete = renderBar(makeGames(502));

  fireEvent.click(screen.getByRole("button", { name: "批量收藏" }));

  await waitFor(() => expect(onComplete).toHaveBeenCalledTimes(1));
  const failedIds = onComplete.mock.calls[0][0] as string[];
  expect(failedIds).toHaveLength(500);
  expect(failedIds.slice(0, 3)).toEqual(["game-1", "game-2", "game-3"]);
  expect(failedIds).not.toContain("game-501");
  expect(operation).toHaveBeenCalledTimes(2);
  expect(operation.mock.calls[1][1].items[0]).toEqual({ gameId: "game-501", expectedRevision: 501, favorite: true });
  // 第一批（500 条）整批失败、第二批 2 条成功：失败提示只展示前 3 条。
  const status = await screen.findByText(/已完成 2 个/);
  expect(status).toHaveTextContent("失败 500 个。Game 1：整批参数非法；Game 2：整批参数非法；Game 3：整批参数非法");
});
