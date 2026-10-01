import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { describeFailure, operation } from "../api";
import type { GameItem } from "../types";
import type { GameFilters } from "../state";
export const PAGE_SIZE = 60;

export interface GamesQuery {
  games: GameItem[];
  total: number;
  loading: boolean;
  loadingMore: boolean;
  error: string | null;
  hasMore: boolean;
  loadMore: () => void;
  reload: () => Promise<void>;
}

/**
 * 服务端查询。v1.1.5 固定 limit=500 并在客户端做搜索/排序/筛选，
 * 导致第 501 个游戏永久搜不到、侧栏计数与列表不一致，而后端 QueryGames
 * 早已支持 search/sort/tagId/favorite/viewId/limit/offset 全量下推。
 */
export function useGamesQuery(
  filters: GameFilters,
  changeToken: number,
  suppressAutoRefresh: boolean,
): GamesQuery {
  const [games, setGames] = useState<GameItem[]>([]);
  const [total, setTotal] = useState(0);
  const [loading, setLoading] = useState(true);
  const [loadingMore, setLoadingMore] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [debouncedSearch, setDebouncedSearch] = useState(filters.search);
  const hasLoadedRef = useRef(false);
  /** games/total 的镜像 ref：串行队列里的异步任务读它决定补拉范围，避免闭包读到旧 state。 */
  const gamesRef = useRef<GameItem[]>([]);
  const totalRef = useRef(0);
  /** 上一次生效的查询参数；引用未变却触发重查 ⇒ 后台失效刷新（bug-2：保留已加载页）。 */
  const lastRequestRef = useRef<Record<string, unknown> | null>(null);
  /** 串行队列：刷新与触底加载互斥，避免并发 games.list 交叉覆盖（补拉的页被 append 吞掉等）。 */
  const chainRef = useRef<Promise<unknown>>(Promise.resolve());

  useEffect(() => {
    const timer = window.setTimeout(() => setDebouncedSearch(filters.search), 250);
    return () => window.clearTimeout(timer);
  }, [filters.search]);

  const request = useMemo(
    () => ({
      search: debouncedSearch.trim() || undefined,
      sort: filters.sort,
      tagId: filters.tagId || undefined,
      favorite: filters.favoriteOnly || undefined,
      viewId: filters.viewId || undefined,
    }),
    [debouncedSearch, filters.sort, filters.tagId, filters.favoriteOnly, filters.viewId],
  );

  const enqueue = useCallback(<T,>(task: () => Promise<T>): Promise<T> => {
    // 前一任务无论成败都继续执行本任务；链尾吞掉错误由各调用方自行捕获。
    const next = chainRef.current.then(task, task);
    chainRef.current = next.then(
      () => undefined,
      () => undefined,
    );
    return next;
  }, []);

  const activeRequest = useRef(request);
  activeRequest.current = request;
  const mounted = useRef(true);
  useEffect(() => {
    mounted.current = true;
    return () => { mounted.current = false; };
  }, []);

  const applyResult = useCallback((items: GameItem[], totalNow: number) => {
    if (!mounted.current) return;
    gamesRef.current = items;
    totalRef.current = totalNow;
    setGames(items);
    setTotal(totalNow);
  }, []);

  const queryPage = useCallback(
    async (offset: number) => {
      const result = await operation<{ total: number; items: GameItem[] }>("games.list", {
        ...request,
        limit: PAGE_SIZE,
        offset,
      });
      return result.data;
    },
    [request],
  );

  /**
   * 刷新（bug-2）：preserveLoaded=true 时重新拉取第 0 页后，把此前已加载的后续页
   * 一并补拉回来，避免整组替换把已触底加载的页清掉——列表变短会让外层滚动容器
   * scrollTop 被钳制，表现为"点开始游戏/添加封面后页面自动跳顶"。筛选/排序变化
   * 走 preserveLoaded=false，重置回第 0 页（此时滚动重置是合理行为）。
   */
  const refresh = useCallback(
    async (preserveLoaded: boolean, isCurrent: () => boolean = () => true) => {
      if (!mounted.current || activeRequest.current !== request || !isCurrent()) return;
      const first = await queryPage(0);
      if (!mounted.current || activeRequest.current !== request || !isCurrent()) return;
      const previousCount = preserveLoaded ? gamesRef.current.length : 0;
      let items = first.items;
      // 偏移分页在数据插入/删除时可能跨页重复同一游戏（边界位移），按 gameId 去重。
      const seen = new Set(items.map((item) => item.gameId));
      while (items.length < previousCount && items.length < first.total) {
        const more = await queryPage(items.length);
        if (!mounted.current || activeRequest.current !== request || !isCurrent()) return;
        if (more.items.length === 0) break;
        const before = items.length;
        for (const item of more.items) {
          if (!seen.has(item.gameId)) {
            seen.add(item.gameId);
            items.push(item);
          }
        }
        // 本页全是重复项（刷新窗口内插入量 ≥ 页大小等极端场景）：继续拉同一偏移会死循环，放弃补齐。
        if (items.length === before) break;
      }
      applyResult(items, first.total);
    },
    [queryPage, applyResult, request],
  );

  useEffect(() => {
    if (suppressAutoRefresh) return;
    let cancelled = false;
    // request 引用变化 ⇒ 筛选/排序/搜索变化 ⇒ 重置分页；仅 changeToken 变化 ⇒ 保留已加载页。
    const isFilterChange = lastRequestRef.current !== request;
    lastRequestRef.current = request;
    if (!hasLoadedRef.current) setLoading(true);
    setError(null);
    void (async () => {
      try {
        // 经串行队列执行：与进行中的触底加载互斥，避免两者交叉覆盖 gamesRef。
        await enqueue(() => refresh(!isFilterChange && hasLoadedRef.current, () => !cancelled));
      } catch (cause) {
        if (!cancelled) setError(describeFailure(cause));
      } finally {
        if (!cancelled) {
          hasLoadedRef.current = true;
          setLoading(false);
        }
      }
    })();
    return () => {
      cancelled = true;
    };
    // 保留调用方主动抑制自动刷新接口；扫描本身不再阻止读取。
  }, [enqueue, refresh, changeToken, suppressAutoRefresh]);

  const loadMore = useCallback(() => {
    if (loadingMore || gamesRef.current.length >= totalRef.current) return;
    setLoadingMore(true);
    void enqueue(async () => {
      if (!mounted.current || activeRequest.current !== request) return;
      const page = await queryPage(gamesRef.current.length);
      if (!mounted.current || activeRequest.current !== request) return;
      const seen = new Set(gamesRef.current.map((item) => item.gameId));
      const merged = [...gamesRef.current];
      for (const item of page.items) {
        if (!seen.has(item.gameId)) {
          seen.add(item.gameId);
          merged.push(item);
        }
      }
      applyResult(merged, page.total);
    })
      .catch((cause) => { if (mounted.current && activeRequest.current === request) setError(describeFailure(cause)); })
      .finally(() => { if (mounted.current) setLoadingMore(false); });
  }, [loadingMore, enqueue, queryPage, applyResult, request]);

  const reload = useCallback(async () => {
    if (!hasLoadedRef.current) setLoading(true);
    try {
      // 手动刷新同样保留已加载页（详情页"添加封面"等 onChanged → reload 的路径与
      // 事件流 bump() 共用同一语义：后台刷新不得改变已加载条数与顺序）。
      await enqueue(() => refresh(true));
      if (mounted.current && activeRequest.current === request) setError(null);
    } catch (cause) {
      if (mounted.current && activeRequest.current === request) setError(describeFailure(cause));
    } finally {
      if (mounted.current && activeRequest.current === request) {
        hasLoadedRef.current = true;
        setLoading(false);
      }
    }
  }, [enqueue, refresh, request]);

  return { games, total, loading, loadingMore, error, hasMore: games.length < total, loadMore, reload };
}
