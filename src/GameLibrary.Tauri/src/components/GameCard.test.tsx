import { cleanup, render, screen, within } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import type { GameItem } from "../lib/types";
import { GameCard } from "./GameCard";
import { GameGrid } from "./GameGrid";

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

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
  },
);
