import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { operation } from "../lib/api";
import { MToolGameTranslation, MToolSettings } from "./MToolTranslation";

vi.mock("../lib/api", async original => ({ ...await original<typeof import("../lib/api")>(), operation: vi.fn() }));
const { pick } = vi.hoisted(() => ({ pick: vi.fn() }));
vi.mock("@tauri-apps/plugin-dialog", () => ({ open: pick }));
afterEach(() => { cleanup(); vi.mocked(operation).mockReset(); pick.mockReset(); });

it("lets the user connect MTool directly from game details before explicitly preparing a recipe", async () => {
  vi.mocked(operation).mockImplementation((async (name: string) => ({ data:
    name === "mtool.settings.get" ? { toolRoot: null }
      : name === "mtool.settings.set" ? { toolRoot: "D:/MTool/Tool" }
        : { state: "idle", reason: "ready for setup" } })) as typeof operation);
  pick.mockResolvedValue("D:/MTool/Tool");
  render(<MToolGameTranslation gameId="game" />);
  fireEvent.click(screen.getByText("MTool 目录设置"));
  fireEvent.click(await screen.findByRole("button", { name: "选择 MTool 目录" }));
  await screen.findByText("D:/MTool/Tool");
  expect(screen.getByText(/选择包含 MTool.exe 和 loaders/)).toBeInTheDocument();
  expect(operation).toHaveBeenCalledWith("mtool.settings.set", { toolRoot: "D:/MTool/Tool" }, expect.any(String));
  expect(vi.mocked(operation).mock.calls.some(([name]) => name === "mtool.prepare" || name === "launch.execute")).toBe(false);
});

it("selects and saves one MTool installation without preparing or launching games", async () => {
  vi.mocked(operation).mockResolvedValueOnce({ data: { toolRoot: null } } as never)
    .mockResolvedValueOnce({ data: { toolRoot: "D:/mock MTool" } } as never);
  pick.mockResolvedValue("D:/mock MTool");
  render(<MToolSettings />);
  await screen.findByText("尚未选择 MTool 目录");
  fireEvent.click(screen.getByRole("button", { name: "选择 MTool 目录" }));
  await screen.findByText("D:/mock MTool");
  expect(operation).toHaveBeenLastCalledWith("mtool.settings.set", { toolRoot: "D:/mock MTool" }, "mtool.settings.set:D:/mock MTool");
  expect(operation).toHaveBeenCalledTimes(2);
});

it("ignores an old game's status and shows preparation feedback for the current game", async () => {
  let old!: (value: unknown) => void;
  vi.mocked(operation).mockImplementation(((name: string, parameters: { gameId?: string }) => {
    if (name === "mtool.status" && parameters.gameId === "old") return new Promise<unknown>(resolve => { old = resolve; });
    return Promise.resolve({ data: name === "mtool.prepare" ? { state: "ready", reason: "已生成 MTool 启动配方；尚未验证游戏内翻译效果" }
      : { state: "blocked", reason: "请先自动识别或选择明确的游戏启动方式" } });
  }) as typeof operation);
  const view = render(<MToolGameTranslation gameId="old" />);
  view.rerender(<MToolGameTranslation gameId="current" />);
  await screen.findByRole("alert");
  await act(async () => old({ data: { state: "ready", reason: "stale game status" } }));
  expect(screen.queryByText("stale game status")).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole("button", { name: "生成或修复 MTool 启动脚本" }));
  await waitFor(() => expect(screen.getByRole("status")).toHaveTextContent("已生成 MTool 启动配方"));
  expect(operation).toHaveBeenCalledWith("mtool.prepare", { gameId: "current" }, "mtool.prepare:current");
});

it("marks a game as translation-required before generating its MTool recipe", async () => {
  vi.mocked(operation).mockImplementation(((name: string) => Promise.resolve({ data:
    name === "translation.get" ? { userOverride: "Auto", effective: "Auto", isRequired: false, revision: 7 }
      : name === "translation.set" ? { userOverride: "Required", effective: "Required", isRequired: true, revision: 8 }
        : { state: "ready", reason: "已生成 MTool 启动配方；尚未验证游戏内翻译效果" } })) as typeof operation);
  render(<MToolGameTranslation gameId="game" />);
  fireEvent.click(await screen.findByRole("button", { name: "标记为需要翻译并生成启动脚本" }));
  await screen.findByRole("status");
  expect(operation).toHaveBeenCalledWith("translation.set",
    { gameId: "game", override: "Required", expectedRevision: 7 }, "translation.set:game:7:Required");
  expect(vi.mocked(operation).mock.calls.map(([name]) => name).filter(name => name === "translation.set" || name === "mtool.prepare"))
    .toEqual(["translation.set", "mtool.prepare"]);
});

it("keeps an explicit not-required policy and prepares without rewriting translation", async () => {
  vi.mocked(operation).mockImplementation(((name: string) => Promise.resolve({ data:
    name === "translation.get" ? { userOverride: "NotRequired", effective: "NotRequired", isRequired: false, revision: 3 }
      : { state: "skipped", reason: "游戏当前不需要翻译" } })) as typeof operation);
  render(<MToolGameTranslation gameId="game" />);
  fireEvent.click(await screen.findByRole("button", { name: "生成或修复 MTool 启动脚本" }));
  await screen.findByRole("status");
  expect(vi.mocked(operation).mock.calls.some(([name]) => name === "translation.set")).toBe(false);
  expect(operation).toHaveBeenCalledWith("mtool.prepare", { gameId: "game" }, "mtool.prepare:game");
});

it("does not let a delayed status read overwrite a newer preparation result", async () => {
  let finish!: (value: unknown) => void;
  vi.mocked(operation).mockImplementation(((name: string) => name === "mtool.status"
    ? new Promise<unknown>(resolve => { finish = resolve; })
    : Promise.resolve({ data: { state: "ready", reason: "MTool 配方已就绪；尚未验证游戏内翻译效果" } })) as typeof operation);
  render(<MToolGameTranslation gameId="game" />);
  fireEvent.click(screen.getByRole("button", { name: "生成或修复 MTool 启动脚本" }));
  await screen.findByRole("status");
  await act(async () => finish({ data: { state: "blocked", reason: "old failure" } }));
  expect(screen.getByRole("status")).toHaveTextContent("MTool 配方已就绪");
  expect(screen.queryByText("old failure")).not.toBeInTheDocument();
});
