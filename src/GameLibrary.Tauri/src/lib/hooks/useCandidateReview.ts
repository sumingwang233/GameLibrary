import { t } from "../i18n";
import { useCallback } from "react";
import { describeFailure, operation } from "../api";
import type { CandidateItem, CandidateReviewResult, Envelope, SimilarGameSuggestion } from "../types";

export function useCandidateReview(supports: (name: string) => boolean, refresh: () => Promise<void>, bump: () => void) {
  return useCallback(async (items: CandidateItem[], action: "accept" | "defer" | "ignore") => {
    const failures: string[] = [];
    const accepted: Array<{ candidate: CandidateItem; similarTo: SimilarGameSuggestion[] }> = [];
    if (supports("candidates.review_batch")) {
      const input = items.map(candidate => ({ candidateId: candidate.candidateId, expectedRevision: candidate.revision }));
      const result = await operation<{ items: Array<{ candidateId: string; result: Envelope<CandidateReviewResult> }> }>(
        "candidates.review_batch", { action, items: input }, `candidates.review_batch:${action}:${JSON.stringify(input)}`);
      for (let i = 0; i < items.length; i++) {
        const outcome = result.data.items[i]?.result;
        if (!outcome?.ok) failures.push(`${items[i].relativePath}: ${outcome?.error?.message ?? t("审核结果缺失")}`);
        else if (action === "accept") accepted.push({ candidate: items[i], similarTo: outcome.data.similarTo ?? [] });
      }
    } else {
      for (const candidate of items) {
        try {
          const result = await operation<CandidateReviewResult>(`candidates.${action}`,
            { candidateId: candidate.candidateId, expectedRevision: candidate.revision },
            `candidates.${action}:${candidate.candidateId}:${candidate.revision}`);
          if (action === "accept") accepted.push({ candidate, similarTo: result.data.similarTo ?? [] });
        } catch (cause) { failures.push(`${candidate.relativePath || candidate.physicalPath}: ${describeFailure(cause)}`); }
      }
    }
    await refresh();
    if (action === "accept") bump();
    if (failures.length) throw new Error(t("{0} 项失败：{1}", failures.length, failures.slice(0, 3).join("；")));
    return accepted;
  }, [supports, refresh, bump]);
}
