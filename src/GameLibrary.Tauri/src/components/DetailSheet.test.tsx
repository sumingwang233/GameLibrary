import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import type { GameItem, ProfileItem } from "../lib/types";
import { DetailSheet } from "./DetailSheet";
import { OperationError } from "../lib/api";

const { operation } = vi.hoisted(() => ({ operation: vi.fn() }));
vi.mock("../lib/api", async importOriginal => ({ ...await importOriginal<typeof import("../lib/api")>(), operation, assetDataUrl: vi.fn(), describeFailure: (cause: unknown) => String(cause) }));
afterEach(() => { cleanup(); operation.mockReset(); });

const game: GameItem = { gameId: "game", title: "Test game", rootPath: "D:/game", kind: "folderGame", favorite: false, revision: 1 };

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
