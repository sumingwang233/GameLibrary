import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useState } from "react";
import { UnityTranslationDialog } from "./UnityTranslationDialog";

const api = vi.hoisted(() => ({ operation: vi.fn() }));
vi.mock("../lib/api", () => ({ operation: api.operation, describeFailure: (error: Error) => error.message }));
vi.mock("@tauri-apps/plugin-dialog", () => ({ open: vi.fn() }));

let state = "configured";
const item = { gameId: "game", title: "示例游戏", attemptId: "attempt", state: "configured", provider: "deepseek", profileId: "profile", tagged: true };
const props = () => ({ enabled: true, settingsOpen: false, onCloseSettings: vi.fn(), request: null,
  reviewTagId: null as string | null, onReviewHandled: vi.fn(),
  onRequestHandled: vi.fn(), onPlay: vi.fn(async (): Promise<void> => undefined), onChanged: vi.fn(async () => undefined), onNavigate: vi.fn() });

function RequestedReview({ callbacks }: { callbacks: ReturnType<typeof props> }) {
  const [tag, setTag] = useState<string | null>("tag");
  return <UnityTranslationDialog {...callbacks} reviewTagId={tag} onReviewHandled={() => { callbacks.onReviewHandled(); setTag(null); }} />;
}

beforeEach(() => {
  state = "configured";
  api.operation.mockReset().mockImplementation(async (id: string) => {
    if (id === "unity_translation.settings.get") return { data: { provider: "deepseek", endpoint: "https://api.deepseek.com/chat/completions", model: "deepseek-flash", hasKey: true, boundTagId: "tag" } };
    if (id === "unity_translation.confirm") state = "declined";
    return { libraryInstanceId: "library", dataEpoch: "epoch", data: { items: [{ ...item, state }], needsSettings: false } };
  });
});
afterEach(cleanup);

describe("Unity translation launch confirmation", () => {
  it("reports manual submission immediately and forwards the backend block without launching", async () => {
    let finish!: () => void;
    let completed = false;
    const onStatusChange = vi.fn();
    api.operation.mockImplementation(async (id: string) => {
      if (id === "unity_translation.settings.get") return { data: { provider: "deepseek", endpoint: "https://api.deepseek.com/chat/completions", model: "deepseek-flash", hasKey: true } };
      if (id === "unity_translation.configure") {
        await new Promise<void>(resolve => { finish = resolve; });
        completed = true;
        return { data: { jobId: "job" } };
      }
      return { libraryInstanceId: "library", dataEpoch: "epoch", data: { items: completed ? [{ ...item, state: "blocked", reason: "Select actual game EXE" }] : [], needsSettings: false } };
    });
    const callbacks = props();
    render(<UnityTranslationDialog {...callbacks} request={{ gameId: "game", action: "configure" }} onStatusChange={onStatusChange} />);
    await waitFor(() => expect(onStatusChange).toHaveBeenCalledWith(expect.arrayContaining([expect.objectContaining({ gameId: "game", state: "queued" })])));
    expect(callbacks.onPlay).not.toHaveBeenCalled();
    finish();
    await waitFor(() => expect(onStatusChange).toHaveBeenCalledWith(expect.arrayContaining([expect.objectContaining({ state: "blocked", reason: "Select actual game EXE" })])));
    expect(api.operation).toHaveBeenCalledWith("unity_translation.configure", { gameIds: ["game"] }, "unity_translation.configure:game");
  });
  it("shows first-run guidance only after selecting a game from the requested tag", async () => {
    const callbacks = props();
    const reason = "IL2CPP 插件已配置；首次启动可能需要联网准备组件，请耐心等待，并在游戏内确认翻译效果";
    api.operation.mockImplementation(async (id: string) => id === "unity_translation.settings.get"
      ? { data: { provider: "deepseek", endpoint: "https://api.deepseek.com/chat/completions", model: "deepseek-flash", hasKey: true, boundTagId: "tag" } }
      : { libraryInstanceId: "library", dataEpoch: "epoch", data: { items: [{ ...item, reason }], needsSettings: false } });
    render(<RequestedReview callbacks={callbacks} />);
    fireEvent.click(await screen.findByRole("button", { name: "尝试运行游戏" }));
    await screen.findByText(new RegExp(reason));
    expect(callbacks.onPlay).not.toHaveBeenCalled();
  });

  it("confirms only the selected game after launch, then opens its details without prompting the next game", async () => {
    const callbacks = props();
    let finishLaunch!: () => void;
    callbacks.onPlay.mockImplementation(() => new Promise<void>(resolve => { finishLaunch = resolve; }));
    const implementation = api.operation.getMockImplementation()!;
    api.operation.mockImplementation(async (id: string, ...args: unknown[]) => {
      const result = await implementation(id, ...args);
      if (id === "unity_translation.status") result.data.items.push(
        { ...item, gameId: "second", title: "另一款游戏", attemptId: "second-attempt", state: "configured" },
        { ...item, gameId: "untagged", title: "标签外的游戏", attemptId: "untagged-attempt", state: "configured", tagged: false });
      return result;
    });
    render(<RequestedReview callbacks={callbacks} />);
    fireEvent.click((await screen.findAllByRole("button", { name: "尝试运行游戏" }))[0]);
    expect(screen.queryByText("标签外的游戏")).not.toBeInTheDocument();
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
    expect(callbacks.onNavigate).toHaveBeenCalledWith("game");
    expect(screen.queryByText(/另一款游戏 已自动配置翻译插件/)).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "尝试运行游戏" })).not.toBeInTheDocument();
  });

  it("declines without launching or submitting success", async () => {
    const callbacks = props();
    render(<RequestedReview callbacks={callbacks} />);
    fireEvent.click(await screen.findByRole("button", { name: "尝试运行游戏" }));
    await screen.findByText(/示例游戏 已自动配置翻译插件/);
    fireEvent.click(screen.getByRole("button", { name: "否" }));
    await waitFor(() => expect(api.operation).toHaveBeenCalledWith("unity_translation.confirm",
      { gameId: "game", attemptId: "attempt", success: false }, "unity_translation.confirm:attempt:false"));
    expect(callbacks.onPlay).not.toHaveBeenCalled();
    await waitFor(() => expect(screen.queryByText("尝试运行游戏")).not.toBeInTheDocument());
    expect(callbacks.onNavigate).toHaveBeenCalledWith("game");
  });

  it("does not interrupt startup or respond to an unrelated tag, even with pending configured games", async () => {
    const callbacks = props();
    const view = render(<UnityTranslationDialog {...callbacks} />);
    await waitFor(() => expect(api.operation).toHaveBeenCalledWith("unity_translation.status"));
    expect(screen.queryByText("尝试运行游戏")).not.toBeInTheDocument();
    view.rerender(<UnityTranslationDialog {...callbacks} reviewTagId="other" />);
    await waitFor(() => expect(callbacks.onReviewHandled).toHaveBeenCalled());
    expect(screen.queryByText("尝试运行游戏")).not.toBeInTheDocument();
    expect(callbacks.onPlay).not.toHaveBeenCalled();
  });

  it("keeps an optimistic row per concurrently configuring game and fails only the one that errored", async () => {
    let serverItems: Array<Record<string, unknown>> = [];
    const pendingConfigure = new Map<string, { resolve: (value: unknown) => void; reject: (reason: Error) => void }>();
    api.operation.mockImplementation(async (id: string, params?: { gameIds?: string[] }) => {
      if (id === "unity_translation.settings.get") return { data: { provider: "deepseek", endpoint: "https://api.deepseek.com/chat/completions", model: "deepseek-flash", hasKey: true } };
      if (id === "unity_translation.configure") return new Promise((resolve, reject) => {
        pendingConfigure.set(params!.gameIds![0], { resolve, reject });
      });
      return { libraryInstanceId: "library", dataEpoch: "epoch", data: { items: serverItems, needsSettings: false } };
    });
    const onStatusChange = vi.fn();
    const callbacks = props();
    const configureCalls = () => api.operation.mock.calls.filter(([name]) => name === "unity_translation.configure");
    const rows = () => (onStatusChange.mock.lastCall?.[0] ?? []) as Array<{ gameId: string; attemptId: string; state: string }>;
    const view = render(<UnityTranslationDialog {...callbacks} request={{ gameId: "a", action: "configure" }} onStatusChange={onStatusChange} />);
    await waitFor(() => expect(configureCalls()).toHaveLength(1));
    // 同一游戏重复提交幂等忽略：不再发第二次请求。
    view.rerender(<UnityTranslationDialog {...callbacks} request={{ gameId: "a", action: "configure" }} onStatusChange={onStatusChange} />);
    await act(async () => { await Promise.resolve(); });
    expect(configureCalls()).toHaveLength(1);
    // 另一个游戏在 A 仍在途时提交：两条乐观行必须同时存在，attemptId 也不能撞车（重复 key 会吞掉一条）。
    view.rerender(<UnityTranslationDialog {...callbacks} request={{ gameId: "b", action: "configure" }} onStatusChange={onStatusChange} />);
    await waitFor(() => expect(configureCalls()).toHaveLength(2));
    expect(rows().map(row => `${row.gameId}:${row.state}`).sort()).toEqual(["a:queued", "b:queued"]);
    expect(new Set(rows().map(row => row.attemptId)).size).toBe(2);
    // A 成功：服务端只回 A 的 configured 行，B 的乐观行不能被冲掉。
    serverItems = [{ gameId: "a", title: "A 游戏", attemptId: "a-attempt", state: "configured", provider: "deepseek" }];
    await act(async () => { pendingConfigure.get("a")!.resolve({ data: { jobId: "job-a" } }); });
    await waitFor(() => expect(rows().map(row => `${row.gameId}:${row.state}`).sort()).toEqual(["a:configured", "b:queued"]));
    // B 失败：只有 B 置 failed，A 保持 configured。
    await act(async () => { pendingConfigure.get("b")!.reject(new Error("disk full")); });
    await waitFor(() => expect(screen.getByRole("alert")).toHaveTextContent("disk full"));
    expect(rows().find(row => row.gameId === "b")).toMatchObject({ state: "failed" });
    expect(rows().find(row => row.gameId === "a")).toMatchObject({ state: "configured" });
  });
});
