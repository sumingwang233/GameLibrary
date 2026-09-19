import { X } from "lucide-react";
import { Button } from "./ui/button";

export function ScanProgressBar({ text, onCancel }: { text: string | null; onCancel: () => void }) {
  if (!text) return null;
  return (
    <div
      role="status"
      aria-live="polite"
      className="mb-4 flex items-center gap-3 rounded-lg border border-steam/30 bg-steam-soft/40 px-4 py-3 text-sm text-text-primary"
    >
      <span aria-hidden="true" className="h-2 w-2 shrink-0 animate-pulse rounded-full bg-steam" />
      <span className="flex-1">{text}</span>
      <Button variant="ghost" size="sm" onClick={onCancel}>
        <X size={16} />
        取消扫描
      </Button>
    </div>
  );
}
