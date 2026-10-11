import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import type { GameItem, ProfileItem, TagItem } from "../lib/types";
import { DetailSheet } from "./DetailSheet";
import { OperationError } from "../lib/api";

const { operation } = vi.hoisted(() => ({ operation: vi.fn() }));
vi.mock("../lib/api", async importOriginal => ({ ...await importOriginal<typeof import("../lib/api")>(), operation, assetDataUrl: vi.fn(), describeFailure: (cause: unknown) => String(cause) }));
afterEach(() => { cleanup(); operation.mockReset(); });

const game: GameItem = { gameId: "game", title: "Test game", rootPath: "D:/game", kind: "folderGame", favorite: false, revision: 1 };

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
  await waitFor(() => expect(operation.mock.calls.filter(call => call[0] === "profiles.list").length).toBeGreaterThan(2));
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
