import { act, cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import type { GameItem } from "../lib/types";
import { GameCard } from "./GameCard";
import { GameGrid } from "./GameGrid";
import { assetDataUrl, invalidateAssetChanges, operation } from "../lib/api";

afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); vi.useRealTimers(); });

it("renders a pending B cover when A changes and rejects an old-library cover", async () => {
  let finish!: (value: unknown) => void;
  let epoch = "first";
  const envelope = (data: unknown, dataEpoch = epoch) => ({ ok: true, data, libraryInstanceId: "cover-test", dataEpoch });
  const invoke = vi.fn(async (_name: string, args: { request: { operationId: string } }) => {
    if (args.request.operationId === "assets.get") return new Promise(resolve => { finish = resolve; });
    if (args.request.operationId === "assets.choose") return envelope({ assetId: "cover-a", previousAssetId: "old-a" });
    return envelope({});
  });
  vi.stubGlobal("__TAURI__", { core: { invoke } });
  vi.stubGlobal("IntersectionObserver", class {
    constructor(private callback: (entries: { isIntersecting: boolean }[]) => void) {}
    observe() { this.callback([{ isIntersecting: true }]); }
    disconnect() {}
  });
  await operation("host.status");
  const game: GameItem = { gameId: "b", title: "B cover", coverAssetId: "cover-b", rootPath: "D:/example/b", kind: "folderGame", revision: 1, favorite: false };
  render(<GameCard game={game} selected={false} checked={false} selectionDisabled={false}
    onToggle={vi.fn()} onSelect={vi.fn()} onPlay={vi.fn()} />);
  await act(async () => {});
  await operation("assets.choose", { gameId: "a", assetId: "cover-a" });
  invalidateAssetChanges({ assetId: "cover-a", previousAssetId: "old-a" });
  await act(async () => finish(envelope({ mimeType: "image/png", dataBase64: "Yg==" })));
  expect(screen.getByRole("presentation")).toHaveAttribute("src", "data:image/png;base64,Yg==");
  expect(invoke.mock.calls.filter(([, args]) => args.request.operationId === "assets.get")).toHaveLength(1);

  const stale = assetDataUrl("old-library-cover");
  const rejected = expect(stale).rejects.toThrow("库已切换");
  const oldFinish = finish;
  epoch = "second";
  await operation("host.status");
  oldFinish(envelope({ mimeType: "image/png", dataBase64: "b2xk" }, "first"));
  await rejected;
});

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

it("uses one stretched detail button while checkbox and launch stay independent", () => {
  vi.stubGlobal("IntersectionObserver", class { observe() {} disconnect() {} });
  const game: GameItem = {
    gameId: "game-1", title: "测试游戏", rootPath: "D:/Games/Test", kind: "executable",
    engine: "unknown", favorite: false, revision: 1, availability: "available", playtimeMinutes: 12,
  };
  const onToggle = vi.fn();
  const onSelect = vi.fn();
  const onPlay = vi.fn();
  render(<GameCard game={game} selected={false} checked={false} selectionDisabled={false}
    onToggle={onToggle} onSelect={onSelect} onPlay={onPlay} />);

  // article 不再是交互控件：没有 role=button / tabIndex / aria-pressed / onClick。
  const article = screen.getByRole("article");
  expect(article).not.toHaveAttribute("role");
  expect(article).not.toHaveAttribute("tabindex");
  expect(article).not.toHaveAttribute("aria-pressed");

  // 详情入口是一个真实 button，用 after 伪元素拉伸整卡热区。
  const detail = screen.getByRole("button", { name: "测试游戏，查看详情" });
  expect(detail).toHaveAttribute("type", "button");
  expect(detail).toHaveAttribute("aria-haspopup", "dialog");
  expect(detail).toHaveClass("after:absolute", "after:inset-0");
  fireEvent.click(detail);
  expect(onSelect).toHaveBeenCalledWith("game-1");

  // checkbox 与启动按钮各自可聚焦、可点击，且不会冒泡成「查看详情」。
  fireEvent.click(screen.getByRole("checkbox", { name: "选择 测试游戏" }));
  expect(onToggle).toHaveBeenCalledWith("game-1");
  expect(onSelect).toHaveBeenCalledTimes(1);

  fireEvent.click(screen.getByRole("button", { name: "启动 测试游戏" }));
  expect(onPlay).toHaveBeenCalledWith("game-1");
  expect(onSelect).toHaveBeenCalledTimes(1);
});

it("releases the card's cover when it leaves the overscan area and reuses the bounded cache", async () => {
  let visibility!: (entries: { isIntersecting: boolean }[]) => void;
  vi.stubGlobal("IntersectionObserver", class {
    constructor(callback: typeof visibility) { visibility = callback; }
    observe() {}
    disconnect() {}
  });
  const invoke = vi.fn(async () => ({ ok: true, libraryInstanceId: "rg03-covers", dataEpoch: "one",
    data: { mimeType: "image/png", dataBase64: "cmcz" } }));
  vi.stubGlobal("__TAURI__", { core: { invoke } });
  const game: GameItem = { gameId: "rg03-cover", title: "Cover", coverAssetId: "rg03-cover-asset",
    rootPath: "D:/example", kind: "folderGame", revision: 1, favorite: false };
  await operation("host.status");
  invoke.mockClear();
  render(<GameCard game={game} selected={false} checked={false} selectionDisabled={false}
    onToggle={vi.fn()} onSelect={vi.fn()} onPlay={vi.fn()} />);
  await act(async () => visibility([{ isIntersecting: true }]));
  expect(screen.getByRole("presentation")).toHaveAttribute("src", "data:image/png;base64,cmcz");
  act(() => visibility([{ isIntersecting: false }]));
  expect(screen.queryByRole("presentation")).not.toBeInTheDocument();
  await act(async () => visibility([{ isIntersecting: true }]));
  expect(screen.getByRole("presentation")).toBeInTheDocument();
  expect(invoke).toHaveBeenCalledOnce();
});

it("unmounts distant list items while preserving a focused checked game", async () => {
  vi.useFakeTimers();
  vi.stubGlobal("IntersectionObserver", class { observe() {} disconnect() {} });
  let top = 0;
  const root = document.createElement("main");
  Object.defineProperty(root, "clientHeight", { value: 200 });
  document.body.append(root);
  vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(function (this: HTMLElement) {
    return new DOMRect(0, this.hasAttribute("data-virtual-list") ? -top : 0, 600,
      this.hasAttribute("data-virtual-item") ? 80 : 200);
  });
  const games: GameItem[] = Array.from({ length: 1000 }, (_, index) => ({
    gameId: String(index), title: `Game ${index}`, rootPath: `D:/example/${index}`,
    kind: "folderGame", revision: 1, favorite: false,
  }));
  const view = render(<GameGrid games={games} loading={false} loadingMore={false} layout="compact" hasMore={false}
    scrollRoot={{ current: root }} onLoadMore={vi.fn()} onSelect={vi.fn()} onPlay={vi.fn()}
    tags={[]} onChanged={vi.fn()} />, { container: root });
  const focused = screen.getByRole("checkbox", { name: "选择 Game 0" });
  fireEvent.click(focused);
  act(() => focused.focus());
  top = 5000;
  fireEvent.scroll(root);
  await act(async () => { await vi.advanceTimersByTimeAsync(20); });
  expect(screen.getAllByRole("listitem").length).toBeLessThan(20);
  expect(screen.queryByRole("checkbox", { name: "选择 Game 1" })).not.toBeInTheDocument();
  expect(screen.getByRole("checkbox", { name: "选择 Game 60" })).toBeInTheDocument();
  expect(focused).toHaveFocus();
  expect(focused).toBeChecked();
  view.unmount(); root.remove();
});
