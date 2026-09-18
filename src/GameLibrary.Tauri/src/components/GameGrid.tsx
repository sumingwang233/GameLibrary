import type { GameItem } from "../lib/types";
import { GameCard } from "./GameCard";
import { EmptyState } from "./EmptyState";
import { SkeletonGrid } from "./SkeletonGrid";

export function GameGrid({ games, loading, layout, selectedId, onSelect, onPlay }: { games: GameItem[]; loading: boolean; layout: string; selectedId?: string; onSelect: (game: GameItem) => void; onPlay: (game: GameItem) => void }) {
  if (loading) return <SkeletonGrid />;
  if (!games.length) return <EmptyState symbol="◈" title="这里还没有游戏" message="添加游戏库目录并扫描，游戏会出现在这里。" />;
  if (layout === "compact") return <div className="space-y-2">{games.map(game => <button key={game.gameId} onClick={() => onSelect(game)} className="flex w-full items-center gap-4 rounded-md border border-border bg-surface px-4 py-3 text-left hover:border-steam"><span className="flex h-10 w-10 items-center justify-center rounded bg-steam-soft font-bold text-steam">{game.title.slice(0, 2).toUpperCase()}</span><span className="min-w-0 flex-1"><span className="block truncate font-semibold">{game.title}</span><span className="block truncate text-xs text-text-secondary">{game.rootPath}</span></span><span className="text-xs text-text-secondary">{game.engine ?? game.kind}</span></button>)}</div>;
  return <div className="grid grid-cols-[repeat(auto-fill,minmax(180px,1fr))] gap-4">{games.map(game => <GameCard key={game.gameId} game={game} selected={game.gameId === selectedId} onSelect={() => onSelect(game)} onPlay={() => onPlay(game)} />)}</div>;
}
