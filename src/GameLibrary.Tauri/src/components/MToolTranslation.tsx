import { useEffect, useRef, useState } from "react";
import { open } from "@tauri-apps/plugin-dialog";
import { describeFailure, operation } from "../lib/api";
import { t } from "../lib/i18n";
import { Button } from "./ui/button";

type Preparation = { state: string; reason: string; scriptPath?: string; backupPath?: string };

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
    <h3 className="font-semibold">{t("MTool 翻译配方")}</h3>
    <p className="text-xs text-text-secondary">{t("添加未翻译标签时生成配方，启动时检查和修复；不会提前启动游戏或请求翻译 API。")}</p>
    <p className="break-all text-xs text-text-secondary">{root ?? t("尚未选择 MTool 目录")}</p>
    <Button variant="outline" disabled={busy} onClick={() => void select()}>{t("选择 MTool 目录")}</Button>
    {error && <p role="alert" className="text-xs text-danger">{error}</p>}
  </div>;
}

export function MToolGameTranslation({ gameId }: { gameId: string }) {
  const [status, setStatus] = useState<Preparation | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const serial = useRef(0);
  useEffect(() => {
    let active = true;
    setStatus(null); setError(null);
    const load = () => {
      const ticket = ++serial.current;
      void operation<Preparation>("mtool.status", { gameId })
        .then(result => { if (active && ticket === serial.current) setStatus(typeof result.data.reason === "string" && typeof result.data.state === "string" ? result.data : null); })
        .catch(cause => { if (active && ticket === serial.current) setError(t(describeFailure(cause))); });
    };
    load();
    window.addEventListener("gamelibrary-mtool-changed", load);
    return () => { active = false; window.removeEventListener("gamelibrary-mtool-changed", load); };
  }, [gameId]);
  const prepare = async () => {
    const ticket = ++serial.current;
    setBusy(true); setError(null);
    try {
      const result = await operation<Preparation>("mtool.prepare", { gameId }, `mtool.prepare:${gameId}`);
      if (ticket === serial.current) setStatus(result.data);
    } catch (cause) { if (ticket === serial.current) setError(t(describeFailure(cause))); }
    finally { setBusy(false); }
  };
  return <div className="space-y-2 rounded-md border border-border p-3">
    <h3 className="text-sm font-semibold">{t("MTool 翻译配方")}</h3>
    {status && <p role={status.state === "blocked" ? "alert" : "status"} className="break-words text-xs text-text-secondary">{t(status.reason)}</p>}
    {status?.scriptPath && <p className="break-all text-xs text-text-secondary">{status.scriptPath}</p>}
    {status?.backupPath && <p className="break-all text-xs text-text-secondary">{t("原配方备份")}: {status.backupPath}</p>}
    <Button variant="outline" disabled={busy} onClick={() => void prepare()}>{t(busy ? "正在生成配方…" : "生成或修复 MTool 配方")}</Button>
    {error && <p role="alert" className="text-xs text-danger">{error}</p>}
  </div>;
}
