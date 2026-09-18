import { X } from "lucide-react";
import { Button } from "./ui/button";

export function ScanProgressBar({ text, onCancel }: { text: string | null; onCancel: () => void }) { if (!text) return null; return <div className="mb-4 flex items-center gap-3 rounded-lg border border-steam/30 bg-steam-soft/40 px-4 py-3 text-sm text-text-primary"><span className="h-2 w-2 animate-pulse rounded-full bg-steam" /><span className="flex-1">{text}</span><Button variant="ghost" className="min-h-8 px-2" onClick={onCancel} aria-label="取消扫描"><X size={16} /></Button></div>; }
