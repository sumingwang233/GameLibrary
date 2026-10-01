import { t } from "../lib/i18n";
import { useEffect, useState } from "react";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { FolderPlus, FolderSearch, FolderTree, Tags, PlusCircle, Maximize2, Minus, Moon, Settings2, Sun, X } from "lucide-react";
import { useSettings } from "../lib/settings";
import { Button } from "./ui/button";

export function TitleBar({ onAddRoot, onScan, onSettings, scanning, onTags, onRoots, onManualAdd }: {
  onTags: () => void;
  onRoots: () => void;
  onManualAdd: () => void;
  onAddRoot: () => void;
  onScan: () => void;
  onSettings: () => void;
  scanning: boolean;
}) {
  const { settings, loading, error: settingsError, update: updateSettings } = useSettings();
  const [themeBusy, setThemeBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [maximized, setMaximized] = useState(false);
  useEffect(() => {
    const window = getCurrentWindow();
    const update = () => { void window.isMaximized().then(setMaximized).catch(() => {}); };
    update();
    const stop = window.onResized(update);
    return () => { void stop.then((unlisten) => unlisten()); };
  }, []);
  const control = async (action: "minimize" | "toggleMaximize" | "close") => {
    try { await getCurrentWindow()[action](); }
    catch { setError(t("窗口操作失败，请使用 Alt+F4 关闭窗口。")); }
  };
  const toggleTheme = async () => {
    setThemeBusy(true);
    try {
      await updateSettings({ theme: document.documentElement.classList.contains("light") ? "dark" : "light" });
    } finally {
      setThemeBusy(false);
    }
  };
  const displayError = error ?? settingsError;
  return (
    <header className="flex h-12 shrink-0 items-center border-b border-border bg-surface text-text-primary select-none">
      <div data-tauri-drag-region className="flex h-full min-w-40 shrink-0 items-center gap-2 px-4">
        <img src="/app-icon.png" alt="" width={40} height={40} className="pointer-events-none h-10 w-10 shrink-0 object-contain" />
        <span className="pointer-events-none text-sm font-semibold tracking-wide">GameLibrary</span>
      </div>
      <nav aria-label={t("快捷操作")} className="flex min-w-0 items-center gap-1 overflow-x-auto [scrollbar-width:thin]">
        <Button variant="ghost" size="sm" onClick={onTags}><Tags size={15} />{t("管理标签")}</Button>
        <Button variant="ghost" size="sm" onClick={onRoots}><FolderTree size={15} />{t("游戏库目录")}</Button>
        <Button variant="ghost" size="sm" onClick={onAddRoot}><FolderPlus size={15} />{t("添加游戏库")}</Button>
        <Button variant="ghost" size="sm" onClick={onManualAdd}><PlusCircle size={15} />{t("手动添加游戏")}</Button>
        <Button variant="ghost" size="sm" onClick={onScan} disabled={scanning}><FolderSearch size={15} />{scanning ? t("正在扫描") : t("扫描游戏库")}</Button>
        <Button size="sm" className="mx-2 shadow-sm" onClick={onSettings}><Settings2 size={16} />{t("设置")}</Button>
      </nav>
      <div data-tauri-drag-region className="h-full flex-1" />
      {displayError && <span role="alert" className="text-xs text-danger">{displayError}</span>}
      <Button type="button" variant="ghost" size="icon" className="h-full w-12 rounded-none"
        aria-label={t("切换深色/浅色模式")} title={t("切换深色/浅色模式")}
        disabled={loading || !settings || themeBusy} onClick={() => void toggleTheme()}>
        <Sun size={20} aria-hidden="true" className="hidden [html.light_&]:block" />
        <Moon size={20} aria-hidden="true" className="[html.light_&]:hidden" />
      </Button>
      <div className="flex h-full shrink-0" aria-label={t("窗口控制")}>
        <button aria-label={t("最小化")} className="w-12 hover:bg-white/10 focus-visible:outline-2 focus-visible:outline-steam" onClick={() => void control("minimize")}><Minus size={16} className="mx-auto" /></button>
        <button aria-label={maximized ? t("还原窗口") : t("最大化")} className="w-12 hover:bg-white/10 focus-visible:outline-2 focus-visible:outline-steam" onClick={() => void control("toggleMaximize")}><Maximize2 size={14} className="mx-auto" /></button>
        <button aria-label={t("关闭窗口")} className="w-12 hover:bg-red-600 hover:text-white focus-visible:outline-2 focus-visible:outline-steam" onClick={() => void control("close")}><X size={17} className="mx-auto" /></button>
      </div>
    </header>
  );
}
