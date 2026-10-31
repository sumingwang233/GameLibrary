import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { describeFailure, operation, TransportError } from "../api";
import { t } from "../i18n";
import type { GameChange, GameItem } from "../types";
import type { GameFilters } from "../state";
export const PAGE_SIZE = 60;

export function changedGameIds(changes: readonly GameChange[] | undefined, after: number, through: number): string[] | null {
  if (!changes || after < 0 || through <= after) return null;
  const pending = changes.filter(change => change.token > after && change.token <= through);
  if (pending.length !== through - after) return null;
  const ids = new Set<string>();
  for (let index = 0; index < pending.length; index++) {
    const change = pending[index];
    if (change.token !== after + index + 1 || change.gameIds === null || !change.gameIds.length) return null;
    for (const id of change.gameIds) ids.add(id);
    if (ids.size > 16) return null;
  }
  return [...ids];
}

/** Mirror the actual SQL membership and order keys; unknown shapes must fall back to QueryGames. */
export function stableQueryItem(before: GameItem, after: GameItem, sort: string, played: boolean) {
  const key = sort === "title" || sort === "title-desc" ? "title"
    : sort === "accepted-desc" ? "acceptedUtc"
      : sort === "recent" || sort === "updated-desc" ? "updatedUtc" : null;
  return key !== null && before.gameId === after.gameId && after.revision >= before.revision
    && before.membership === "active" && after.membership === "active"
    && before.played === played && after.played === played
    && typeof before[key] === "string" && before[key] === after[key];
}

export interface GamesQuery {
  games: GameItem[];
  total: number;
  loading: boolean;
  loadingMore: boolean;
  error: string | null;
  hasMore: boolean;
  loadMore: () => void;
  reload: () => Promise<void>;
  patch: (item: Partial<GameItem> & Pick<GameItem, "gameId" | "revision">) => void;
}

/** games.list 原始响应：nextOffset/hasMore 是 v1.7.23 新增字段，旧 Host 不会返回。 */
type GamesPageResponse = {
  total: number;
  items: GameItem[];
  nextOffset?: number | null;
  hasMore?: boolean;
};

/** 归一后的一页：nextOffset 是「下一页第一行的原始行偏移」，null 表示已到底。 */
export interface GamesPage {
  items: GameItem[];
  total: number;
  nextOffset: number | null;
  hasMore: boolean;
  /** Client-only scope tag; never sent as a games.list parameter. */
  scope?: string | null;
}

function libraryScope(result: { libraryInstanceId?: string | null; dataEpoch?: string | null }) {
  return result.libraryInstanceId && result.dataEpoch ? JSON.stringify([result.libraryInstanceId, result.dataEpoch]) : null;
}

/**
 * 把一页响应归一成游标。
 *
 * 服务端按原始行分页，前端按 gameId 去重；用去重后的 games.length 当 offset 时，
 * 跨页重复（边界位移）会让同一段被反复请求或整页被跳过。因此一律以游标推进：
 * - 新契约有 nextOffset/hasMore 时以服务端为准；
 * - 旧响应用 offset + 本页原始 items.length 递进，hasMore 按 offset+行数<total 近似；
 * - 空页、游标不前进、hasMore=false 都是终止条件，避免死循环。
 */
function normalizePage(data: GamesPageResponse, offset: number): GamesPage {
  const items = Array.isArray(data.items) ? data.items : [];
  const total = Number.isFinite(data.total) ? data.total : items.length;
  const serverHasMore = typeof data.hasMore === "boolean" ? data.hasMore : null;
  const serverNext = typeof data.nextOffset === "number"
    && Number.isSafeInteger(data.nextOffset) && data.nextOffset > offset ? data.nextOffset : null;
  // 旧宿主没有游标字段：只能用原始行数递进（去重后的条数会小于真实行号）。
  const fallbackNext = offset + items.length < total ? offset + items.length : null;
  const nextOffset = serverHasMore === false ? null : (serverNext ?? fallbackNext);
  return { items, total, nextOffset, hasMore: nextOffset !== null };
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
  gameChanges?: readonly GameChange[],
): GamesQuery {
  const [games, setGames] = useState<GameItem[]>([]);
  const [total, setTotal] = useState(0);
  const [loading, setLoading] = useState(true);
  const [loadingMore, setLoadingMore] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [hasMore, setHasMore] = useState(false);
  const [debouncedSearch, setDebouncedSearch] = useState(filters.search);
  const hasLoadedRef = useRef(false);
  const appliedToken = useRef(-1);
  const dataScope = useRef<string | null>(null);
  /** games 的镜像 ref：串行队列里的异步任务读它决定保留多少条，避免闭包读到旧 state。 */
  const gamesRef = useRef<GameItem[]>([]);
  /** 下一页的原始行 offset；null＝已到底。触底加载只认它，不认去重后的 games.length。 */
  const cursorRef = useRef<number | null>(null);
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
      played: filters.playedOnly,
      hasCover: filters.hasCover ?? undefined,
      viewId: filters.viewId || undefined,
    }),
    [debouncedSearch, filters.sort, filters.tagId, filters.favoriteOnly, filters.playedOnly, filters.hasCover, filters.viewId],
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

  const applyResult = useCallback((items: GameItem[], page: GamesPage) => {
    if (!mounted.current) return;
    const scope = page.scope ?? null;
    const newer = new Map((dataScope.current === scope ? gamesRef.current : []).map(item => [item.gameId, item]));
    dataScope.current = scope;
    items = items.map(item => (newer.get(item.gameId)?.revision ?? 0) > item.revision ? newer.get(item.gameId)! : item);
    // 空页说明服务端已没有更多原始行：把游标收口，重复触底不会继续请求同一段。
    const nextOffset = page.items.length === 0 ? null : page.nextOffset;
    gamesRef.current = items;
    cursorRef.current = nextOffset;
    setGames(items);
    setTotal(page.total);
    setHasMore(nextOffset !== null);
  }, []);

  const resetResult = useCallback(() => {
    applyResult([], { items: [], total: 0, nextOffset: null, hasMore: false });
  }, [applyResult]);

  const queryPage = useCallback(
    async (offset: number) => {
      const result = await operation<GamesPageResponse>("games.list", {
        ...request,
        limit: PAGE_SIZE,
        offset,
      });
      return { ...normalizePage(result.data, offset), scope: libraryScope(result) };
    },
    [request],
  );

  /**
   * 刷新（bug-2）：preserveLoaded=true 时重新拉取第 0 页后，把此前已加载的后续页
   * 一并补拉回来，避免整组替换把已触底加载的页清掉——列表变短会让外层滚动容器
   * scrollTop 被钳制，表现为"点开始游戏/添加封面后页面自动跳顶"。筛选/排序变化
   * 走 preserveLoaded=false，重置回第 0 页（此时滚动重置是合理行为）。
   *
   * 补页沿服务端游标走，而不是沿「已经去重了多少条」走：跨页重复时后者会原地
   * 打转，而游标一定指向尚未取过的原始行。
   */
  const refresh = useCallback(
    async (preserveLoaded: boolean, isCurrent: () => boolean = () => true, changedIds: string[] | null = null) => {
      if (!mounted.current || activeRequest.current !== request || !isCurrent()) return;
      // A small, known batch costs fewer reads than replaying all loaded pages.
      const scope = dataScope.current;
      if (scope !== null && preserveLoaded && changedIds?.length && changedIds.length < Math.ceil(gamesRef.current.length / PAGE_SIZE)
        && !request.search && !request.tagId && !request.favorite && request.hasCover === undefined && !request.viewId) {
        const replacements = new Map<string, GameItem>();
        let safe = true;
        try {
          for (const id of changedIds) {
            const before = gamesRef.current.find(game => game.gameId === id);
            if (!before) { safe = false; break; }
            const result = await operation<GameItem>("games.get", { gameId: id });
            if (!mounted.current || activeRequest.current !== request || !isCurrent()) return;
            if (libraryScope(result) !== scope || !stableQueryItem(before, result.data, request.sort, request.played)) { safe = false; break; }
            replacements.set(id, result.data);
          }
        } catch { safe = false; }
        if (!mounted.current || activeRequest.current !== request || !isCurrent()) return;
        if (safe && [...replacements].every(([id, item]) => {
          const current = gamesRef.current.find(game => game.gameId === id);
          return current && stableQueryItem(current, item, request.sort, request.played);
        })) {
          gamesRef.current = gamesRef.current.map(game => replacements.get(game.gameId) ?? game);
          setGames(gamesRef.current);
          return;
        }
      }
      let page = await queryPage(0);
      if (!mounted.current || activeRequest.current !== request || !isCurrent()) return;
      const previousCount = preserveLoaded ? gamesRef.current.length : 0;
      // 上限只防异常游标（服务端一直返回同一页）导致的无限补页；正常情况由游标到底结束。
      const maxPages = Math.ceil(previousCount / PAGE_SIZE) + 1;
      let items = page.items;
      // 偏移分页在数据插入/删除时可能跨页重复同一游戏（边界位移），按 gameId 去重。
      const seen = new Set(items.map((item) => item.gameId));
      for (let fetched = 0; preserveLoaded && page.nextOffset !== null
        && items.length < previousCount && items.length < page.total && fetched < maxPages; fetched++) {
        const more = await queryPage(page.nextOffset);
        if (!mounted.current || activeRequest.current !== request || !isCurrent()) return;
        if (more.scope !== page.scope) throw new TransportError(t("库已切换，已丢弃旧响应"));
        // 空页＝服务端没有更多行：终止补页，并把游标收口。
        if (more.items.length === 0) { page = more; break; }
        for (const item of more.items) {
          if (!seen.has(item.gameId)) {
            seen.add(item.gameId);
            items.push(item);
          }
        }
        page = more;
      }
      applyResult(items, page);
    },
    [queryPage, applyResult, request],
  );

  useEffect(() => {
    if (suppressAutoRefresh) return;
    let cancelled = false;
    // request 引用变化 ⇒ 筛选/排序/搜索变化 ⇒ 重置分页；仅 changeToken 变化 ⇒ 保留已加载页。
    const isFilterChange = lastRequestRef.current !== request;
    const isLibraryChange = lastRequestRef.current?.played !== request.played;
    lastRequestRef.current = request;
    // 参数一变旧游标立即作废，避免刷新返回前用旧 offset 触底拉到上一组筛选的行。
    if (isFilterChange) cursorRef.current = null;
    if (isLibraryChange) resetResult();
    if (!hasLoadedRef.current || isLibraryChange) setLoading(true);
    setError(null);
    void (async () => {
      try {
        // 经串行队列执行：与进行中的触底加载互斥，避免两者交叉覆盖 gamesRef。
        await enqueue(() => refresh(!isFilterChange && hasLoadedRef.current, () => !cancelled,
          changedGameIds(gameChanges, appliedToken.current, changeToken)));
        if (!cancelled && mounted.current && activeRequest.current === request) appliedToken.current = changeToken;
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
  }, [enqueue, refresh, request, resetResult, changeToken, suppressAutoRefresh, gameChanges]);

  const loadMore = useCallback(() => {
    const offset = cursorRef.current;
    if (loadingMore || offset === null) return;
    setLoadingMore(true);
    void enqueue(async () => {
      if (!mounted.current || activeRequest.current !== request) return;
      const page = await queryPage(offset);
      if (!mounted.current || activeRequest.current !== request) return;
      if (page.scope !== dataScope.current) throw new TransportError(t("库已切换，已丢弃旧响应"));
      const seen = new Set(gamesRef.current.map((item) => item.gameId));
      const merged = [...gamesRef.current];
      for (const item of page.items) {
        if (!seen.has(item.gameId)) {
          seen.add(item.gameId);
          merged.push(item);
        }
      }
      applyResult(merged, page);
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

  const patch = useCallback((item: Partial<GameItem> & Pick<GameItem, "gameId" | "revision">) => {
    gamesRef.current = gamesRef.current.map(game => game.gameId === item.gameId && game.revision <= item.revision ? { ...game, ...item } : game);
    setGames(gamesRef.current);
  }, []);

  return { games, total, loading, loadingMore, error, hasMore, loadMore, reload, patch };
}
