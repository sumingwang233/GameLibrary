import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import type { GameItem, ProfileItem, TagItem } from "../lib/types";
import { DetailSheet } from "./DetailSheet";
import { OperationError } from "../lib/api";
import { open } from "@tauri-apps/plugin-dialog";

const { operation } = vi.hoisted(() => ({ operation: vi.fn() }));
vi.mock("../lib/api", async importOriginal => ({ ...await importOriginal<typeof import("../lib/api")>(), operation, assetDataUrl: vi.fn(), describeFailure: (cause: unknown) => String(cause) }));
vi.mock("@tauri-apps/plugin-dialog", () => ({ open: vi.fn() }));
afterEach(() => { cleanup(); operation.mockReset(); vi.mocked(open).mockReset(); });

const game: GameItem = { gameId: "game", title: "Test game", rootPath: "D:/game", kind: "folderGame", favorite: false, revision: 1 };

it("lets a user select a BAT file when adding a launch profile", async () => {
  vi.mocked(open).mockResolvedValue("D:/game/Start game.bat");
  operation.mockImplementation(async name => ({ data: name === "games.get" ? game : name === "profiles.list" ? { items: [] } : {} }));
  render(<DetailSheet game={game} tags={[]} onClose={vi.fn()} onPlay={vi.fn()} onChanged={vi.fn()} initialTab="launch" />);
  fireEvent.click(await screen.findByRole("button", { name: "添加启动方式" }));
  await waitFor(() => expect(operation).toHaveBeenCalledWith("profiles.create", {
    gameId: "game", executablePath: "D:/game/Start game.bat", argv: [], cwd: "D:/game", isDefault: true,
  }, expect.any(String)));
  expect(open).toHaveBeenCalledWith(expect.objectContaining({ filters: [{ name: "游戏程序", extensions: ["exe", "swf", "bat"] }] }));
});

it("previews the full project path before recycling a manually added EXE", async () => {
  const detail = { ...game, kind: "manualFile", rootPath: "D:/game/Game.exe" };
  operation.mockImplementation(async (name, params) => ({ data: name === "games.get"
    ? { ...detail, ...(params.includeRemovalPlan ? { revision: 3, removalPlan: { targetPath: "D:/game", scope: "directory", error: null } } : {}) }
    : name === "profiles.list" ? { items: [] } : {} }));
  const close = vi.fn();
  render(<DetailSheet game={detail} tags={[]} onClose={close} onPlay={vi.fn()} onChanged={vi.fn()} />);
  fireEvent.click(await screen.findByRole("button", { name: "删除游戏及原文件" }));
  expect(await screen.findByText(/将整个游戏文件夹移入回收站/)).toHaveTextContent("D:/game");
  expect(operation.mock.calls.some(([name]) => name === "games.remove")).toBe(false);
  fireEvent.click(screen.getByRole("button", { name: "确认移入回收站" }));
  await waitFor(() => expect(close).toHaveBeenCalledOnce());
  expect(operation).toHaveBeenCalledWith("games.remove", { gameId: "game", expectedRevision: 3, deleteFiles: true,
    confirmedPath: "D:/game" }, expect.any(String));
});

it("does not offer an unverified EXE-only deletion when the Host has no removal plan", async () => {
  operation.mockImplementation(async name => ({ data: name === "games.get" ? game : name === "profiles.list" ? { items: [] } : {} }));
  render(<DetailSheet game={game} tags={[]} onClose={vi.fn()} onPlay={vi.fn()} onChanged={vi.fn()} />);
  fireEvent.click(await screen.findByRole("button", { name: "删除游戏及原文件" }));
  await screen.findByText(/无法读取删除范围/);
  expect(screen.queryByRole("button", { name: "确认移入回收站" })).not.toBeInTheDocument();
  expect(operation.mock.calls.some(([name]) => name === "games.remove")).toBe(false);
});

it("updates played status and game stars independently while preserving favorite", async () => {
  let detail: GameItem = { ...game, favorite: true, played: false, rating: 0 };
  operation.mockImplementation(async (name, params) => {
    if (name === "games.update") detail = { ...detail, ...params, revision: detail.revision + 1 };
    return { data: name === "games.get" ? detail : name === "profiles.list" ? { items: [] } : {} };
  });
  render(<DetailSheet game={detail} tags={[]} onClose={vi.fn()} onPlay={vi.fn()} onChanged={vi.fn()} />);
  fireEvent.click(await screen.findByRole("button", { name: "标记为已游玩" }));
  await waitFor(() => expect(screen.getByRole("button", { name: "已游玩" })).toHaveAttribute("aria-pressed", "true"));
  expect(operation).toHaveBeenCalledWith("games.update", { gameId: "game", played: true, expectedRevision: 1 }, expect.any(String));
  expect(detail.favorite).toBe(true);
  expect(screen.queryByRole("button", { name: "取消收藏" })).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole("button", { name: "评为 5 星" }));
  await waitFor(() => expect(screen.getByRole("button", { name: "评为 5 星" })).toHaveAttribute("aria-pressed", "true"));
  expect(operation).toHaveBeenCalledWith("games.update", { gameId: "game", rating: 5, expectedRevision: 2 }, expect.any(String));
  fireEvent.click(screen.getByRole("button", { name: "清除评分" }));
  await screen.findByText("尚未评分");
  expect(detail.played).toBe(true); expect(detail.favorite).toBe(true); expect(detail.rating).toBe(0);
});

it("does not highlight an already rated game after another play session, including a refreshed rating", async () => {
  operation.mockImplementation(async name => ({ data: name === "games.get" ? { ...game, rating: 5 }
    : name === "profiles.list" ? { items: [] } : {} }));
  const props = { game: { ...game, rating: 4 }, tags: [], onClose: vi.fn(), onPlay: vi.fn(), onChanged: vi.fn() };
  const view = render(<DetailSheet {...props} ratingPromptToken={1} />);
  expect(screen.getByRole("group", { name: "游戏评分" }).className).not.toContain("animate-pulse");
  await screen.findByText("5 / 5 星");
  view.rerender(<DetailSheet {...props} ratingPromptToken={2} />);
  expect(screen.queryByText("这次游玩体验如何？点选星星为游戏评分。")).not.toBeInTheDocument();
  expect(screen.getByRole("group", { name: "游戏评分" }).className).not.toContain("animate-pulse");
  expect(screen.getByRole("button", { name: "评为 3 星" })).toBeEnabled();
});

it("highlights the rating after a fresh exit and dismisses it after rating without carrying it to another game", async () => {
  operation.mockImplementation(async (name, params) => ({ data: name === "games.get" ? { ...game, gameId: params.gameId }
    : name === "profiles.list" ? { items: [] } : {} }));
  const props = { game, tags: [], onClose: vi.fn(), onPlay: vi.fn(), onChanged: vi.fn() };
  const view = render(<DetailSheet {...props} />);
  expect(screen.queryByText("这次游玩体验如何？点选星星为游戏评分。")).not.toBeInTheDocument();
  view.rerender(<DetailSheet {...props} ratingPromptToken={1} />);
  expect(await screen.findByText("这次游玩体验如何？点选星星为游戏评分。")).toBeInTheDocument();
  expect(screen.getByRole("group", { name: "游戏评分" }).className).toContain("motion-safe:animate-pulse");
  fireEvent.click(screen.getByRole("button", { name: "评为 4 星" }));
  await waitFor(() => expect(screen.queryByText("这次游玩体验如何？点选星星为游戏评分。")).not.toBeInTheDocument());
  view.rerender(<DetailSheet {...props} ratingPromptToken={2} />);
  expect(await screen.findByText("这次游玩体验如何？点选星星为游戏评分。")).toBeInTheDocument();
  view.rerender(<DetailSheet {...props} game={{ ...game, gameId: "other" }} />);
  expect(screen.queryByText("这次游玩体验如何？点选星星为游戏评分。")).not.toBeInTheDocument();
});

it("keeps the saved rating when a write is rejected", async () => {
  operation.mockImplementation(async name => {
    if (name === "games.update") throw new Error("Revision conflict");
    return { data: name === "games.get" ? { ...game, rating: 3 } : name === "profiles.list" ? { items: [] } : {} };
  });
  render(<DetailSheet game={{ ...game, rating: 3 }} tags={[]} onClose={vi.fn()} onPlay={vi.fn()} onChanged={vi.fn()} />);
  await waitFor(() => expect(screen.getByRole("button", { name: "评为 3 星" })).toHaveAttribute("aria-pressed", "true"));
  fireEvent.click(screen.getByRole("button", { name: "评为 5 星" }));
  expect(await screen.findByRole("alert")).toHaveTextContent("Revision conflict");
  expect(screen.getByRole("button", { name: "评为 3 星" })).toHaveAttribute("aria-pressed", "true");
});

it.each([false, true])("ignores the first A session after A → B → A (reject=%s)", async reject => {
  let settle!: (value: unknown) => void, fail!: (cause: Error) => void;
  let reads = 0;
  operation.mockImplementation(async (name, params) => {
    if (name === "games.get") {
      if (++reads === 1) return new Promise((resolve, reject) => { settle = resolve; fail = reject; });
      return { data: { ...game, gameId: params.gameId, title: params.gameId === "other" ? "B" : "Fresh A", revision: 3 } };
    }
    return { data: name === "profiles.list" ? { items: [] } : {} };
  });
  const props = { game, tags: [], onClose: vi.fn(), onPlay: vi.fn(), onChanged: vi.fn() };
  const view = render(<DetailSheet {...props} />);
  view.rerender(<DetailSheet {...props} game={{ ...game, gameId: "other", title: "B" }} />);
  await waitFor(() => expect(screen.getByRole("textbox", { name: "游戏标题" })).toHaveValue("B"));
  view.rerender(<DetailSheet {...props} game={{ ...game, title: "Fresh A", revision: 3 }} />);
  const input = screen.getByRole("textbox", { name: "游戏标题" });
  await waitFor(() => expect(input).toHaveValue("Fresh A"));
  fireEvent.change(input, { target: { value: "Current draft" } });
  await act(async () => { if (reject) fail(new Error("old A failed")); else settle({ data: game }); });
  expect(input).toHaveValue("Current draft");
  expect(screen.queryByRole("alert")).not.toBeInTheDocument();
});

it.each([false, true])("ignores an old action's error and finally while B is busy (reject=%s)", async reject => {
  let finishA!: () => void, failA!: (cause: Error) => void, finishB!: () => void;
  operation.mockImplementation(async (name, params) => ({ data: name === "games.get"
    ? { ...game, gameId: params.gameId, title: params.gameId === "other" ? "B" : game.title }
    : name === "profiles.list" ? { items: [] } : {} }));
  const onPlay = vi.fn((id: string) => new Promise<void>((resolve, reject) => {
    if (id === "game") { finishA = resolve; failA = reject; } else finishB = resolve;
  }));
  const props = { game, tags: [], onClose: vi.fn(), onPlay, onChanged: vi.fn() };
  const view = render(<DetailSheet {...props} />);
  fireEvent.click(screen.getByRole("button", { name: "开始游戏" }));
  view.rerender(<DetailSheet {...props} game={{ ...game, gameId: "other", title: "B" }} />);
  await waitFor(() => expect(screen.getByRole("textbox", { name: "游戏标题" })).toHaveValue("B"));
  const playB = screen.getByRole("button", { name: "开始游戏" });
  fireEvent.click(playB);
  await act(async () => { if (reject) failA(new Error("old action failed")); else finishA(); });
  expect(playB).toBeDisabled();
  expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  await act(async () => finishB());
  expect(playB).toBeEnabled();
});

it("does not let an old detail response roll back a newer prop revision", async () => {
  let finish!: (value: unknown) => void;
  operation.mockImplementation(async name => name === "games.get"
    ? new Promise(resolve => { finish = resolve; }) : { data: name === "profiles.list" ? { items: [] } : {} });
  const props = { game, tags: [], onClose: vi.fn(), onPlay: vi.fn(), onChanged: vi.fn() };
  const view = render(<DetailSheet {...props} />);
  view.rerender(<DetailSheet {...props} game={{ ...game, title: "New title", summary: "New summary", revision: 2 }} />);
  await act(async () => finish({ data: { ...game, summary: "Old summary" } }));
  expect(screen.getByRole("textbox", { name: "游戏标题" })).toHaveValue("New title");
  expect(screen.getByRole("textbox", { name: "游戏简介" })).toHaveValue("New summary");
});

it("keeps a dirty summary and its baseline through refresh and conflict retry", async () => {
  let detail = { ...game, summary: "Original" };
  let writes = 0;
  operation.mockImplementation(async name => {
    if (name === "fields.set" && ++writes === 1) {
      detail = { ...detail, summary: "External change", revision: 3 };
      throw new OperationError("RevisionConflict", "summary changed", { ok: false, requestId: "test", status: "failed", data: null });
    }
    return { data: name === "games.get" ? detail : name === "profiles.list" ? { items: [] } : {} };
  });
  const props = { game: detail, tags: [], onClose: vi.fn(), onPlay: vi.fn(), onChanged: vi.fn() };
  const view = render(<DetailSheet {...props} />);
  const input = await screen.findByRole("textbox", { name: "游戏简介" });
  await waitFor(() => expect(input).toHaveValue("Original"));
  fireEvent.change(input, { target: { value: "My summary" } });
  view.rerender(<DetailSheet {...props} game={{ ...detail, summary: "Background change", revision: 2 }} />);
  expect(input).toHaveValue("My summary");
  fireEvent.blur(input);
  await screen.findByRole("alert");
  expect(input).toHaveValue("My summary");
  expect(operation.mock.calls.filter(([name]) => name === "fields.set")).toHaveLength(1);
  expect(operation).toHaveBeenCalledWith("fields.set", { gameId: "game", field: "summary", value: "My summary", expectedRevision: 1 }, "fields.set:game:summary:1:My summary");
  fireEvent.blur(input);
  await waitFor(() => expect(operation).toHaveBeenCalledWith("fields.set", { gameId: "game", field: "summary", value: "My summary", expectedRevision: 3 }, "fields.set:game:summary:3:My summary"));
});

it("keeps text typed while saving and rebases its next explicit save", async () => {
  let finish!: (value: unknown) => void;
  let detail = game;
  let writes = 0;
  operation.mockImplementation(async name => {
    if (name === "fields.set" && ++writes === 1) return new Promise(resolve => { finish = resolve; });
    return { data: name === "games.get" ? detail : name === "profiles.list" ? { items: [] } : {} };
  });
  render(<DetailSheet game={game} tags={[]} onClose={vi.fn()} onPlay={vi.fn()} onChanged={vi.fn()} />);
  const input = await screen.findByRole("textbox", { name: "游戏标题" });
  await waitFor(() => expect(input).toHaveValue(game.title));
  fireEvent.change(input, { target: { value: "First edit" } });
  fireEvent.click(screen.getByRole("button", { name: "保存" }));
  fireEvent.change(input, { target: { value: "Second edit" } });
  detail = { ...game, title: "First edit", revision: 2 };
  await act(async () => finish({ data: { revision: 2 } }));
  expect(input).toHaveValue("Second edit");
  fireEvent.click(screen.getByRole("button", { name: "保存" }));
  await waitFor(() => expect(operation).toHaveBeenCalledWith("fields.set", { gameId: "game", field: "title", value: "Second edit", expectedRevision: 2 }, "fields.set:game:title:2:Second edit"));
});

it.each(["user", "engine"])("moves a %s tag into an empty category without changing its game assignment", async kind => {
  respond([]);
  const tag: TagItem = { tagId: "tag", name: "Keep tag", kind, revision: 7, category: "special" };
  const update = vi.fn(async () => {});
  render(<DetailSheet game={game} tags={[tag]} onClose={vi.fn()} onPlay={vi.fn()} onChanged={vi.fn()} onUpdateTag={update} />);
  fireEvent.mouseDown(await screen.findByRole("tab", { name: "标签" }), { button: 0, ctrlKey: false });
  const source = await screen.findByRole("button", { name: "Keep tag" });
  const data = new Map<string, string>();
  const dataTransfer = { setData: (key: string, value: string) => data.set(key, value), getData: (key: string) => data.get(key) ?? "" };
  fireEvent.dragStart(source, { dataTransfer });
  const target = screen.getByRole("group", { name: "玩法" });
  fireEvent.dragOver(target, { dataTransfer });
  fireEvent.drop(target, { dataTransfer });
  await waitFor(() => expect(update).toHaveBeenCalledWith(tag, { category: "gameplay" }));
  expect(operation.mock.calls.some(call => ["tags.assign", "tags.unassign"].includes(call[0]))).toBe(false);
});

it("ignores unknown drops and drops into the current tag category", async () => {
  respond([]);
  const tag: TagItem = { tagId: "tag", name: "Keep tag", kind: "user", revision: 7, category: "special" };
  const update = vi.fn(async () => {});
  render(<DetailSheet game={game} tags={[tag]} onClose={vi.fn()} onPlay={vi.fn()} onChanged={vi.fn()} onUpdateTag={update} />);
  fireEvent.mouseDown(await screen.findByRole("tab", { name: "标签" }), { button: 0, ctrlKey: false });
  await screen.findByRole("button", { name: "Keep tag" });
  fireEvent.drop(screen.getByRole("group", { name: "特殊" }), { dataTransfer: { getData: () => "tag" } });
  fireEvent.drop(screen.getByRole("group", { name: "玩法" }), { dataTransfer: { getData: () => "not-a-tag" } });
  expect(update).not.toHaveBeenCalled();
});

it("supports category changes without dragging and reports a rejected update", async () => {
  respond([]);
  const tag: TagItem = { tagId: "tag", name: "Keep tag", kind: "user", revision: 7, category: "special" };
  const update = vi.fn(async () => { throw new Error("Revision conflict"); });
  render(<DetailSheet game={game} tags={[tag]} onClose={vi.fn()} onPlay={vi.fn()} onChanged={vi.fn()} onUpdateTag={update} />);
  fireEvent.mouseDown(await screen.findByRole("tab", { name: "标签" }), { button: 0, ctrlKey: false });
  fireEvent.click(await screen.findByText("调整标签分类"));
  fireEvent.change(screen.getByRole("combobox", { name: "标签分类" }), { target: { value: "social" } });
  await waitFor(() => expect(update).toHaveBeenCalledWith(tag, { category: "social" }));
  expect(await screen.findByRole("alert")).toHaveTextContent("Revision conflict");
  expect(screen.getByRole("combobox", { name: "标签分类" })).toHaveValue("special");
});

it("preserves a draft and its original editing target when a translation arrives", async () => {
  respond(profiles());
  const props = { game, tags: [], onClose: vi.fn(), onPlay: vi.fn(), onChanged: vi.fn() };
  const view = render(<DetailSheet {...props} />);
  const input = await screen.findByRole("textbox", { name: "游戏标题" });
  await waitFor(() => expect(input).toHaveValue("Test game"));
  fireEvent.change(input, { target: { value: "My draft" } });
  const translated: GameItem = { ...game, title: "中文名称", originalTitle: game.title, translatedTitle: "中文名称", titleDisplayMode: "translated", revision: 2 };
  view.rerender(<DetailSheet {...props} game={translated} />);
  expect(input).toHaveValue("My draft");
  expect(screen.getByText("标题（原文）")).toBeInTheDocument();
  fireEvent.click(screen.getByRole("button", { name: "保存" }));
  await waitFor(() => expect(operation).toHaveBeenCalledWith("fields.set", { gameId: "game", field: "title", value: "My draft", expectedRevision: 1 }, "fields.set:game:title:1:My draft"));
});

it("edits the displayed Chinese alias and keeps the original title", async () => {
  const translated: GameItem = { ...game, title: "中文名称", originalTitle: game.title, translatedTitle: "中文名称", titleDisplayMode: "translated", revision: 2 };
  operation.mockImplementation(async name => ({ data: name === "games.get" ? translated : name === "profiles.list" ? { items: [] } : {} }));
  render(<DetailSheet game={translated} tags={[]} onClose={vi.fn()} onPlay={vi.fn()} onChanged={vi.fn()} />);
  const input = await screen.findByRole("textbox", { name: "游戏标题" });
  await waitFor(() => expect(input).toHaveValue("中文名称"));
  fireEvent.change(input, { target: { value: "我的中文名" } });
  fireEvent.click(screen.getByRole("button", { name: "保存" }));
  await waitFor(() => expect(operation).toHaveBeenCalledWith("titles.set_translated", { gameId: "game", title: "我的中文名", expectedRevision: 2 }, "titles.set_translated:game:title:2:我的中文名"));
});

it("switches back to the original without sending a new translation request", async () => {
  let detail: GameItem = { ...game, title: "中文名称", originalTitle: game.title, translatedTitle: "中文名称", titleDisplayMode: "translated", revision: 2 };
  operation.mockImplementation(async (name) => {
    if (name === "titles.set_display") detail = { ...detail, title: game.title, titleDisplayMode: "original", revision: 3 };
    return { data: name === "games.get" ? detail : name === "profiles.list" ? { items: [] } : {} };
  });
  const start = vi.fn(async () => {});
  const controller = { start, busy: false, progress: null, error: null, state: "", cancel: vi.fn(async () => {}), retry: vi.fn(async () => {}) };
  render(<DetailSheet game={detail} tags={[]} onClose={vi.fn()} onPlay={vi.fn()} onChanged={vi.fn()} titleTranslation={controller} />);
  fireEvent.click(await screen.findByRole("button", { name: "显示原文" }));
  await waitFor(() => expect(operation).toHaveBeenCalledWith("titles.set_display", { gameId: "game", mode: "original", expectedRevision: 2 }, "titles.set_display:game:2"));
  await waitFor(() => expect(screen.getByRole("textbox", { name: "游戏标题" })).toHaveValue(game.title));
  expect(start).not.toHaveBeenCalled();
});

it("keeps draft text on revision conflict and waits for an explicit save retry", async () => {
  let conflict = true;
  let detail = game;
  operation.mockImplementation(async (name) => {
    if (name === "fields.set" && conflict) {
      conflict = false; detail = { ...game, revision: 2 };
      throw new OperationError("RevisionConflict", "changed", { ok: false, requestId: "test", status: "failed", data: null });
    }
    return { data: name === "games.get" ? detail : name === "profiles.list" ? { items: [] } : {} };
  });
  render(<DetailSheet game={game} tags={[]} onClose={vi.fn()} onPlay={vi.fn()} onChanged={vi.fn()} />);
  const input = await screen.findByRole("textbox", { name: "游戏标题" });
  await waitFor(() => expect(input).toHaveValue(game.title));
  fireEvent.change(input, { target: { value: "Keep this draft" } });
  fireEvent.click(screen.getByRole("button", { name: "保存" }));
  await waitFor(() => expect(screen.getByRole("alert")).toHaveTextContent("changed"));
  expect(input).toHaveValue("Keep this draft");
  expect(operation.mock.calls.filter(call => call[0] === "fields.set")).toHaveLength(1);
  fireEvent.click(screen.getByRole("button", { name: "保存" }));
  await waitFor(() => expect(operation).toHaveBeenCalledWith("fields.set", { gameId: "game", field: "title", value: "Keep this draft", expectedRevision: 2 }, "fields.set:game:title:2:Keep this draft"));
});
function profiles(status: ProfileItem["validationStatus"] = "suggested"): ProfileItem[] {
  return [{ profileId: "profile", executablePath: "D:/game/game_chs.exe", source: "automatic", validationStatus: status, isDefault: false, revision: 2 }];
}
function respond(items: ProfileItem[]) {
  operation.mockImplementation(async (name) => ({ data: name === "games.get" ? game
    : name === "profiles.list" ? { items } : name === "translation.get" ? { effective: "Auto" } : {} }));
}

it.each(["unity", "Unity", "UNITY"])("shows plugin retry controls for engine %s", async engine => {
  const unityGame = { ...game, engine };
  operation.mockImplementation(async name => ({ data: name === "games.get" ? unityGame
    : name === "profiles.list" ? { items: [] } : name === "translation.get" ? { effective: "Required" } : {} }));
  const configure = vi.fn();
  render(<DetailSheet game={unityGame} tags={[]} onClose={vi.fn()} onPlay={vi.fn()} onChanged={vi.fn()}
    initialTab="launch" onUnityTranslation={configure} />);
  fireEvent.click(await screen.findByRole("button", { name: "配置或重试翻译插件" }));
  expect(configure).toHaveBeenCalledWith("game", "configure");
  expect(screen.queryByText("先添加未翻译标签，程序将自动配置插件。配置失败或需要再次测试时可在这里重试。")).not.toBeInTheDocument();
});

it("shows shared plugin activity and failure inside game details and disables duplicate work", async () => {
  const unityGame = { ...game, engine: "unity" };
  operation.mockImplementation(async name => ({ data: name === "games.get" ? unityGame
    : name === "profiles.list" ? { items: [] } : {} }));
  const configure = vi.fn();
  const props = { game: unityGame, tags: [], onClose: vi.fn(), onPlay: vi.fn(), onChanged: vi.fn(), initialTab: "launch" as const, onUnityTranslation: configure };
  const state = { gameId: "game", title: "Test game", attemptId: "attempt", state: "queued", provider: "deepseek" };
  const view = render(<DetailSheet {...props} unityTranslationState={state} />);
  await screen.findByText("等待配置翻译插件");
  expect(screen.getByRole("button", { name: "配置或重试翻译插件" })).toBeDisabled();
  view.rerender(<DetailSheet {...props} unityTranslationState={{ ...state, state: "blocked", reason: "Select actual game EXE" }} />);
  await screen.findByRole("alert");
  expect(screen.getByRole("alert")).toHaveTextContent("Select actual game EXE");
  expect(screen.getByRole("button", { name: "配置或重试翻译插件" })).toBeEnabled();
});

it("starts the explicitly selected suggestion without making it a default", async () => {
  respond(profiles());
  const play = vi.fn().mockResolvedValue(undefined);
  render(<DetailSheet game={game} tags={[]} onClose={vi.fn()} onPlay={play} onChanged={vi.fn()} initialTab="launch" supportsSuggestions />);
  const entry = await screen.findByText("D:/game/game_chs.exe");
  fireEvent.click(within(entry.closest("li")!).getByRole("button", { name: "开始游戏" }));
  await waitFor(() => expect(play).toHaveBeenCalledWith("game", "profile"));
  expect(operation.mock.calls.some(call => call[0] === "profiles.set_default")).toBe(false);
});

it("uses the automatic detection label and hides redundant launch evidence", async () => {
  respond([{ ...profiles()[0], suggestionReasons: ["engine-evidence", "single-entry", "chinese-name"] }]);
  render(<DetailSheet game={game} tags={[]} onClose={vi.fn()} onPlay={vi.fn()} onChanged={vi.fn()} initialTab="launch" supportsSuggestions />);
  expect(await screen.findByRole("button", { name: "自动识别启动方式" })).toBeEnabled();
  expect(await screen.findByText("中文入口名称")).toBeInTheDocument();
  expect(screen.queryByText(/建议入口持续运行30秒/)).not.toBeInTheDocument();
  expect(screen.queryByText(/引擎目录证据|目录中唯一游戏入口/)).not.toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "识别建议启动方式" })).not.toBeInTheDocument();
});

it("restores a discarded entry with the current revision", async () => {
  respond(profiles("discarded"));
  render(<DetailSheet game={game} tags={[]} onClose={vi.fn()} onPlay={vi.fn()} onChanged={vi.fn()} initialTab="launch" supportsSuggestions />);
  const restore = await screen.findByRole("button", { name: "恢复入口" });
  expect(screen.queryByRole("button", { name: "设为默认" })).not.toBeInTheDocument();
  fireEvent.click(restore);
  await waitFor(() => expect(operation).toHaveBeenCalledWith("profiles.restore", { profileId: "profile", expectedRevision: 2 }, "profiles.restore:profile:2"));
});

it("refreshes profile status without overwriting an unsaved game title", async () => {
  respond(profiles());
  const props = { game, tags: [], onClose: vi.fn(), onPlay: vi.fn(), onChanged: vi.fn() };
  const view = render(<DetailSheet {...props} refreshToken={1} />);
  await waitFor(() => expect(operation).toHaveBeenCalledWith("games.get", { gameId: "game" }));
  await waitFor(() => expect(screen.getByRole("textbox", { name: "游戏标题" })).toHaveValue("Test game"));
  fireEvent.change(screen.getByRole("textbox", { name: "游戏标题" }), { target: { value: "Unsaved title" } });
  respond(profiles("verified"));
  view.rerender(<DetailSheet {...props} refreshToken={2} />);
  await waitFor(() => expect(operation.mock.calls.filter(call => call[0] === "profiles.list")).toHaveLength(2));
  expect(screen.getByRole("textbox", { name: "游戏标题" })).toHaveValue("Unsaved title");
});

it("imports pasted images only at the focused cover and leaves text inputs alone", async () => {
  respond([]);
  render(<DetailSheet game={game} tags={[]} onClose={vi.fn()} onPlay={vi.fn()} onChanged={vi.fn()} />);
  const input = await screen.findByRole("textbox", { name: "游戏标题" });
  const file = new File([new Uint8Array([1, 2, 3])], "cover.png", { type: "image/png" });
  const clipboardData = { items: [{ kind: "file", type: "image/png", getAsFile: () => file }] };
  fireEvent.paste(input, { clipboardData });
  expect(operation.mock.calls.some(([name]) => name === "assets.import")).toBe(false);
  const cover = screen.getByRole("button", { name: "封面：点击后按 Ctrl+V 粘贴图片" });
  fireEvent.click(cover);
  expect(cover).toHaveFocus();
  fireEvent.paste(cover, { clipboardData });
  await waitFor(() => expect(operation).toHaveBeenCalledWith("assets.import",
    { gameId: "game", imageBase64: "AQID", mimeType: "image/png" }, "assets.import:game:paste:AQID"));
});

it("rejects an oversized clipboard image before reading or importing it", async () => {
  respond([]);
  render(<DetailSheet game={game} tags={[]} onClose={vi.fn()} onPlay={vi.fn()} onChanged={vi.fn()} />);
  const cover = screen.getByRole("button", { name: "封面：点击后按 Ctrl+V 粘贴图片" });
  const file = new File([new Uint8Array(5 * 1024 * 1024 + 1)], "cover.png", { type: "image/png" });
  await waitFor(() => expect(screen.getByRole("textbox", { name: "游戏标题" })).toHaveValue(game.title));
  fireEvent.paste(cover, { clipboardData: { items: [{ kind: "file", type: "image/png", getAsFile: () => file }] } });
  await waitFor(() => expect(screen.getByRole("alert")).toHaveTextContent("图片超过 5 MiB 上限"));
  expect(operation.mock.calls.some(([name]) => name === "assets.import")).toBe(false);
});

it("restores a historical cover using assets.choose and the game revision", async () => {
  operation.mockImplementation(async name => ({ data: name === "games.get" ? { ...game, coverAssetId: "current" }
    : name === "profiles.list" ? { items: [] }
    : name === "assets.list" ? { items: [{ assetId: "old", isCurrent: false }, { assetId: "current", isCurrent: true }] }
    : name === "assets.choose" ? { assetId: "old", warning: "同步失败，原图已保留" } : {} }));
  render(<DetailSheet game={game} tags={[]} onClose={vi.fn()} onPlay={vi.fn()} onChanged={vi.fn()} />);
  fireEvent.click(screen.getByRole("button", { name: "封面：点击后按 Ctrl+V 粘贴图片" }));
  fireEvent.click(await screen.findByRole("button", { name: "恢复封面 1" }));
  await waitFor(() => expect(operation).toHaveBeenCalledWith("assets.choose", { gameId: "game", assetId: "old", expectedRevision: 1 }, "assets.choose:game:old:1"));
  await waitFor(() => expect(screen.getByRole("alert")).toHaveTextContent("同步失败，原图已保留"));
});
