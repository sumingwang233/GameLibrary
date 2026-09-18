import type { ReactNode } from "react";

export function AppShell({ sidebar, children }: { sidebar: ReactNode; children: ReactNode }) {
  return <div className="flex h-screen min-w-[980px] bg-background text-text-primary">{sidebar}{children}</div>;
}
