import { useEffect, useRef, useState } from "react";
import { open } from "@tauri-apps/plugin-dialog";
import { describeFailure, operation } from "../lib/api";
import { t } from "../lib/i18n";
import { Button } from "./ui/button";

type Preparation = { state: string; reason: string; scriptPath?: string; backupPath?: string };
type TranslationState = { userOverride: string; isRequired: boolean; revision: number };

export function MToolSettings() {
  const [root, setRoot] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    let active = true;
    void operation<{ toolRoot: string | null }>("mtool.settings.get").then(result => { if (active) setRoot(result.data.toolRoot); })
      .catch(cause => { if (active) setError(t(describeFailure(cause))); });
    return () => { active = false; };
  }, []);
  const select = async () => {
    setBusy(true); setError(null);
    try {
      const path = await open({ directory: true, multiple: false, title: t("选择 MTool 目录") });
      if (typeof path !== "string") return;
      const result = await operation<{ toolRoot: string }>("mtool.settings.set", { toolRoot: path }, `mtool.settings.set:${path}`);
      setRoot(result.data.toolRoot);
    } catch (cause) { setError(t(describeFailure(cause))); }
    finally { setBusy(false); }
  };
  return <div className="space-y-2 text-sm">
    <h3 className="font-semibold">{t("MTool 翻译")}</h3>
    <p className="text-xs text-text-secondary">{t("选择包含 MTool.exe 和 loaders 文件夹的目录，只需设置一次。")}</p>
    <p className="text-xs text-text-secondary">{t("给非 Unity 游戏添加“未翻译”标签，或在游戏详情中生成 MTool 启动脚本，再点击“开始游戏”。不支持自动配置的游戏会显示原因。")}</p>
    <p className="break-all text-xs text-text-secondary">{root ?? t("尚未选择 MTool 目录")}</p>
    <Button variant="outline" disabled={busy} onClick={() => void select()}>{t("选择 MTool 目录")}</Button>
    {error && <p role="alert" className="text-xs text-danger">{error}</p>}
  </div>;
}

export function MToolGameTranslation({ gameId }: { gameId: string }) {
  const [status, setStatus] = useState<Preparation | null>(null);
  const [translation, setTranslation] = useState<TranslationState | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const serial = useRef(0);
  useEffect(() => {
    let active = true;
    setStatus(null); setTranslation(null); setError(null);
    const load = () => {
      const ticket = ++serial.current;
      void operation<Preparation>("mtool.status", { gameId })
        .then(result => { if (active && ticket === serial.current) setStatus(typeof result.data.reason === "string" && typeof result.data.state === "string" ? result.data : null); })
        .catch(cause => { if (active && ticket === serial.current) setError(t(describeFailure(cause))); });
      // 翻译需求只决定按钮是否先写入显式 Required；读取失败不阻塞 MTool 状态显示。
      void operation<Partial<TranslationState>>("translation.get", { gameId })
        .then(result => {
          const data = result.data;
          if (active && ticket === serial.current && typeof data?.isRequired === "boolean"
            && typeof data?.revision === "number" && typeof data?.userOverride === "string") setTranslation(data as TranslationState);
        })
        .catch(() => undefined);
    };
    load();
    window.addEventListener("gamelibrary-mtool-changed", load);
    return () => { active = false; window.removeEventListener("gamelibrary-mtool-changed", load); };
  }, [gameId]);
  const prepare = async () => {
    const ticket = ++serial.current;
    setBusy(true); setError(null);
    try {
      // 界面承诺“点击生成即可翻译”：未标记需要翻译时先写显式 Required；NotRequired 是用户拒绝，不改。
      if (translation && !translation.isRequired && translation.userOverride !== "NotRequired") {
        const required = await operation<TranslationState>("translation.set",
          { gameId, override: "Required", expectedRevision: translation.revision },
          `translation.set:${gameId}:${translation.revision}:Required`);
        if (ticket === serial.current) setTranslation(required.data);
      }
      const result = await operation<Preparation>("mtool.prepare", { gameId }, `mtool.prepare:${gameId}`);
      if (ticket === serial.current) setStatus(result.data);
    } catch (cause) { if (ticket === serial.current) setError(t(describeFailure(cause))); }
    finally { setBusy(false); }
  };
  const needsRequirement = translation !== null && !translation.isRequired && translation.userOverride !== "NotRequired";
  return <div className="space-y-2 rounded-md border border-border p-3">
    <h3 className="text-sm font-semibold">{t("MTool 翻译")}</h3>
    <details>
      <summary className="cursor-pointer text-xs text-steam">{t("MTool 目录设置")}</summary>
      <div className="mt-2"><MToolSettings /></div>
    </details>
    {status && <p role={status.state === "blocked" ? "alert" : "status"} className="break-words text-xs text-text-secondary">{t(status.reason)}</p>}
    {status?.scriptPath && <p className="break-all text-xs text-text-secondary">{status.scriptPath}</p>}
    {status?.backupPath && <p className="break-all text-xs text-text-secondary">{t("原配方备份")}: {status.backupPath}</p>}
    <Button variant="outline" disabled={busy} onClick={() => void prepare()}>{t(busy ? "正在生成启动脚本…" : needsRequirement ? "标记为需要翻译并生成启动脚本" : "生成或修复 MTool 启动脚本")}</Button>
    {error && <p role="alert" className="text-xs text-danger">{error}</p>}
  </div>;
}
