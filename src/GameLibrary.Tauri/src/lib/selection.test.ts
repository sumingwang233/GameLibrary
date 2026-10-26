import { expect, it } from "vitest";
import { resolveSelectedGame } from "./selection";
import type { GameItem } from "./types";

const game = (id: string, title: string, revision = 1): GameItem => ({
  gameId: id, title, rootPath: "C:/games", kind: "manualDirectory", favorite: false, revision,
});

it("prefers the current list object so background reloads refresh the open detail", () => {
  const stale = game("a", "旧标题", 1);
  const fresh = game("a", "新标题", 2);
  // 打开详情时拿的是 stale 快照；列表 reload 之后详情必须跟着变成 fresh。
  expect(resolveSelectedGame([fresh], "a", stale)).toBe(fresh);
});

it("falls back to the games.get snapshot when the game is outside the current page", () => {
  const snapshot = game("b", "列表外的游戏", 3);
  expect(resolveSelectedGame([game("a", "A")], "b", snapshot)).toBe(snapshot);
});

it("ignores a snapshot for another game so closing cannot leak the previous detail", () => {
  // 关闭详情后 selectedGameId 为 null：无论快照是什么都必须为空。
  expect(resolveSelectedGame([game("a", "A")], null, game("a", "A"))).toBeNull();
  // gameId 已切到 b 但快照还是 a（例如 games.get 还没回来）：不能拿 a 充当 b。
  expect(resolveSelectedGame([], "b", game("a", "A"))).toBeNull();
});

it("returns null when neither the list nor the snapshot has the game", () => {
  expect(resolveSelectedGame([], "gone", null)).toBeNull();
});
