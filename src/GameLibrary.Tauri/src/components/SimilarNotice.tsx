import { Copy, X } from "lucide-react";
import type { SimilarGameSuggestion } from "../lib/types";
import { formatSimilarity } from "../lib/utils";
import { Badge } from "./ui/badge";
import { Button } from "./ui/button";

/** 一批 accept 中某个新游戏的相似建议组（entries 需已按相似度降序）。 */
export interface SimilarNoticeGroup {
  /** 新加入游戏的候选目录名（candidate.relativePath || physicalPath）。 */
  sourceTitle: string;
  entries: SimilarGameSuggestion[];
}

/** 一批 accept 的聚合提示数据；groups 为空时横幅整体不渲染。 */
export interface SimilarNoticeData {
  /** 本次加入的游戏数（全部 accept 成功的候选数）。 */
  addedCount: number;
  groups: SimilarNoticeGroup[];
}

/**
 * 候选确认后的相似副本提示横幅。单例不堆叠（新批覆盖旧批由 App 侧 state 保证），
 * role="status" 信息级提示不与错误条（role="alert"）抢注意力。
 * 旧后端（响应无 similarTo）与无命中（空数组）均聚合为 groups=[] → 整体不渲染。
 */
export function SimilarNotice({
  notice,
  onDismiss,
  onNavigate,
}: {
  notice: SimilarNoticeData | null;
  onDismiss: () => void;
  onNavigate: (gameId: string) => void;
}) {
  if (!notice || notice.groups.length === 0) return null;

  // 单候选：正文一句直出最高相似度；同游戏多余条目折叠进 details。
  const single = notice.addedCount === 1 && notice.groups.length === 1;
  const singleGroup = single ? notice.groups[0] : null;
  const headline = singleGroup
    ? `《${singleGroup.sourceTitle}》已加入游戏库，疑似与《${singleGroup.entries[0].title}》重复（相似度 ${formatSimilarity(singleGroup.entries[0].similarity)}）`
    : `本次加入 ${notice.addedCount} 个游戏，${notice.groups.length} 个疑似与库中已有游戏重复`;

  // details 明细：单候选只放「其余」条目（行首那条已在正文）；批量放全部 M 组。
  const detailGroups = singleGroup
    ? singleGroup.entries.length > 1
      ? [{ sourceTitle: singleGroup.sourceTitle, entries: singleGroup.entries.slice(1) }]
      : []
    : notice.groups;

  return (
    <div
      role="status"
      className="mb-4 rounded-lg border border-steam/40 bg-steam-soft/40 px-4 py-3 text-sm"
    >
      <div className="flex items-center gap-3">
        <Copy size={16} aria-hidden="true" className="shrink-0 text-steam" />
        <span className="min-w-0 flex-1 text-text-primary">{headline}</span>
        <Button
          size="sm"
          variant="ghost"
          aria-label="关闭相似提示"
          onClick={onDismiss}
          className="shrink-0"
        >
          <X size={14} aria-hidden="true" />
        </Button>
      </div>

      {detailGroups.length > 0 && (
        <details className="mt-2 pl-7">
          <summary className="cursor-pointer text-xs text-text-secondary select-none">
            {singleGroup
              ? `查看其余 ${singleGroup.entries.length - 1} 个相似游戏`
              : `查看 ${notice.groups.length} 组相似详情`}
          </summary>
          <div className="mt-2 space-y-3">
            {detailGroups.map((group, index) => (
              <div key={`${group.sourceTitle}:${index}`}>
                <p className="text-xs text-text-secondary">《{group.sourceTitle}》疑似与：</p>
                <div className="mt-1 space-y-1">
                  <SuggestionButton item={group.entries[0]} onNavigate={onNavigate} />
                  {group.entries.length > 1 && (
                    <ul className="ml-4 space-y-1 border-l border-border pl-3">
                      {group.entries.slice(1).map((item) => (
                        <li key={item.gameId}>
                          <SuggestionButton item={item} onNavigate={onNavigate} />
                        </li>
                      ))}
                    </ul>
                  )}
                </div>
              </div>
            ))}
          </div>
        </details>
      )}
    </div>
  );
}

/** 明细条目：原生 button，aria-label 朗读完整句子；可见文本含「相似度 N%」，
 *  Badge 再以 aria-hidden 隐藏，避免裸数字被读屏单独朗读。 */
function SuggestionButton({
  item,
  onNavigate,
}: {
  item: SimilarGameSuggestion;
  onNavigate: (gameId: string) => void;
}) {
  const percent = formatSimilarity(item.similarity);
  return (
    <button
      type="button"
      aria-label={`查看《${item.title}》详情，相似度 ${percent}`}
      onClick={() => onNavigate(item.gameId)}
      className="flex w-full cursor-pointer items-center justify-between gap-2 rounded-md border border-border bg-surface px-3 py-1.5 text-left text-sm transition hover:border-steam focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
    >
      <span className="min-w-0 flex-1 truncate text-text-primary">《{item.title}》</span>
      <Badge variant="steam" aria-hidden="true" className="shrink-0">
        相似度 {percent}
      </Badge>
    </button>
  );
}
