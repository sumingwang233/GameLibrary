import type { GameItem } from "./types";

/**
 * 详情面板的数据来源：优先当前列表里的同 id 对象（reload / patch 之后标题、收藏、封面
 * 立刻跟着更新），列表未命中时才退回打开瞬间的快照。列表外路径包括相似建议跳转、
 * launch.exited 弹出的退出提示、以及分页之外尚未加载的游戏。
 * 快照必须同 id 才生效，否则关闭再打开别的游戏会看到上一个游戏的残留。
 */
export function resolveSelectedGame(
  games: readonly GameItem[],
  selectedGameId: string | null,
  fallback: GameItem | null,
): GameItem | null {
  if (!selectedGameId) return null;
  return games.find(game => game.gameId === selectedGameId)
    ?? (fallback?.gameId === selectedGameId ? fallback : null);
}
