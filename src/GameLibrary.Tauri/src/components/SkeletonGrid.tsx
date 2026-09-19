import { Skeleton } from "./ui/skeleton";

const GRID_CLASS = "grid grid-cols-[repeat(auto-fill,minmax(var(--grid-min-card),1fr))] gap-4";

export function SkeletonGrid({ count = 12 }: { count?: number }) {
  return (
    <div className={GRID_CLASS} aria-hidden="true">
      {Array.from({ length: count }, (_, index) => (
        <Skeleton key={index} className="aspect-3/4 rounded-lg" />
      ))}
    </div>
  );
}
