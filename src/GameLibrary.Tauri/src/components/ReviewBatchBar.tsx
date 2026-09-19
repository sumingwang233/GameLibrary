import { Check, EyeOff, Pause } from "lucide-react";
import type { CandidateItem } from "../lib/types";
import { Button } from "./ui/button";

export function ReviewBatchBar({
  candidates,
  selectedCount,
  busy,
  onAccept,
  onDefer,
  onIgnore,
  onToggleAll,
}: {
  candidates: CandidateItem[];
  selectedCount: number;
  busy: boolean;
  onAccept: () => void;
  onDefer: () => void;
  onIgnore: () => void;
  onToggleAll: () => void;
}) {
  if (candidates.length === 0) return null;
  const allSelected = selectedCount === candidates.length;

  return (
    <div className="mb-4 flex flex-wrap items-center gap-2 rounded-lg border border-border bg-surface px-4 py-3">
      <Button variant="ghost" size="sm" onClick={onToggleAll} aria-pressed={allSelected}>
        <Check size={14} />
        {allSelected ? "取消全选" : "全选"}
      </Button>
      <span className="mr-auto text-sm text-text-secondary">
        <strong className="text-text-primary">{candidates.length.toLocaleString()}</strong> 个项目待确认
        {selectedCount > 0 && ` · 已选 ${selectedCount.toLocaleString()}`}
      </span>
      <Button variant="outline" size="sm" disabled={selectedCount === 0 || busy} onClick={onAccept}>
        <Check size={15} />
        加入
      </Button>
      <Button variant="ghost" size="sm" disabled={selectedCount === 0 || busy} onClick={onDefer}>
        <Pause size={15} />
        暂不处理
      </Button>
      <Button variant="danger" size="sm" disabled={selectedCount === 0 || busy} onClick={onIgnore}>
        <EyeOff size={15} />
        忽略
      </Button>
    </div>
  );
}
