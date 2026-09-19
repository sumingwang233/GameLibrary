import { useEffect, useRef } from "react";
import { FolderOpen } from "lucide-react";
import type { GameItem } from "../lib/types";
import { EmptyState } from "./EmptyState";
import { GameCard } from "./GameCard";
import { SkeletonGrid } from "./SkeletonGrid";
import { Button } from "./ui/button";

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
}) {
  const sentinel = useRef<HTMLDivElement | null>(null);

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
        title="这里还没有游戏"
        message={emptyHint ?? "添加游戏库目录并扫描，游戏会出现在这里。"}
      />
    );
  }

  return (
    <div>
      {layout === "compact" ? (
        <ul className="space-y-2">
          {games.map((game) => (
            <li key={game.gameId}>
              <button
                type="button"
                onClick={() => onSelect(game.gameId)}
                aria-current={game.gameId === selectedId}
                className="flex w-full cursor-pointer items-center gap-4 rounded-md border border-border bg-surface px-4 py-3 text-left transition hover:border-steam focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
              >
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
                  <span className="block truncate text-xs text-text-secondary">{game.rootPath}</span>
                </span>
                <span className="shrink-0 text-xs text-text-secondary">
                  {game.engine ?? game.kind}
                </span>
                <Button
                  variant="outline"
                  size="sm"
                  onClick={(event) => {
                    event.stopPropagation();
                    onPlay(game.gameId);
                  }}
                  aria-label={`启动 ${game.title}`}
                >
                  启动
                </Button>
              </button>
            </li>
          ))}
        </ul>
      ) : (
        <div className={GRID_CLASS}>
          {games.map((game) => (
            <GameCard
              key={game.gameId}
              game={game}
              selected={game.gameId === selectedId}
              onSelect={onSelect}
              onPlay={onPlay}
            />
          ))}
        </div>
      )}

      <div ref={sentinel} className="flex h-16 items-center justify-center">
        {loadingMore && <span className="text-xs text-text-secondary">正在加载更多…</span>}
        {!hasMore && games.length > 0 && (
          <span className="text-xs text-text-secondary">已显示全部 {games.length.toLocaleString()} 个</span>
        )}
      </div>
    </div>
  );
}
