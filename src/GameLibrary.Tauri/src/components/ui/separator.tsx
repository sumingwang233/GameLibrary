import { cn } from "../../lib/utils";

export function Separator({ className }: { className?: string }) {
  return <div className={cn("my-3 h-px w-full shrink-0 bg-border", className)} role="separator" />;
}
