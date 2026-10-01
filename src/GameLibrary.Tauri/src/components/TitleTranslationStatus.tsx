import { t } from "../lib/i18n";
import { retryTitleIds, type TitleTranslationController } from "../lib/hooks/useTitleTranslation";
import { Button } from "./ui/button";

const reasons: Record<string, string> = {
  rateLimited: "翻译服务限流", serviceError: "翻译服务暂不可用", networkError: "翻译服务连接失败",
  timeout: "翻译请求超时", invalidResponse: "翻译服务返回无效结果", enginesUnavailable: "两个翻译引擎暂不可用",
  revisionConflict: "游戏已发生变更，请重试", alreadyTranslated: "已有译名，已跳过", unchanged: "译名未变化",
  invalidTitle: "名称为空或超过 1000 字符", inactive: "游戏已移除，已跳过",
};
export function TitleTranslationStatus({ controller }: { controller: TitleTranslationController }) {
  const { progress, busy, error, state } = controller;
  if (!progress && !busy && !error) return null;
  const summaries = [...new Set([...(progress?.items.map(item => item.reason) ?? []), progress?.stopReason].filter((reason): reason is string => !!reason))];
  return <div className="my-3 rounded-md border border-border bg-surface p-3 text-sm" role="status">
    <p>{progress ? t("名称翻译：{0}/{1}，成功 {2}，失败 {3}，跳过 {4}，未处理 {5}", progress.completed, progress.total, progress.succeeded, progress.failed, progress.skipped, progress.pending) : t("正在翻译名称…")}</p>
    {state === "cancelled" && <p>{t("翻译已取消，已成功的译名已保留")}</p>}
    {summaries.map(reason => <p key={reason} className="mt-1 text-text-secondary">{t(reasons[reason] ?? "翻译服务暂不可用")}</p>)}
    {error && <p role="alert">{error}</p>}
    <div className="mt-2 flex gap-2">
      {busy && <Button size="sm" variant="outline" disabled={state === "cancelRequested"} onClick={() => void controller.cancel()}>{t("取消翻译")}</Button>}
      {!busy && progress && retryTitleIds(progress).length > 0 && <Button size="sm" variant="outline" onClick={() => void controller.retry()}>{t("重试失败和未处理项")}</Button>}
    </div>
  </div>;
}
