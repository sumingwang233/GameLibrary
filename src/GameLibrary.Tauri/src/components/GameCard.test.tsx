import { cleanup, render, screen, within } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import type { GameItem } from "../lib/types";
import { GameCard } from "./GameCard";

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

it.each([[12, "12 分钟"], [120, "2.0 小时"], [0, ""], [undefined, ""]] as const)(
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
