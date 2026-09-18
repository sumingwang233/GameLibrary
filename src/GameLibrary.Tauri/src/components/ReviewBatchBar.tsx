import { Check, EyeOff, Pause } from "lucide-react";
import type { CandidateItem } from "../lib/types";
import { Button } from "./ui/button";

export function ReviewBatchBar({ candidates, selectedCount, onAccept, onDefer, onIgnore }: { candidates: CandidateItem[]; selectedCount: number; onAccept: () => void; onDefer: () => void; onIgnore: () => void }) {
  if (!candidates.length) return null;
  return <div className="mb-4 flex items-center gap-2 rounded-lg border border-border bg-surface px-4 py-3"><span className="mr-auto text-sm text-text-secondary"><strong className="text-text-primary">{candidates.length.toLocaleString()}</strong> 个项目待确认 · 已选 {selectedCount}</span><Button variant="outline" disabled={!selectedCount} onClick={onAccept}><Check size={15} />加入</Button><Button variant="ghost" disabled={!selectedCount} onClick={onDefer}><Pause size={15} />暂不处理</Button><Button variant="danger" disabled={!selectedCount} onClick={onIgnore}><EyeOff size={15} />忽略</Button></div>;
}
