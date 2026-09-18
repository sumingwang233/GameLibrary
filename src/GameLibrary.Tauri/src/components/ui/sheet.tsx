import type { ReactNode } from "react";
import { X } from "lucide-react";
import { Button } from "./button";

export function Sheet({ open, onClose, title, children }: { open: boolean; onClose: () => void; title: string; children: ReactNode }) {
  if (!open) return null;
  return <div className="fixed inset-0 z-50 flex justify-end" role="dialog" aria-modal="true" aria-label={title}>
    <button className="absolute inset-0 cursor-default bg-black/60" onClick={onClose} aria-label="关闭详情" />
    <aside className="relative h-full w-[min(460px,92vw)] overflow-y-auto border-l border-border bg-[#1b2838] p-6 shadow-2xl">
      <div className="mb-6 flex items-center justify-between"><h2 className="text-lg font-bold text-text-primary">{title}</h2><Button variant="ghost" className="px-2" onClick={onClose} aria-label="关闭详情"><X size={18} /></Button></div>
      {children}
    </aside>
  </div>;
}
