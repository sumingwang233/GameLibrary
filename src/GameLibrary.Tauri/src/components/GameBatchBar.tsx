import { useState } from "react";
import { Tag } from "lucide-react";
import { describeFailure, operation } from "../lib/api";
import { groupTagsByCategory, tagLabel } from "../lib/tags";
import type { GameItem, TagItem } from "../lib/types";
import { Button } from "./ui/button";
import { ConfirmDialog } from "./ui/confirm-dialog";

/**
 * 批量操作条。bug-3：「添加标签」不再依赖先在下拉里选好标签的前置状态（用户不理解
 * 按钮为何灰），改为按钮常可点（busy 除外）→ 点击弹出按分类分组的标签浮层 → 选择即
 * 应用。assign 调用链保持不变：逐游戏 games.get 取最新 revision 后 tags.assign。
 */
export function GameBatchBar({ games, tags, onComplete, onBusy }: {
  games: GameItem[];
  tags: TagItem[];
  onComplete: (failedIds: string[]) => Promise<void>;
  onBusy: (busy: boolean) => void;
}) {
  const [busy, setBusy] = useState(false);
  const [confirm, setConfirm] = useState(false);
  const [pickerOpen, setPickerOpen] = useState(false);
  const [result, setResult] = useState("");
  const execute = async (action: "favorite" | "unfavorite" | "remove" | "tag", tagId = "") => {
    if (busy || games.length === 0) return;
    if (action === "tag" && !tagId) return;
    setBusy(true);
    onBusy(true);
    setConfirm(false);
    const failures: string[] = [];
    const failedIds: string[] = [];
    try {
      for (const game of games) {
        try {
          const latest = await operation<GameItem>("games.get", { gameId: game.gameId });
          const operationId = action === "remove" ? "games.remove" : action === "tag" ? "tags.assign" : "games.update";
          await operation(operationId, {
            gameId: game.gameId,
            expectedRevision: latest.data.revision,
            ...(action === "tag" ? { tagId } : action === "remove" ? {} : { favorite: action === "favorite" }),
          }, `${operationId}:${game.gameId}:${latest.data.revision}:${action}:${tagId}`);
        } catch (error) {
          failedIds.push(game.gameId);
          failures.push(`${game.title}：${describeFailure(error)}`);
        }
      }
      setResult(`已完成 ${games.length - failures.length} 个${failures.length ? `，失败 ${failures.length} 个。${failures.slice(0, 3).join("；")}` : ""}`);
      await onComplete(failedIds);
    } catch (error) {
      setResult(`操作已处理，刷新失败：${describeFailure(error)}`);
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
    <div className="flex flex-wrap items-center gap-2" aria-label="游戏批量操作">
      <span className="text-sm">已选 {games.length} 个</span>
      <Button size="sm" variant="outline" disabled={busy || !games.length} onClick={() => void execute("favorite")}>批量收藏</Button>
      <Button size="sm" variant="outline" disabled={busy || !games.length} onClick={() => void execute("unfavorite")}>取消收藏</Button>
      <span className="relative">
        <Button size="sm" variant="outline" disabled={busy || !games.length} aria-haspopup="listbox" aria-expanded={pickerOpen} onClick={() => setPickerOpen(previous => !previous)}>
          <Tag size={14} />
          添加标签
        </Button>
        {pickerOpen && (
          <>
            <button type="button" aria-label="关闭标签选择" className="fixed inset-0 z-10 cursor-default" onClick={() => setPickerOpen(false)} />
            <div role="listbox" aria-label="选择标签" className="absolute top-full left-0 z-20 mt-1 max-h-72 w-72 overflow-y-auto rounded-md border border-border bg-panel p-3 shadow-2xl">
              {groups.length === 0 ? (
                <p className="px-1 py-2 text-sm text-text-secondary">库里还没有标签，先到「管理标签」新建。</p>
              ) : (
                groups.map((group) => (
                  <div key={group.value} className="mb-3 last:mb-0">
                    <p className="mb-1 px-1 text-[11px] font-semibold tracking-[0.14em] text-text-secondary uppercase">
                      {group.label} · {group.items.length}
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
                            {tag.color && (
                              <span aria-hidden="true" className="size-2 shrink-0 rounded-full" style={{ backgroundColor: tag.color }} />
                            )}
                            <span className="min-w-0 flex-1 truncate">{tagLabel(tag)}</span>
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
      <Button size="sm" variant="danger" disabled={busy || !games.length} onClick={() => setConfirm(true)}>批量移除</Button>
      {busy && <span role="status">正在处理…</span>}
    </div>
    {result && <p role="status" className="mt-2 break-words text-sm">{result}</p>}
    <ConfirmDialog open={confirm} title={`移除选中的 ${games.length} 个游戏`} description="只从游戏库移除，保留磁盘上的全部游戏文件。移除后不会在后续扫描中自动添加。" confirmLabel="移除游戏" destructive busy={busy} onCancel={() => setConfirm(false)} onConfirm={() => void execute("remove")} />
  </>;
}
