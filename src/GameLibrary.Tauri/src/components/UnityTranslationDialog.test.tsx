import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { UnityTranslationDialog } from "./UnityTranslationDialog";

const api = vi.hoisted(() => ({ operation: vi.fn() }));
vi.mock("../lib/api", () => ({ operation: api.operation, describeFailure: (error: Error) => error.message }));
vi.mock("@tauri-apps/plugin-dialog", () => ({ open: vi.fn() }));

let state = "configured";
const item = { gameId: "game", title: "示例游戏", attemptId: "attempt", state: "configured", provider: "deepseek", profileId: "profile" };
const props = () => ({ enabled: true, settingsOpen: false, onCloseSettings: vi.fn(), request: null,
  onRequestHandled: vi.fn(), onPlay: vi.fn(async (): Promise<void> => undefined), onChanged: vi.fn(async () => undefined), onNavigate: vi.fn() });

beforeEach(() => {
  state = "configured";
  api.operation.mockReset().mockImplementation(async (id: string) => {
    if (id === "unity_translation.settings.get") return { data: { provider: "deepseek", endpoint: "https://api.deepseek.com/chat/completions", model: "deepseek-flash", hasKey: true } };
    if (id === "unity_translation.confirm") state = "declined";
    return { libraryInstanceId: "library", dataEpoch: "epoch", data: { items: [{ ...item, state }], needsSettings: false } };
  });
});
afterEach(cleanup);

describe("Unity translation launch confirmation", () => {
  it("shows first-run preparation guidance without starting the game automatically", async () => {
    const callbacks = props();
    const reason = "IL2CPP 插件已配置；首次启动可能需要联网准备组件，请耐心等待，并在游戏内确认翻译效果";
    api.operation.mockImplementation(async (id: string) => id === "unity_translation.settings.get"
      ? { data: { provider: "deepseek", endpoint: "https://api.deepseek.com/chat/completions", model: "deepseek-flash", hasKey: true } }
      : { libraryInstanceId: "library", dataEpoch: "epoch", data: { items: [{ ...item, reason }], needsSettings: false } });
    render(<UnityTranslationDialog {...callbacks} />);
    await screen.findByText(new RegExp(reason));
    expect(callbacks.onPlay).not.toHaveBeenCalled();
  });

  it("recovers the prompt from the snapshot and confirms success only after launch and the user's second answer", async () => {
    const callbacks = props();
    let finishLaunch!: () => void;
    callbacks.onPlay.mockImplementation(() => new Promise<void>(resolve => { finishLaunch = resolve; }));
    render(<UnityTranslationDialog {...callbacks} />);
    await screen.findByText(/示例游戏 已自动配置翻译插件/);
    fireEvent.click(screen.getByRole("button", { name: "是" }));
    expect(callbacks.onPlay).toHaveBeenCalledWith("game", "profile");
    expect(screen.queryByText("插件是否运行成功？")).not.toBeInTheDocument();
    expect(api.operation.mock.calls.some(call => call[0] === "unity_translation.confirm")).toBe(false);
    finishLaunch();
    await screen.findByText("插件是否运行成功？");
    fireEvent.click(screen.getByRole("button", { name: "是" }));
    await waitFor(() => expect(api.operation).toHaveBeenCalledWith("unity_translation.confirm",
      { gameId: "game", attemptId: "attempt", success: true }, "unity_translation.confirm:attempt:true"));
    await waitFor(() => expect(callbacks.onChanged).toHaveBeenCalledOnce());
  });

  it("declines without launching or submitting success", async () => {
    const callbacks = props();
    render(<UnityTranslationDialog {...callbacks} />);
    await screen.findByText(/示例游戏 已自动配置翻译插件/);
    fireEvent.click(screen.getByRole("button", { name: "否" }));
    await waitFor(() => expect(api.operation).toHaveBeenCalledWith("unity_translation.confirm",
      { gameId: "game", attemptId: "attempt", success: false }, "unity_translation.confirm:attempt:false"));
    expect(callbacks.onPlay).not.toHaveBeenCalled();
    await waitFor(() => expect(screen.queryByText("尝试运行游戏")).not.toBeInTheDocument());
  });
});
