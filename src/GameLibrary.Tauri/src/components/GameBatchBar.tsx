import { useState } from "react";
import { describeFailure, operation } from "../lib/api";
import type { GameItem, TagItem } from "../lib/types";
import { Button } from "./ui/button";
import { ConfirmDialog } from "./ui/confirm-dialog";

export function GameBatchBar({ games, tags, onComplete, onBusy }: {
  games: GameItem[];
  tags: TagItem[];
  onComplete: (failedIds: string[]) => Promise<void>;
  onBusy: (busy: boolean) => void;
}) {
  const [busy, setBusy] = useState(false);
  const [confirm, setConfirm] = useState(false);
  const [tagId, setTagId] = useState("");
  const [result, setResult] = useState("");
  const execute = async (action: "favorite" | "unfavorite" | "remove" | "tag") => {
    if (busy || games.length === 0) return;
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
  return <>
    <div className="flex flex-wrap items-center gap-2" aria-label="游戏批量操作">
      <span className="text-sm">已选 {games.length} 个</span>
      <Button size="sm" variant="outline" disabled={busy || !games.length} onClick={() => void execute("favorite")}>批量收藏</Button>
      <Button size="sm" variant="outline" disabled={busy || !games.length} onClick={() => void execute("unfavorite")}>取消收藏</Button>
      <select aria-label="批量添加标签" className="rounded border border-border bg-surface p-2 text-sm" disabled={busy} value={tagId} onChange={event => setTagId(event.target.value)}>
        <option value="">选择标签</option>
        {tags.map(tag => <option key={tag.tagId} value={tag.tagId}>{tag.name}</option>)}
      </select>
      <Button size="sm" variant="outline" disabled={busy || !games.length || !tagId} onClick={() => void execute("tag")}>添加标签</Button>
      <Button size="sm" variant="danger" disabled={busy || !games.length} onClick={() => setConfirm(true)}>批量移除</Button>
      {busy && <span role="status">正在处理…</span>}
    </div>
    {result && <p role="status" className="mt-2 break-words text-sm">{result}</p>}
    <ConfirmDialog open={confirm} title={`移除选中的 ${games.length} 个游戏`} description="只从游戏库移除，保留磁盘上的全部游戏文件。移除后不会在后续扫描中自动添加。" confirmLabel="移除游戏" destructive busy={busy} onCancel={() => setConfirm(false)} onConfirm={() => void execute("remove")} />
  </>;
}
