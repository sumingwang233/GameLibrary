import { useEffect, useState } from "react";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { FolderPlus, FolderSearch, Maximize2, Minus, Settings2, X } from "lucide-react";
import { Button } from "./ui/button";

export function TitleBar({ onAddRoot, onScan, onSettings, scanning }: {
  onAddRoot: () => void;
  onScan: () => void;
  onSettings: () => void;
  scanning: boolean;
}) {
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
    catch { setError("窗口操作失败，请使用 Alt+F4 关闭窗口。"); }
  };
  return (
    <header className="flex h-12 shrink-0 items-center border-b border-border bg-surface text-text-primary select-none">
      <div data-tauri-drag-region className="flex h-full min-w-40 items-center gap-2 px-4">
        <img src="/app-icon.svg" alt="" className="pointer-events-none h-7 w-7" />
        <span className="pointer-events-none text-sm font-semibold tracking-wide">GameLibrary</span>
      </div>
      <nav aria-label="快捷操作" className="flex items-center gap-1">
        <Button variant="ghost" size="sm" onClick={onAddRoot}><FolderPlus size={15} />添加目录</Button>
        <Button variant="ghost" size="sm" onClick={onScan} disabled={scanning}><FolderSearch size={15} />{scanning ? "正在扫描" : "扫描游戏库"}</Button>
        <Button variant="ghost" size="sm" onClick={onSettings}><Settings2 size={15} />设置</Button>
      </nav>
      <div data-tauri-drag-region className="h-full flex-1" />
      {error && <span role="alert" className="text-xs text-danger">{error}</span>}
      <div className="flex h-full" aria-label="窗口控制">
        <button aria-label="最小化" className="w-12 hover:bg-white/10 focus-visible:outline-2 focus-visible:outline-steam" onClick={() => void control("minimize")}><Minus size={16} className="mx-auto" /></button>
        <button aria-label={maximized ? "还原窗口" : "最大化"} className="w-12 hover:bg-white/10 focus-visible:outline-2 focus-visible:outline-steam" onClick={() => void control("toggleMaximize")}><Maximize2 size={14} className="mx-auto" /></button>
        <button aria-label="关闭窗口" className="w-12 hover:bg-red-600 hover:text-white focus-visible:outline-2 focus-visible:outline-steam" onClick={() => void control("close")}><X size={17} className="mx-auto" /></button>
      </div>
    </header>
  );
}
