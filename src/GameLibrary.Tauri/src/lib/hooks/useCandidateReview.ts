import { t } from "../i18n";
import { useCallback } from "react";
import { describeFailure, operation } from "../api";
import type { CandidateItem, CandidateReviewResult, Envelope, SimilarGameSuggestion } from "../types";

/**
 * 一次批量审核的结果。部分失败不再整批抛出：调用方（ReviewList）只清除
 * succeededIds 命中的勾选，失败项保持选中以便重试；App 仍用 failureMessage
 * 在 actionError 里呈现「{0} 项失败：{1}」，accepted 用于聚合相似副本提示。
 */
export interface ReviewOutcome {
  succeededIds: string[];
  accepted: Array<{ candidate: CandidateItem; similarTo: SimilarGameSuggestion[] }>;
  failureMessage: string | null;
}

export function useCandidateReview(supports: (name: string) => boolean, refresh: () => Promise<void>, bump: () => void) {
  return useCallback(async (items: CandidateItem[], action: "accept" | "defer" | "ignore"): Promise<ReviewOutcome> => {
    const failures: string[] = [];
    const succeededIds: string[] = [];
    const accepted: ReviewOutcome["accepted"] = [];
    if (supports("candidates.review_batch")) {
      const input = items.map(candidate => ({ candidateId: candidate.candidateId, expectedRevision: candidate.revision,
        ...(action === "accept" && candidate.flashReview ? { flashKind: candidate.flashReview.kind,
          entryPaths: candidate.flashReview.entryPaths, adjustments: candidate.flashReview.adjustments } : {}) }));
      const result = await operation<{ items: Array<{ candidateId: string; result: Envelope<CandidateReviewResult> }> }>(
        "candidates.review_batch", { action, items: input }, `candidates.review_batch:${action}:${JSON.stringify(input)}`);
      // 按 candidateId 对齐而不是下标：后端若重排/丢弃条目，下标对齐会把 A 的成功记到 B 头上，
      // 导致 ReviewList 清错勾选。缺失条目按失败处理。
      const outcomes = new Map<string, Envelope<CandidateReviewResult>>();
      for (const entry of result.data.items ?? []) if (entry?.candidateId) outcomes.set(entry.candidateId, entry.result);
      for (const candidate of items) {
        const outcome = outcomes.get(candidate.candidateId);
        if (!outcome?.ok) failures.push(`${candidate.relativePath}: ${outcome?.error?.message ?? t("审核结果缺失")}`);
        else {
          succeededIds.push(candidate.candidateId);
          if (action === "accept") accepted.push({ candidate, similarTo: outcome.data.similarTo ?? [] });
        }
      }
    } else {
      for (const candidate of items) {
        try {
          const result = await operation<CandidateReviewResult>(`candidates.${action}`,
            { candidateId: candidate.candidateId, expectedRevision: candidate.revision,
              ...(action === "accept" && candidate.flashReview ? { flashKind: candidate.flashReview.kind,
                entryPaths: candidate.flashReview.entryPaths, adjustments: candidate.flashReview.adjustments } : {}) },
            `candidates.${action}:${candidate.candidateId}:${candidate.revision}:${JSON.stringify(candidate.flashReview ?? null)}`);
          succeededIds.push(candidate.candidateId);
          if (action === "accept") accepted.push({ candidate, similarTo: result.data.similarTo ?? [] });
        } catch (cause) { failures.push(`${candidate.relativePath || candidate.physicalPath}: ${describeFailure(cause)}`); }
      }
    }
    await refresh();
    if (action === "accept") bump();
    return {
      succeededIds,
      accepted,
      failureMessage: failures.length ? t("{0} 项失败：{1}", failures.length, failures.slice(0, 3).join("；")) : null,
    };
  }, [supports, refresh, bump]);
}
