import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import type { GameItem } from "../lib/types";
import { GameCard } from "./GameCard";
import { GameGrid } from "./GameGrid";

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

it("translates only checked games and keeps selection when a translated title arrives", () => {
  vi.stubGlobal("IntersectionObserver", class { observe() {} disconnect() {} });
  const a: GameItem = { gameId: "a", title: "A", rootPath: "D:/example/a", kind: "folderGame", revision: 1, favorite: false };
  const b: GameItem = { ...a, gameId: "b", title: "B" };
  const start = vi.fn(async () => {});
  const controller = { start, busy: false, progress: null, error: null, state: "", cancel: vi.fn(async () => {}), retry: vi.fn(async () => {}) };
  const props = { games: [a, b], loading: false, loadingMore: false, layout: "grid" as const, hasMore: false,
    onLoadMore: vi.fn(), onSelect: vi.fn(), onPlay: vi.fn(), tags: [], onChanged: vi.fn(), titleTranslation: controller };
  const view = render(<GameGrid {...props} />);
  fireEvent.click(screen.getByRole("checkbox", { name: "选择 B" }));
  fireEvent.click(screen.getByRole("button", { name: "翻译选中名称" }));
  expect(start).toHaveBeenCalledWith(["b"]);
  view.rerender(<GameGrid {...props} games={[a, { ...b, title: "中文译名", revision: 2 }]} />);
  expect(screen.getByRole("checkbox", { name: "选择 中文译名" })).toBeChecked();
  expect(screen.getByRole("checkbox", { name: "选择 A" })).not.toBeChecked();
});

it.each([[12, "12 分钟"], [120, "2.0 小时"], [0, "0 分钟"], [undefined, "0 分钟"]] as const)(
  "shows playtime %s below the title instead of the engine",
  (playtimeMinutes, expected) => {
    vi.stubGlobal("IntersectionObserver", class { observe() {} disconnect() {} });
    const game: GameItem = {
      gameId: "game-1", title: "测试游戏", rootPath: "D:/Games/Test", kind: "executable",
      engine: "unknown", favorite: true, revision: 1, availability: "available", playtimeMinutes,
    };
    render(<GameCard game={game} selected={false} checked={false} selectionDisabled={false}
      onToggle={vi.fn()} onSelect={vi.fn()} onPlay={vi.fn()} />);
    const titleArea = screen.getByRole("heading", { name: game.title }).parentElement!;
    const footer = screen.getByText("可启动").parentElement!;
    expect(screen.queryByText(game.engine!)).not.toBeInTheDocument();
    expect(screen.queryByText(game.kind)).not.toBeInTheDocument();
    expect(titleArea.querySelector("p")?.textContent ?? "").toBe(expected);
    if (expected) expect(within(footer).queryByText(expected)).not.toBeInTheDocument();
    expect(within(footer).getByLabelText("已收藏")).toBeInTheDocument();
  },
);

it.each(["grid", "compact"] as const)(
  "%s layout keeps zero playtime below the title and hides the engine",
  (layout) => {
    vi.stubGlobal("IntersectionObserver", class { observe() {} disconnect() {} });
    const game: GameItem = {
      gameId: "game-1", title: "测试游戏", rootPath: "D:/Games/Test", kind: "executable",
      engine: "unknown", favorite: false, revision: 1, availability: "available", playtimeMinutes: 0,
    };
    const props = { games: [game], loading: false, loadingMore: false, layout, hasMore: false,
      onLoadMore: vi.fn(), onSelect: vi.fn(), onPlay: vi.fn(), tags: [], onChanged: vi.fn() };
    const { rerender } = render(<GameGrid {...props} />);
    const titleArea = () => screen.getByText(game.title).parentElement!;
    expect(within(titleArea()).getByText("0 分钟")).toHaveAttribute("title", "累计游玩 0 分钟");
    expect(screen.queryByText(game.engine!)).not.toBeInTheDocument();
    expect(screen.queryByText(game.kind)).not.toBeInTheDocument();
    rerender(<GameGrid {...props} games={[{ ...game, playtimeMinutes: 12 }]} />);
    expect(within(titleArea()).getByText("12 分钟")).toHaveAttribute("title", "累计游玩 12 分钟");
    rerender(<GameGrid {...props} games={[{ ...game, title: "中文译名", originalTitle: "Original name", translatedTitle: "中文译名", titleDisplayMode: "translated", revision: 2 }]} />);
    expect(screen.getByText("中文译名")).toBeInTheDocument();
    expect(screen.queryByText("Original name")).not.toBeInTheDocument();
    rerender(<GameGrid {...props} games={[{ ...game, title: "Original name", originalTitle: "Original name", translatedTitle: "中文译名", titleDisplayMode: "original", revision: 3 }]} />);
    expect(screen.getByText("Original name")).toBeInTheDocument();
    expect(screen.queryByText("中文译名")).not.toBeInTheDocument();
  },
);
