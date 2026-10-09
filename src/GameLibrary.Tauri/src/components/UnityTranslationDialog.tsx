import { useCallback, useEffect, useRef, useState } from "react";
import { open } from "@tauri-apps/plugin-dialog";
import { t } from "../lib/i18n";
import { describeFailure, operation } from "../lib/api";
import { Button } from "./ui/button";
import { ConfirmDialog } from "./ui/confirm-dialog";
import { Sheet } from "./ui/sheet";
import { UnityTranslationSettingsForm, type UnityProviderInput, type UnityProviderSettings } from "./UnityTranslationSettingsForm";

export interface UnityTranslationRequest { gameId: string; action: "configure" | "restore" }
export interface UnityTranslationItem {
  gameId: string; title: string; attemptId: string; state: string; provider: string;
  reason?: string | null; profileId?: string | null; jobId?: string | null;
}
interface UnityStatus { items: UnityTranslationItem[]; needsSettings: boolean }
const ACTIVE = new Set(["queued", "inspecting", "installing"]);

export function UnityTranslationDialog({ enabled, settingsOpen, onCloseSettings, request, onRequestHandled, onPlay, onChanged, onNavigate }: {
  enabled: boolean;
  settingsOpen: boolean;
  onCloseSettings: () => void;
  request: UnityTranslationRequest | null;
  onRequestHandled: () => void;
  onPlay: (gameId: string, profileId?: string) => Promise<void>;
  onChanged: () => Promise<void>;
  onNavigate: (gameId: string) => void;
}) {
  const [status, setStatus] = useState<UnityStatus>({ items: [], needsSettings: false });
  const [settings, setSettings] = useState<UnityProviderSettings | null>(null);
  const [mode, setMode] = useState<"settings" | "demo" | null>(null);
  const [prompt, setPrompt] = useState<{ item: UnityTranslationItem; result: boolean } | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const dismissed = useRef(new Set<string>());
  const wizardDismissed = useRef(false);
  const running = useRef(false);
  const alive = useRef(false);
  const identity = useRef("");
  const callbacks = useRef({ onCloseSettings, onRequestHandled, onPlay, onChanged, onNavigate });
  callbacks.current = { onCloseSettings, onRequestHandled, onPlay, onChanged, onNavigate };

  const refresh = useCallback(async () => {
    const result = await operation<UnityStatus>("unity_translation.status");
    if (!alive.current) return;
    const nextIdentity = `${result.libraryInstanceId}:${result.dataEpoch}`;
    if (identity.current && identity.current !== nextIdentity) {
      dismissed.current.clear(); wizardDismissed.current = false;
      setPrompt(null); setMode(null); setSettings(null);
    }
    identity.current = nextIdentity;
    if (!Array.isArray(result.data?.items) || result.data.items.some(item =>
      !item || [item.gameId, item.title, item.attemptId, item.state, item.provider].some(value => typeof value !== "string")))
      throw new Error(t("翻译插件状态不完整，请刷新后重试"));
    setStatus(result.data);
    setPrompt(previous => previous && result.data.items.some(item => item.attemptId === previous.item.attemptId && item.state === "configured") ? previous : null);
  }, []);

  const readSettings = useCallback(async () => {
    const result = await operation<UnityProviderSettings>("unity_translation.settings.get");
    const data = result.data;
    if (!data || [data.provider, data.endpoint, data.model].some(value => typeof value !== "string") || typeof data.hasKey !== "boolean")
      throw new Error(t("翻译插件设置不完整，请刷新后重试"));
    const next = { provider: data.provider, endpoint: data.endpoint, model: data.model, hasKey: data.hasKey };
    if (alive.current) setSettings(previous => previous && JSON.stringify(previous) === JSON.stringify(next) ? previous : next);
  }, []);

  useEffect(() => {
    if (!enabled) return;
    alive.current = true;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let polling = false;
    const poll = async () => {
      if (polling || !alive.current) return;
      polling = true;
      try { await refresh(); }
      catch (cause) { if (alive.current) setError(describeFailure(cause)); }
      finally { polling = false; if (alive.current) timer = setTimeout(() => void poll(), 2500); }
    };
    const changed = () => { clearTimeout(timer); void poll(); void readSettings().catch(() => undefined); };
    window.addEventListener("gamelibrary-unity-changed", changed);
    void readSettings().catch(cause => { if (alive.current) setError(describeFailure(cause)); });
    void poll();
    return () => { alive.current = false; clearTimeout(timer); window.removeEventListener("gamelibrary-unity-changed", changed); };
  }, [enabled, refresh, readSettings]);

  useEffect(() => {
    if (enabled && settingsOpen) {
      setMode("settings");
      void readSettings().catch(cause => setError(describeFailure(cause)));
    }
  }, [enabled, settingsOpen, readSettings]);

  useEffect(() => {
    if (!enabled || !settings || mode || prompt) return;
    if (status.needsSettings && !settings.hasKey && !wizardDismissed.current && status.items.some(item => item.state === "needs_settings")) {
      setMode("settings"); return;
    }
    const ready = status.items.find(item => item.state === "configured" && !dismissed.current.has(item.attemptId));
    if (ready) setPrompt({ item: ready, result: false });
  }, [enabled, mode, prompt, settings, status]);

  useEffect(() => {
    if (!enabled || !request) return;
    const intent = request;
    callbacks.current.onRequestHandled();
    setError(null);
    void (async () => {
      try {
        await operation(`unity_translation.${intent.action}`, intent.action === "configure" ? { gameIds: [intent.gameId] } : { gameId: intent.gameId },
          `unity_translation.${intent.action}:${intent.gameId}`);
        wizardDismissed.current = false;
        await refresh(); await callbacks.current.onChanged();
      } catch (cause) { if (alive.current) setError(describeFailure(cause)); }
    })();
  }, [enabled, request, refresh]);

  const closeSettings = () => {
    setMode(null); wizardDismissed.current = true; callbacks.current.onCloseSettings();
  };
  const saveSettings = async (input: UnityProviderInput) => {
    const result = await operation<UnityProviderSettings>("unity_translation.settings.set", { ...input }, "unity_translation.settings.set");
    if (!alive.current) return;
    setSettings(result.data); setMode(null); wizardDismissed.current = false;
    callbacks.current.onCloseSettings(); await refresh();
  };
  const importSettings = async () => {
    const path = await open({ multiple: false, directory: false, title: t("选择已有翻译插件 Config.ini"), filters: [{ name: "Config.ini", extensions: ["ini"] }] });
    if (typeof path !== "string") return;
    const result = await operation<UnityProviderSettings>("unity_translation.settings.import", { configPath: path }, "unity_translation.settings.import");
    if (!alive.current) return;
    setSettings(result.data); setMode(null); wizardDismissed.current = false;
    callbacks.current.onCloseSettings(); await refresh();
  };

  const answer = async (yes: boolean) => {
    if (!prompt || running.current) return;
    const current = prompt;
    running.current = true; setBusy(true); setError(null);
    try {
      if (!current.result && yes) {
        await callbacks.current.onPlay(current.item.gameId, current.item.profileId ?? undefined);
        if (alive.current) setPrompt({ item: current.item, result: true });
      } else {
        await operation("unity_translation.confirm", { gameId: current.item.gameId, attemptId: current.item.attemptId, success: current.result && yes },
          `unity_translation.confirm:${current.item.attemptId}:${current.result && yes}`);
        dismissed.current.add(current.item.attemptId);
        if (alive.current) setPrompt(null);
        await refresh(); await callbacks.current.onChanged();
      }
    } catch (cause) { if (alive.current) setError(describeFailure(cause)); }
    finally { running.current = false; if (alive.current) setBusy(false); }
  };

  const cancelQueue = async () => {
    setError(null);
    try {
      for (const jobId of new Set(status.items.filter(item => ACTIVE.has(item.state) && item.jobId).map(item => item.jobId!)))
        await operation("jobs.cancel", { jobId }, `jobs.cancel:${jobId}`);
      await refresh();
    } catch (cause) { setError(describeFailure(cause)); }
  };

  if (!enabled) return null;
  const active = status.items.filter(item => ACTIVE.has(item.state));
  const attention = status.items.filter(item => ["blocked", "failed", "needs_settings"].includes(item.state));
  const providerLabel = prompt?.item.provider === "deepseek" ? "DeepSeek" : t("自定义 OpenAI 兼容服务");
  return <>
    {(active.length > 0 || attention.length > 0 || error) && <aside className="fixed right-4 bottom-4 z-30 max-h-[40vh] w-[min(400px,92vw)] overflow-y-auto rounded-lg border border-border bg-panel p-4 text-sm shadow-lg" aria-label={t("Unity 翻译插件")}>
      {error && <p role="alert" className="mb-2 break-words text-danger">{error}</p>}
      {active.length > 0 && <div className="flex items-center justify-between gap-2"><span role="status">{t("正在配置翻译插件：{0} 个游戏", active.length)}</span><Button size="sm" variant="outline" onClick={() => void cancelQueue()}>{t("取消")}</Button></div>}
      {attention.length > 0 && <details><summary className="cursor-pointer text-text-secondary">{t("翻译插件需要处理：{0} 个游戏", attention.length)}</summary>
        <ul className="mt-2 space-y-2">{attention.map(item => <li key={item.attemptId} className="break-words"><button className="text-steam underline" onClick={() => callbacks.current.onNavigate(item.gameId)}>{item.title}</button><p className="text-xs text-text-secondary">{item.reason ? t(item.reason) : item.state}</p></li>)}</ul>
      </details>}
    </aside>}
    <Sheet open={mode !== null} onClose={closeSettings} title={mode === "demo" ? t("体验首次翻译设置") : t("Unity 翻译插件设置")}>
      {mode === "demo" || settings ? <UnityTranslationSettingsForm key={mode} settings={mode === "demo" ? null : settings} demo={mode === "demo"} onSave={saveSettings} onImport={importSettings} onCancel={closeSettings} />
        : <p>{t("正在读取设置…")}</p>}
      {mode === "settings" && <Button className="mt-5" variant="outline" onClick={() => setMode("demo")}>{t("体验首次设置（不保存）")}</Button>}
    </Sheet>
    <ConfirmDialog open={prompt !== null && mode === null} title={prompt?.result ? t("插件是否运行成功？") : t("尝试运行游戏")}
      description={(prompt ? prompt.result ? t("请在游戏中确认翻译效果。选择「是」将移除 {0} 的未翻译标签。", prompt.item.title)
        : t("{0} 已自动配置翻译插件（使用 {1}），是否尝试运行该游戏？", prompt.item.title, providerLabel) : "")
        + (prompt && !prompt.result && prompt.item.reason ? `\n${t(prompt.item.reason)}` : "") + (error ? `\n${error}` : "")}
      confirmLabel={t("是")} cancelLabel={t("否")} busy={busy} onConfirm={() => void answer(true)} onCancel={() => void answer(false)} />
  </>;
}
