import { t } from "../lib/i18n";
import { useEffect, useState } from "react";
import { X } from "lucide-react";
import type { ScanProgressState } from "../lib/state";
import { Button } from "./ui/button";

function formatElapsed(milliseconds: number) {
  const seconds = Math.max(0, Math.floor(milliseconds / 1000));
  if (seconds < 60) return t("{0} 秒", seconds);
  const minutes = Math.floor(seconds / 60);
  return t("{0} 分 {1} 秒", minutes, seconds % 60);
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
  const title =
    progress.phase === "starting"
      ? t("正在准备扫描…")
      : progress.phase === "cancelling"
        ? t("正在停止扫描…")
        : t("正在扫描游戏库…");

  return (
    <div
      role="status"
      aria-live="polite"
      className="mb-4 rounded-lg border border-steam/30 bg-steam-soft/40 px-4 py-3 text-sm text-text-primary"
    >
      <div className="flex items-start gap-3">
        <div className="min-w-0 flex-1">
          <p className="font-medium">{title}</p>
          <p className="mt-1 text-xs text-text-secondary">{t("已运行")} {formatElapsed(elapsed)}
          </p>
        </div>
        <Button
          variant="ghost"
          size="sm"
          onClick={onCancel}
          disabled={progress.phase === "cancelling"}
        >
          <X size={16} />{t("取消扫描")}</Button>
      </div>
      <div
        role="progressbar"
        aria-label={t("正在扫描游戏库")}
        className="mt-3 h-1.5 overflow-hidden rounded-full bg-steam/15"
      >
        <div className="scan-progress-indicator h-full w-1/3 rounded-full bg-steam" />
      </div>
    </div>
  );
}
