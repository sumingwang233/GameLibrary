import { useState } from "react";
import { Inbox } from "lucide-react";
import type { CandidateItem } from "../lib/types";
import { EmptyState } from "./EmptyState";
import { ReviewBatchBar } from "./ReviewBatchBar";
import { Checkbox } from "./ui/checkbox";

export type ReviewAction = "accept" | "defer" | "ignore";

export function ReviewList({
  candidates,
  busy,
  onReview,
}: {
  candidates: CandidateItem[];
  busy: boolean;
  onReview: (items: CandidateItem[], action: ReviewAction) => Promise<void>;
}) {
  const [selected, setSelected] = useState<Set<string>>(new Set());

  if (candidates.length === 0) {
    return (
      <EmptyState
        icon={<Inbox size={44} strokeWidth={1.25} />}
        title="没有待确认项目"
        message="扫描到的新游戏会在这里等你确认。"
      />
    );
  }

  const chosen = candidates.filter((candidate) => selected.has(candidate.candidateId));

  const toggle = (candidateId: string) =>
    setSelected((previous) => {
      const next = new Set(previous);
      if (next.has(candidateId)) next.delete(candidateId);
      else next.add(candidateId);
      return next;
    });

  const apply = async (action: ReviewAction) => {
    await onReview(chosen, action);
    setSelected(new Set());
  };

  return (
    <div>
      <ReviewBatchBar
        candidates={candidates}
        selectedCount={chosen.length}
        busy={busy}
        onAccept={() => void apply("accept")}
        onDefer={() => void apply("defer")}
        onIgnore={() => void apply("ignore")}
        onToggleAll={() =>
          setSelected((previous) =>
            previous.size === candidates.length
              ? new Set()
              : new Set(candidates.map((candidate) => candidate.candidateId)),
          )
        }
      />
      <ul className="space-y-2">
        {candidates.map((candidate) => (
          <li key={candidate.candidateId}>
            <label className="flex cursor-pointer items-center gap-4 rounded-lg border border-border bg-surface p-4 hover:border-steam/60">
              <Checkbox
                checked={selected.has(candidate.candidateId)}
                onCheckedChange={() => toggle(candidate.candidateId)}
                aria-label={`选择 ${candidate.relativePath || candidate.physicalPath}`}
              />
              <span
                aria-hidden="true"
                className="flex h-12 w-12 shrink-0 items-center justify-center rounded-md bg-steam-soft text-xl text-steam"
              >
                ◇
              </span>
              <span className="min-w-0 flex-1">
                <span className="block truncate font-semibold text-text-primary">
                  {candidate.relativePath || candidate.physicalPath}
                </span>
                <span className="mt-1 block truncate text-xs text-text-secondary">
                  {candidate.kind} · {candidate.physicalPath}
                </span>
              </span>
            </label>
          </li>
        ))}
      </ul>
    </div>
  );
}
