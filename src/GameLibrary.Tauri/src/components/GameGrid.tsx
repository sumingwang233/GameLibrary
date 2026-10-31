import { t } from "../lib/i18n";
import { useCallback, useEffect, useMemo, useRef, useState, type RefObject } from "react";
import { Clock3, FolderOpen } from "lucide-react";
import type { GameItem, TagItem } from "../lib/types";
import { formatPlaytime } from "../lib/utils";
import { GameBatchBar } from "./GameBatchBar";
import { EmptyState } from "./EmptyState";
import { GameCard } from "./GameCard";
import { SkeletonGrid } from "./SkeletonGrid";
import { Button } from "./ui/button";
import type { TitleTranslationController } from "../lib/hooks/useTitleTranslation";
import { useVirtualRows, virtualRowIndices } from "../lib/hooks/useVirtualRows";

const GRID_CLASS = "grid grid-cols-[repeat(auto-fill,minmax(var(--grid-min-card),1fr))] gap-4";

export function GameGrid({
  games,
  loading,
  loadingMore,
  layout,
  hasMore,
  selectedId,
  emptyHint,
  onLoadMore,
  onSelect,
  onPlay,
  tags,
  onChanged,
  titleTranslation,
  scrollRoot,
}: {
  games: GameItem[];
  loading: boolean;
  loadingMore: boolean;
  layout: "grid" | "compact";
  hasMore: boolean;
  selectedId?: string;
  emptyHint?: string;
  onLoadMore: () => void;
  onSelect: (gameId: string) => void;
  onPlay: (gameId: string) => void;
  tags: TagItem[];
  onChanged: () => Promise<void>;
  titleTranslation?: TitleTranslationController;
  scrollRoot?: RefObject<HTMLElement | null>;
}) {
  const sentinel = useRef<HTMLDivElement | null>(null);
  const [checked, setChecked] = useState<Set<string>>(new Set());
  const [actionBusy, setBusy] = useState(false);
  const [focusedId, setFocusedId] = useState<string | null>(null);
  const viewport = useVirtualRows(games.length, layout, !loading, scrollRoot);
  const focusedIndex = focusedId === null ? -1 : games.findIndex(game => game.gameId === focusedId);
  const rows = virtualRowIndices(viewport.first, viewport.last,
    focusedIndex < 0 ? -1 : Math.floor(focusedIndex / viewport.columns), viewport.total);
  const busy = actionBusy || !!titleTranslation?.busy;
  const chosen = useMemo(() => games.filter(game => checked.has(game.gameId)), [games, checked]);
  const toggle = useCallback((id: string) => setChecked(previous => {
    const next = new Set(previous);
    if (next.has(id)) next.delete(id); else next.add(id);
    return next;
  }), []);
  useEffect(() => {
    const available = new Set(games.map(game => game.gameId));
    setChecked(previous => [...previous].every(id => available.has(id))
      ? previous : new Set([...previous].filter(id => available.has(id))));
  }, [games]);

  // 触底自动加载下一页，取代 v1.1.5 的 limit=500 硬上限。
  useEffect(() => {
    const node = sentinel.current;
    if (!node || !hasMore) return;
    const observer = new IntersectionObserver(
      (entries) => {
        if (entries.some((entry) => entry.isIntersecting)) onLoadMore();
      },
      { rootMargin: "400px" },
    );
    observer.observe(node);
    return () => observer.disconnect();
  }, [hasMore, onLoadMore, games.length]);

  if (loading) return <SkeletonGrid />;
  if (games.length === 0) {
    return (
      <EmptyState
        icon={<FolderOpen size={44} strokeWidth={1.25} />}
        title={t("这里还没有游戏")}
        message={emptyHint ?? t("添加游戏库目录并扫描，游戏会出现在这里。")}
      />
    );
  }

  return (
    <div>
      <div className="mb-4 rounded-lg border border-border bg-surface p-3">
        <div className="mb-2 flex gap-2">
          <Button size="sm" variant="outline" disabled={busy} onClick={() => setChecked(new Set(games.map(game => game.gameId)))}>{t("全选")}</Button>
          <Button size="sm" variant="ghost" disabled={busy || !chosen.length} onClick={() => setChecked(new Set())}>{t("清空选择")}</Button>
        </div>
        <GameBatchBar games={chosen} tags={tags} titleTranslation={titleTranslation} onBusy={setBusy} onComplete={async failedIds => {
          setChecked(new Set(failedIds));
          await onChanged();
        }} />
      </div>
      <div ref={viewport.container} data-virtual-list
        onFocusCapture={event => setFocusedId((event.target as HTMLElement).closest<HTMLElement>("[data-game-id]")?.dataset.gameId ?? null)}
        onBlurCapture={event => {
          if (!(event.relatedTarget instanceof Node) || !event.currentTarget.contains(event.relatedTarget)) setFocusedId(null);
        }}
        style={{ overflowAnchor: "none" }}>
      {layout === "compact" ? (
        <ul className="relative" style={{ height: viewport.height }}>
          {rows.map(row => {
            const game = games[row];
            const playtime = formatPlaytime(game.playtimeMinutes) || t("{0} 分钟", 0);
            return <li key={game.gameId} data-virtual-item data-game-id={game.gameId}
              aria-posinset={row + 1} aria-setsize={hasMore ? -1 : games.length}
              className="absolute inset-x-0" style={{ top: row * viewport.stride }}>
              <div
                role="button"
                tabIndex={0}
                onKeyDown={event => { if (event.target === event.currentTarget && (event.key === "Enter" || event.key === " ")) { event.preventDefault(); onSelect(game.gameId); } }}
                onClick={() => onSelect(game.gameId)}
                aria-current={game.gameId === selectedId}
                className="flex w-full cursor-pointer items-center gap-4 rounded-md border border-border bg-surface px-4 py-3 text-left transition hover:border-steam focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
              >
                <input type="checkbox" aria-label={t("选择 {0}", game.title)} checked={checked.has(game.gameId)} disabled={busy}
                  className="size-5 accent-steam" onClick={event => event.stopPropagation()} onChange={() => toggle(game.gameId)} />
                <span
                  aria-hidden="true"
                  className="flex h-10 w-10 shrink-0 items-center justify-center rounded bg-steam-soft font-bold text-steam"
                >
                  {game.title.slice(0, 2).toUpperCase()}
                </span>
                <span className="min-w-0 flex-1">
                  <span className="block truncate font-semibold text-text-primary">
                    {game.title}
                  </span>
                  <span title={t("累计游玩 {0}", playtime)} className="mt-1 flex items-center gap-1 text-xs text-text-secondary">
                    <Clock3 size={12} aria-hidden="true" className="shrink-0" />
                    {playtime}
                  </span>
                  <span className="block truncate text-xs text-text-secondary">{game.rootPath}</span>
                </span>
                <Button
                  variant="outline"
                  size="sm"
                  onClick={(event) => {
                    event.stopPropagation();
                    onPlay(game.gameId);
                  }}
                  aria-label={t("启动 {0}", game.title)}
                >{t("启动")}</Button>
              </div>
            </li>;
          })}
        </ul>
      ) : (
        <div role="list" data-virtual-grid className={`${GRID_CLASS} relative`} style={{ height: viewport.height }}>
          {rows.flatMap(row => games.slice(row * viewport.columns, (row + 1) * viewport.columns).map((game, column) => (
            <div role="listitem" key={game.gameId} data-virtual-item data-game-id={game.gameId}
              aria-posinset={row * viewport.columns + column + 1} aria-setsize={hasMore ? -1 : games.length}
              className="absolute inset-x-0" style={{ top: row * viewport.stride, gridColumn: `${column + 1} / span 1` }}>
            <GameCard
              game={game}
              selected={game.gameId === selectedId}
              checked={checked.has(game.gameId)}
              selectionDisabled={busy}
              onToggle={toggle}
              onSelect={onSelect}
              onPlay={onPlay}
            />
            </div>
          )))}
        </div>
      )}
      </div>

      <div ref={sentinel} className="flex h-16 items-center justify-center">
        {loadingMore && <span className="text-xs text-text-secondary">{t("正在加载更多…")}</span>}
        {!hasMore && games.length > 0 && (
          <span className="text-xs text-text-secondary">{t("已显示全部")}{games.length.toLocaleString()}{t("个")}</span>
        )}
      </div>
    </div>
  );
}
