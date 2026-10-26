import { cleanup, render, screen, waitFor } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import type { GameItem } from "../lib/types";
import { DetailSheet } from "./DetailSheet";

const { operation } = vi.hoisted(() => ({ operation: vi.fn() }));
vi.mock("../lib/api", async importOriginal => ({
  ...await importOriginal<typeof import("../lib/api")>(),
  operation, assetDataUrl: vi.fn(), describeFailure: (cause: unknown) => String(cause),
}));
vi.mock("@tauri-apps/plugin-dialog", () => ({ open: vi.fn() }));
afterEach(() => { cleanup(); operation.mockReset(); });

const game: GameItem = { gameId: "game", title: "旧标题", rootPath: "D:/game", kind: "folderGame", favorite: false, revision: 1 };

// 缺陷 2 的另一半：App 改成「selectedGameId + 从当前列表取最新对象」后会传入同一个
// gameId 的新对象。DetailSheet 的 mergeDetail 必须接受这次更新（revision 更大），
// 而挂载时那次 games.get 的旧响应不能把标题倒退回旧值。
it("applies a newer game object from the parent while ignoring the older games.get response", async () => {
  operation.mockImplementation(async (name: string) => ({
    data: name === "games.get" ? game : name === "profiles.list" ? { items: [] } : {},
  }));
  const props = { tags: [], onClose: vi.fn(), onPlay: vi.fn(), onChanged: vi.fn() };
  const view = render(<DetailSheet {...props} game={game} />);
  await screen.findByRole("heading", { name: "旧标题" });
  view.rerender(<DetailSheet {...props} game={{ ...game, title: "新标题", revision: 2 }} />);
  await waitFor(() => expect(screen.getByRole("heading", { name: "新标题" })).toBeInTheDocument());
});
