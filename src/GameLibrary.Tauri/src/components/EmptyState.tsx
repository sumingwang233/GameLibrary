import type { ReactNode } from "react";

export function EmptyState({
  icon,
  title,
  message,
}: {
  icon: ReactNode;
  title: string;
  message: string;
}) {
  return (
    <div className="flex min-h-[420px] items-center justify-center rounded-lg border border-dashed border-border bg-surface/50 text-center">
      <div className="max-w-md px-6">
        <div aria-hidden="true" className="flex justify-center text-steam/40">
          {icon}
        </div>
        <h2 className="mt-4 text-xl font-semibold text-text-primary">{title}</h2>
        <p className="mt-2 text-sm text-text-secondary">{message}</p>
      </div>
    </div>
  );
}
