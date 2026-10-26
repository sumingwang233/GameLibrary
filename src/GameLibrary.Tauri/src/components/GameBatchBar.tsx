import { t } from "../lib/i18n";
import { useState } from "react";
import { Tag } from "lucide-react";
import { describeFailure, operation } from "../lib/api";
import { groupTagsByCategory, tagLabel, tagStyle } from "../lib/tags";
import type { GameItem, TagItem } from "../lib/types";
import { Button } from "./ui/button";
import { ConfirmDialog } from "./ui/confirm-dialog";
import type { TitleTranslationController } from "../lib/hooks/useTitleTranslation";

/** 批量回执单条（冻结契约：逐项失败是 ok:false + errorCode，不会抛 Envelope 级错误）。 */
interface BatchItemResult {
  gameId: string;
  ok: boolean;
  revision?: number;
  errorCode?: string;
  message?: string;
}

/** games.update_batch / tags.assign_batch / games.remove_batch 共用的 Data 形状。 */
interface BatchResultData {
  items: BatchItemResult[];
  updated?: number;
  failed?: number;
}

/** 契约单批上限 1000；取 500 让单次 payload 体积可控（1024 项 → 3 次 IPC）。 */
const BATCH_CHUNK_SIZE = 500;

function chunk<T>(values: T[], size: number): T[][] {
  const batches: T[][] = [];
  for (let index = 0; index < values.length; index += size) batches.push(values.slice(index, index + size));
  return batches;
}

/** 逐项回执只有字符串 errorCode/message；describeFailure 接受异常对象，这里单独映射可读文案。 */
function batchFailureText(item: BatchItemResult): string {
  if (item.message) return item.message;
  if (item.errorCode === "NotFound") return t("游戏不存在或已移除");
  // 其余错误码（如 Conflict）保留代码，便于日志与后端契约对账；文案复用已有键，不新增未翻译条目。
  return item.errorCode ? `${t("操作失败")} (${item.errorCode})` : t("操作失败");
}

/**
 * 批量操作条。bug-3：「添加标签」不再依赖先在下拉里选好标签的前置状态（用户不理解
 * 按钮为何灰），改为按钮常可点（busy 除外）→ 点击弹出按分类分组的标签浮层 → 选择即
 * 应用。
 *
 * optimize-v1.7.23：提交路径从「逐游戏 games.get + 单项操作（2N 次 IPC）」改为每类操作
 * 一次批量 IPC（games.update_batch / tags.assign_batch / games.remove_batch）。
 * expectedRevision 直接取列表项已有的 revision，不再回查；单批按契约上限 1000 之内的
 * 500 条分块顺序提交，逐项回执（ok/errorCode）合并成失败列表。
 */
export function GameBatchBar({ games, tags, onComplete, onBusy, titleTranslation }: {
  games: GameItem[];
  tags: TagItem[];
  onComplete: (failedIds: string[]) => Promise<void>;
  onBusy: (busy: boolean) => void;
  titleTranslation?: TitleTranslationController;
}) {
  const [actionBusy, setBusy] = useState(false);
  const busy = actionBusy || !!titleTranslation?.busy;
  const [confirm, setConfirm] = useState(false);
  const [pickerOpen, setPickerOpen] = useState(false);
  const [result, setResult] = useState("");
  const execute = async (action: "favorite" | "unfavorite" | "remove" | "tag", tagId = "") => {
    if (busy) return;
    if (games.length === 0) {
      setResult(t("请先勾选游戏"));
      return;
    }
    if (action === "tag" && !tagId) return;
    setBusy(true);
    onBusy(true);
    setConfirm(false);
    const titles = new Map(games.map((game) => [game.gameId, game.title]));
    const failures: string[] = [];
    const failedIds: string[] = [];
    let okCount = 0;
    const collect = (item: BatchItemResult) => {
      if (item.ok) {
        okCount++;
        return;
      }
      failedIds.push(item.gameId);
      failures.push(`${titles.get(item.gameId) ?? item.gameId}：${batchFailureText(item)}`);
    };
    try {
      for (const batch of chunk(games, BATCH_CHUNK_SIZE)) {
        const items = batch.map((game) => ({ gameId: game.gameId, expectedRevision: game.revision }));
        try {
          const response = action === "favorite" || action === "unfavorite"
            ? await operation<BatchResultData>("games.update_batch", {
              items: items.map((item) => ({ ...item, favorite: action === "favorite" })),
            }, `games.update_batch:${action}`)
            : action === "tag"
              ? await operation<BatchResultData>("tags.assign_batch", { tagId, items }, `tags.assign_batch:${tagId}`)
              : await operation<BatchResultData>("games.remove_batch", { items }, "games.remove_batch");
          if (!Array.isArray(response.data?.items)) throw new Error(t("操作失败"));
          response.data.items.forEach(collect);
        } catch (error) {
          // Envelope 级错误只可能来自整批参数非法或传输失败：本批全记失败，其余批继续提交。
          const reason = describeFailure(error);
          for (const game of batch) {
            failedIds.push(game.gameId);
            failures.push(`${game.title}：${reason}`);
          }
        }
      }
      setResult(t("已完成 {0} 个{1}", okCount, failures.length ? `，失败 ${failures.length} 个。${failures.slice(0, 3).join("；")}` : ""));
      await onComplete(failedIds);
    } catch (error) {
      setResult(t("操作已处理，刷新失败：{0}", describeFailure(error)));
    } finally {
      setBusy(false);
      onBusy(false);
    }
  };
  const applyTag = (tagId: string) => {
    setPickerOpen(false);
    void execute("tag", tagId);
  };
  const groups = groupTagsByCategory(tags).filter((group) => group.items.length > 0);
  return <>
    <div className="flex flex-wrap items-center gap-2" aria-label={t("游戏批量操作")}>
      <span className="text-sm">{t("已选")}{games.length}{t("个")}</span>
      {titleTranslation && <Button size="sm" variant="outline" disabled={busy || !games.length} onClick={() => void titleTranslation.start(games.map(game => game.gameId))}>{t("翻译选中名称")}</Button>}
      <Button size="sm" variant="outline" disabled={busy || !games.length} onClick={() => void execute("favorite")}>{t("批量收藏")}</Button>
      <Button size="sm" variant="outline" disabled={busy || !games.length} onClick={() => void execute("unfavorite")}>{t("取消收藏")}</Button>
      <span className="relative">
        <Button size="sm" variant="outline" disabled={busy} aria-haspopup="listbox" aria-expanded={pickerOpen} onClick={() => setPickerOpen(previous => !previous)}>
          <Tag size={14} />{t("添加标签")}</Button>
        {pickerOpen && (
          <>
            <button type="button" aria-label={t("关闭标签选择")} className="fixed inset-0 z-10 cursor-default" onClick={() => setPickerOpen(false)} />
            <div role="listbox" aria-label={t("选择标签")} className="absolute top-full left-0 z-20 mt-1 max-h-72 w-72 overflow-y-auto rounded-md border border-border bg-panel p-3 shadow-2xl">
              {groups.length === 0 ? (
                <p className="px-1 py-2 text-sm text-text-secondary">{t("库里还没有标签，先到「管理标签」新建。")}</p>
              ) : (
                groups.map((group) => (
                  <div key={group.value} className="mb-3 last:mb-0">
                    <p className="mb-1 px-1 text-[11px] font-semibold tracking-[0.14em] text-text-secondary uppercase">
                      {t(group.label)} · {group.items.length}
                    </p>
                    <ul>
                      {group.items.map((tag) => (
                        <li key={tag.tagId}>
                          <button
                            type="button"
                            role="option"
                            aria-selected={false}
                            className="flex w-full cursor-pointer items-center gap-2 rounded px-2 py-1.5 text-left text-sm text-text-primary transition hover:bg-surface focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
                            onClick={() => applyTag(tag.tagId)}
                          >
                            <span className="flex min-w-0 flex-1 items-center gap-2 rounded px-1" style={tagStyle(tag)}>
                              <span aria-hidden="true" className="size-2 shrink-0 rounded-full bg-current" />
                              <span className="truncate">{tagLabel(tag)}</span>
                            </span>
                            {tag.gameCount ? <span className="shrink-0 text-xs text-text-secondary">{tag.gameCount.toLocaleString()}</span> : null}
                          </button>
                        </li>
                      ))}
                    </ul>
                  </div>
                ))
              )}
            </div>
          </>
        )}
      </span>
      <Button size="sm" variant="danger" disabled={busy || !games.length} onClick={() => setConfirm(true)}>{t("批量移除")}</Button>
      {busy && <span role="status">{t("正在处理…")}</span>}
    </div>
    {result && <p role="status" className="mt-2 break-words text-sm">{result}</p>}
    <ConfirmDialog open={confirm} title={t("移除选中的 {0} 个游戏", games.length)} description={t("只从游戏库移除，保留磁盘上的全部游戏文件。移除后不会在后续扫描中自动添加。")} confirmLabel={t("移除游戏")} destructive busy={busy} onCancel={() => setConfirm(false)} onConfirm={() => void execute("remove")} />
  </>;
}
