import { t } from "../lib/i18n";
import { Skeleton } from "./ui/skeleton";

const GRID_CLASS = "grid grid-cols-[repeat(auto-fill,minmax(var(--grid-min-card),1fr))] gap-4";

export function SkeletonGrid({ count = 12 }: { count?: number }) {
  return (
    <div role="status" aria-live="polite" aria-busy="true" className="space-y-4">
      <p className="text-sm text-text-secondary">{t("正在加载游戏库…")}</p>
      <div className={GRID_CLASS} aria-hidden="true">
        {Array.from({ length: count }, (_, index) => (
          <Skeleton key={index} className="aspect-3/4 rounded-lg" />
        ))}
      </div>
    </div>
  );
}
