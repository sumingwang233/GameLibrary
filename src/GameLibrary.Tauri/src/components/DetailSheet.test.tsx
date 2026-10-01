import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import type { GameItem, ProfileItem } from "../lib/types";
import { DetailSheet } from "./DetailSheet";

const { operation } = vi.hoisted(() => ({ operation: vi.fn() }));
vi.mock("../lib/api", () => ({ operation, assetDataUrl: vi.fn(), describeFailure: (cause: unknown) => String(cause) }));
afterEach(() => { cleanup(); operation.mockReset(); });

const game: GameItem = { gameId: "game", title: "Test game", rootPath: "D:/game", kind: "folderGame", favorite: false, revision: 1 };
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
