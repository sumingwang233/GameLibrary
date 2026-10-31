import { expect, it } from "vitest";
import { changedGameIds, stableQueryItem } from "./useGamesQuery";
import type { GameItem } from "../types";

it("requires contiguous known changes, preserving unknown invalidations and bounded history", () => {
  expect(changedGameIds([{ token: 2, gameIds: ["a"] }, { token: 3, gameIds: ["a", "b"] }], 1, 3)).toEqual(["a", "b"]);
  expect(changedGameIds([{ token: 2, gameIds: null }, { token: 3, gameIds: ["a"] }], 1, 3)).toBeNull();
  expect(changedGameIds([{ token: 3, gameIds: ["a"] }], 1, 3)).toBeNull();
  expect(changedGameIds(undefined, 0, 1)).toBeNull();
});

it("only patches items that keep membership, played filter and the actual SQL order key", () => {
  const game: GameItem = { gameId: "a", title: "A", rootPath: "D:/a", kind: "folderGame", membership: "active",
    played: false, favorite: false, revision: 1, acceptedUtc: "2026-01-01", updatedUtc: "2026-01-02" };
  const updated = { ...game, revision: 2, favorite: true, updatedUtc: "2026-01-03" };
  expect(stableQueryItem(game, updated, "title", false)).toBe(true);
  expect(stableQueryItem(game, updated, "accepted-desc", false)).toBe(true);
  expect(stableQueryItem(game, updated, "recent", false)).toBe(false);
  expect(stableQueryItem(game, { ...updated, title: "Z" }, "title", false)).toBe(false);
  expect(stableQueryItem(game, { ...updated, played: true }, "title", false)).toBe(false);
  expect(stableQueryItem(game, { ...updated, membership: "removed" }, "title", false)).toBe(false);
  expect(stableQueryItem(game, { ...updated, revision: 0 }, "title", false)).toBe(false);
});
