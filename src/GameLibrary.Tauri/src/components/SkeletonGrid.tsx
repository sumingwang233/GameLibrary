import { Skeleton } from "./ui/skeleton";

export function SkeletonGrid() {
  return <div className="grid grid-cols-[repeat(auto-fill,minmax(180px,1fr))] gap-4">{Array.from({ length: 8 }, (_, index) => <Skeleton key={index} className="aspect-[3/4]" />)}</div>;
}
