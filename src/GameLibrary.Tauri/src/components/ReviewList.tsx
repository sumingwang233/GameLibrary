import { t } from "../lib/i18n";
import { useEffect, useState } from "react";
import { Inbox } from "lucide-react";
import type { CandidateItem, FlashCandidateInspection } from "../lib/types";
import type { ReviewOutcome } from "../lib/hooks/useCandidateReview";
import { describeFailure, operation } from "../lib/api";
import { EmptyState } from "./EmptyState";
import { ReviewBatchBar } from "./ReviewBatchBar";
import { Checkbox } from "./ui/checkbox";
import { Button } from "./ui/button";

export type ReviewAction = "accept" | "defer" | "ignore";

/** onReview 返回成功项；返回 void（旧实现）时视为整批成功，保留兼容。 */
type ReviewHandler = (items: CandidateItem[], action: ReviewAction) => Promise<ReviewOutcome | void>;

export function ReviewList({
  candidates,
  busy,
  onReview,
  total = candidates.length,
  hasMore = false,
  loadingMore = false,
  onLoadMore,
}: {
  candidates: CandidateItem[];
  busy: boolean;
  onReview: ReviewHandler;
  total?: number;
  hasMore?: boolean;
  loadingMore?: boolean;
  onLoadMore?: () => void;
}) {
  const [selected, setSelected] = useState<Set<string>>(new Set());
  useEffect(() => {
    const available = new Set(candidates.map(item => item.candidateId));
    setSelected(previous => [...previous].every(id => available.has(id)) ? previous
      : new Set([...previous].filter(id => available.has(id))));
  }, [candidates]);

  if (candidates.length === 0) {
    return (
      <EmptyState
        icon={<Inbox size={44} strokeWidth={1.25} />}
        title={t("没有待确认项目")}
        message={t("扫描到的新游戏会在这里等你确认。")}
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
    // 只清除后端确认成功的勾选：部分失败时失败项保持选中，用户可直接重试。
    // 整批失败（onReview 抛出）时保持全部勾选，错误由 App 的 actionError 呈现。
    try {
      const outcome = await onReview(chosen, action);
      const succeeded = new Set(outcome ? outcome.succeededIds : chosen.map(item => item.candidateId));
      if (succeeded.size === 0) return;
      setSelected(previous => new Set([...previous].filter(id => !succeeded.has(id))));
    } catch { /* 整批失败：保留勾选以便重试；错误提示由调用方负责。 */ }
  };

  return (
    <div>
      <ReviewBatchBar
        candidates={candidates}
        total={total}
        selectedCount={chosen.length}
        busy={busy}
        onAccept={() => void apply("accept")}
        onDefer={() => void apply("defer")}
        onIgnore={() => void apply("ignore")}
        onToggleAll={() =>
          setSelected((previous) =>
            candidates.every(candidate => previous.has(candidate.candidateId))
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
                aria-label={t("选择 {0}", candidate.relativePath || candidate.physicalPath)}
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
            {candidate.flash?.requiresReview && <FlashReviewEditor candidate={candidate} busy={busy} onReview={onReview} />}
          </li>
        ))}
      </ul>
      {hasMore && <Button variant="outline" className="mt-4 w-full" disabled={busy || loadingMore}
        onClick={onLoadMore}>{t(loadingMore ? "正在加载更多…" : "加载更多")}</Button>}
    </div>
  );
}

function FlashReviewEditor({ candidate, busy, onReview }: {
  candidate: CandidateItem; busy: boolean;
  onReview: ReviewHandler;
}) {
  const [detail, setDetail] = useState<FlashCandidateInspection | null>(null);
  const [kind, setKind] = useState<"project" | "collection" | "resources" | "">("");
  const [entries, setEntries] = useState<string[]>([]);
  const [preview, setPreview] = useState<FlashCandidateInspection | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState("");
  const valid = kind === "resources" || kind === "project" && entries.length === 1 || kind === "collection" && entries.length > 0;
  const inspect = async (forPreview = false) => {
    setLoading(true); setError("");
    try {
      const result = await operation<FlashCandidateInspection>("candidates.inspect", {
        candidateId: candidate.candidateId, ...(forPreview ? { flashKind: kind, entryPaths: entries } : {}),
      });
      setDetail(result.data);
      if (forPreview) setPreview(result.data);
    } catch (cause) { setError(describeFailure(cause)); }
    finally { setLoading(false); }
  };
  const submit = async () => {
    if (!preview || !kind) return;
    setLoading(true); setError("");
    try {
      await onReview([{ ...candidate, revision: preview.revision,
        flashReview: { kind, entryPaths: entries, adjustments: preview.adjustments.map(({ gameId, expectedRevision }) => ({ gameId, expectedRevision })) } }], "accept");
      setPreview(null);
    } catch (cause) { setError(describeFailure(cause)); setPreview(null); }
    finally { setLoading(false); }
  };
  return <div className="space-y-3 rounded-b-lg border border-t-0 border-border bg-surface p-4">
    <p className="text-sm text-text-secondary">{t("有效 SWF 文件不等于独立完整游戏")}</p>
    {!detail ? <Button variant="outline" disabled={busy || loading} onClick={() => void inspect()}>{t("展开目录审核")}</Button> : <>
      <label className="flex flex-wrap items-center gap-2 text-sm">
        {t("目录用途")}
        <select className="rounded border border-border bg-surface p-2" value={kind} disabled={busy || loading}
          onChange={event => { setKind(event.target.value as typeof kind); setEntries([]); setPreview(null); }}>
          <option value="">{t("请选择目录用途")}</option>
          <option value="project">{t("单个项目（主入口和资源）")}</option>
          <option value="collection">{t("独立游戏合集")}</option>
          <option value="resources">{t("资源目录（不入库）")}</option>
        </select>
      </label>
      {!detail.flash?.complete && <p role="alert">{t("目录读取不完整，请重新扫描")}</p>}
      {detail.flash?.includeDescendants === false && <p className="text-sm text-text-secondary">
        {t("本组仅审核直属入口，子目录会分别扫描和审核。")}
      </p>}
      {(kind === "project" || kind === "collection") && <fieldset className="space-y-2">
        <legend className="text-sm">{t(kind === "project" ? "选择主入口" : "选择独立入口")}</legend>
        {kind === "collection" && <Button variant="outline" disabled={busy || loading}
          onClick={() => { setEntries(detail.flash?.inventory ?? []); setPreview(null); }}>{t("选择全部入口")}</Button>}
        <div className="max-h-64 space-y-1 overflow-auto">
          {(detail.flash?.inventory ?? []).map(path => <label key={path} className="flex items-center gap-2 text-sm">
            <input type={kind === "project" ? "radio" : "checkbox"} name={`flash-${candidate.candidateId}`} checked={entries.includes(path)}
              disabled={busy || loading} onChange={() => {
                setEntries(previous => kind === "project" ? [path] : previous.includes(path) ? previous.filter(item => item !== path) : [...previous, path]);
                setPreview(null);
              }} />
            <span className="break-all">{path}</span>
          </label>)}
        </div>
      </fieldset>}
      <Button variant="outline" disabled={busy || loading || !valid || !detail.flash?.complete}
        onClick={() => void inspect(true)}>{t("预览调整清单")}</Button>
      {preview && <div className="space-y-2 text-sm">
        <p>{preview.adjustments.length ? t("将从库中移除 {0} 项，保留元数据和原文件", preview.adjustments.filter(item => item.proposedAction === "removeFromLibrary").length)
          : t("没有需要移除的现存记录")}</p>
        <ul className="max-h-48 overflow-auto">{preview.adjustments.map(item => <li key={item.gameId} className="break-all">
          {item.title} · {item.rootPath} · {t(item.proposedAction === "setEntry" ? "更新主入口" : "仅从库中移除")}
        </li>)}</ul>
        <Button disabled={busy || loading} onClick={() => void submit()}>{t("确认目录判断并应用")}</Button>
      </div>}
    </>}
    {error && <p role="alert" className="text-sm text-danger">{error}</p>}
  </div>;
}
