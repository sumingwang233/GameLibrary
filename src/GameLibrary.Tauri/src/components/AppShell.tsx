import type { ReactNode } from "react";

export function AppShell({ sidebar, children, titleBar }: { sidebar: ReactNode; children: ReactNode; titleBar: ReactNode }) {
  return (
    <div className="flex h-screen min-w-[980px] flex-col bg-background text-text-primary">
      {titleBar}
      <div className="flex min-h-0 flex-1">
        {sidebar}
        {children}
      </div>
    </div>
  );
}
