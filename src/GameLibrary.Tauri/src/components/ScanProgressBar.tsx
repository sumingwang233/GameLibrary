import { useEffect, useState } from "react";
import { X } from "lucide-react";
import type { ScanProgressState } from "../lib/state";
import { Button } from "./ui/button";

function formatElapsed(milliseconds: number) {
  const seconds = Math.max(0, Math.floor(milliseconds / 1000));
  if (seconds < 60) return `${seconds} 秒`;
  const minutes = Math.floor(seconds / 60);
  return `${minutes} 分 ${seconds % 60} 秒`;
}

export function ScanProgressBar({
  progress,
  onCancel,
}: {
  progress: ScanProgressState | null;
  onCancel: () => void;
}) {
  const [now, setNow] = useState(Date.now());

  useEffect(() => {
    if (!progress) return;
    setNow(Date.now());
    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, [progress?.startedAt]);

  if (!progress) return null;
  const elapsed = Math.max(0, now - progress.startedAt);
  const elapsedSeconds = Math.max(1, elapsed / 1000);
  const averageRate = Math.round(progress.scannedDirectories / elapsedSeconds);
  const title =
    progress.phase === "starting"
      ? "正在准备扫描…"
      : progress.phase === "cancelling"
        ? "正在停止扫描…"
        : `已检查 ${progress.scannedDirectories.toLocaleString()} 个文件夹，找到 ${progress.candidatesFound.toLocaleString()} 个游戏位置`;

  return (
    <div
      role="status"
      aria-live="polite"
      className="mb-4 rounded-lg border border-steam/30 bg-steam-soft/40 px-4 py-3 text-sm text-text-primary"
    >
      <div className="flex items-start gap-3">
        <div className="min-w-0 flex-1">
          <p className="font-medium">{title}</p>
          <p className="mt-1 text-xs text-text-secondary">
            {progress.activeRoots} 个游戏库位置 · 已运行 {formatElapsed(elapsed)}
            {progress.phase === "running" && averageRate > 0
              ? ` · 平均每秒检查 ${averageRate.toLocaleString()} 个文件夹`
              : ""}
          </p>
          <p className="mt-1 text-xs text-text-secondary">
            扫描完成后会自动去重，只显示需要确认的新游戏。
          </p>
        </div>
        <Button
          variant="ghost"
          size="sm"
          onClick={onCancel}
          disabled={progress.phase === "cancelling"}
        >
          <X size={16} />
          取消扫描
        </Button>
      </div>
      <div
        role="progressbar"
        aria-label="正在扫描游戏库"
        className="mt-3 h-1.5 overflow-hidden rounded-full bg-steam/15"
      >
        <div className="scan-progress-indicator h-full w-1/3 rounded-full bg-steam" />
      </div>
    </div>
  );
}
