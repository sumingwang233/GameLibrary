import {
  createContext,
  createElement,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
  type PropsWithChildren,
} from "react";
import { describeFailure, operation } from "./api";
import type {
  CandidateItem,
  GameItem,
  LaunchPlan,
  LibraryEvent,
  NotificationItem,
  RootItem,
  TagItem,
  ViewItem,
} from "./types";

export const PAGE_SIZE = 60;

/** 扫描进度轮询间隔。仅在确有活动作业时轮询，空闲时零请求。 */
const SCAN_POLL_MS = 700;

/**
 * 事件流轮询间隔。用于捕捉 CLI / MCP 在别处对库做的改动（用户确实会用 MCP 批量操作），
 * 以及后台周期核对产生的可用性变化——v1.1.5 完全没有接 events.read，只能靠手动刷新。
 */
const EVENT_POLL_MS = 2500;

const TERMINAL_JOB_STATES = ["succeeded", "failed", "cancelled"];

export interface GameFilters {
  search: string;
  sort: string;
  tagId: string;
  favoriteOnly: boolean;
  viewId: string;
}

export const emptyFilters: GameFilters = {
  search: "",
  sort: "accepted-desc",
  tagId: "",
  favoriteOnly: false,
  viewId: "",
};

interface ScanJob {
  jobId: string;
  root: string;
}

interface LibraryController {
  tags: TagItem[];
  roots: RootItem[];
  views: ViewItem[];
  notifications: NotificationItem[];
  candidates: CandidateItem[];
  gameTotal: number;
  candidateTotal: number;
  metaLoading: boolean;
  error: string | null;
  /** 每次收到库事件自增，作为查询侧的失效信号。 */
  changeToken: number;
  scanning: boolean;
  scanText: string | null;
  refreshMeta: () => Promise<void>;
  startScan: () => Promise<void>;
  cancelScan: () => Promise<void>;
  reviewCandidates: (items: CandidateItem[], action: "accept" | "defer" | "ignore") => Promise<void>;
  launch: (gameId: string) => Promise<void>;
  createTag: (name: string) => Promise<void>;
  renameTag: (tag: TagItem, name: string) => Promise<void>;
  removeTag: (tag: TagItem) => Promise<void>;
  addRoot: (path: string) => Promise<void>;
  removeRoot: (root: RootItem) => Promise<void>;
  createView: (name: string, filters: GameFilters) => Promise<void>;
  removeView: (view: ViewItem) => Promise<void>;
  acknowledgeNotification: (notificationId: string) => Promise<void>;
}

const LibraryContext = createContext<LibraryController | null>(null);

function useLibraryController(): LibraryController {
  const [tags, setTags] = useState<TagItem[]>([]);
  const [roots, setRoots] = useState<RootItem[]>([]);
  const [views, setViews] = useState<ViewItem[]>([]);
  const [notifications, setNotifications] = useState<NotificationItem[]>([]);
  const [candidates, setCandidates] = useState<CandidateItem[]>([]);
  const [gameTotal, setGameTotal] = useState(0);
  const [candidateTotal, setCandidateTotal] = useState(0);
  const [metaLoading, setMetaLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [changeToken, setChangeToken] = useState(0);
  const [jobs, setJobs] = useState<ScanJob[]>([]);
  const [scanText, setScanText] = useState<string | null>(null);

  const cursorRef = useRef<number | undefined>(undefined);
  const mounted = useRef(true);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  const bump = useCallback(() => {
    if (mounted.current) setChangeToken((token) => token + 1);
  }, []);

  const refreshMeta = useCallback(async () => {
    setMetaLoading(true);
    try {
      const status = await operation<{ libraryInitialized: boolean }>("host.status");
      if (!status.data.libraryInitialized) {
        await operation("library.init", {}, "library.init");
      }

      const [games, pending, tagList, rootList, viewList, notificationList] = await Promise.all([
        // 只为取 total：limit=1 让后端走 COUNT + 单行，v19 的部分索引使其成本恒定。
        operation<{ total: number }>("games.list", { limit: 1, offset: 0 }),
        operation<{ total: number; items: CandidateItem[] }>("candidates.list", {
          state: "pendingReview",
          limit: 1000,
          offset: 0,
        }),
        operation<{ items: TagItem[] }>("tags.list"),
        operation<{ items: RootItem[] }>("roots.list"),
        operation<{ items: ViewItem[] }>("views.list"),
        operation<{ items: NotificationItem[] }>("notifications.list", { state: "pending" }),
      ]);

      if (!mounted.current) return;
      setGameTotal(games.data.total);
      setCandidateTotal(pending.data.total);
      setCandidates(pending.data.items);
      setTags(tagList.data.items);
      setRoots(rootList.data.items);
      // views.list 会把内置视图（全部/收藏）一并返回，它们已由侧栏固定项承载，
      // 混进来会变成「可删除的收藏夹」并且 revision 为 null 导致 views.remove 失败。
      setViews(viewList.data.items.filter((view) => view.kind !== "builtin" && view.revision !== null));
      setNotifications(notificationList.data.items);
      setError(null);
    } catch (cause) {
      if (mounted.current) setError(describeFailure(cause));
    } finally {
      if (mounted.current) setMetaLoading(false);
    }
  }, []);

  useEffect(() => {
    void refreshMeta();
  }, [refreshMeta]);

  // 事件流：按 cursor 增量读取。游标过期（宿主重启或事件被淘汰）则清空游标重新全量读。
  useEffect(() => {
    const timer = window.setInterval(() => {
      void (async () => {
        try {
          const result = await operation<{ nextCursor: number; items: LibraryEvent[] }>(
            "events.read",
            { cursor: cursorRef.current, limit: 200 },
          );
          cursorRef.current = result.data.nextCursor;
          if (result.data.items.length > 0) bump();
        } catch (cause) {
          if (describeFailure(cause).includes("CursorExpired")) {
            cursorRef.current = undefined;
          }
        }
      })();
    }, EVENT_POLL_MS);
    return () => window.clearInterval(timer);
  }, [bump]);

  const startScan = useCallback(async () => {
    if (roots.length === 0) {
      throw new Error("请先添加游戏库目录");
    }

    // v1.1.5 只扫 roots[0]，多库根时其余静默不扫。这里逐个根启动并聚合进度。
    const started: ScanJob[] = [];
    const failures: string[] = [];
    for (const root of roots) {
      try {
        const result = await operation<{ jobId: string }>(
          "scan.start",
          { root: root.path },
          `scan.start:${root.rootId}`,
        );
        const jobId = result.jobId ?? result.data.jobId;
        if (jobId) started.push({ jobId, root: root.path });
      } catch (cause) {
        failures.push(`${root.path}: ${describeFailure(cause)}`);
      }
    }

    if (started.length === 0) {
      throw new Error(failures.join("；") || "扫描未能启动");
    }

    setJobs(started);
    setScanText(`已请求扫描 ${started.length} 个游戏库目录`);
    if (failures.length > 0) setError(failures.join("；"));
  }, [roots]);

  const cancelScan = useCallback(async () => {
    await Promise.all(
      jobs.map((job) =>
        operation("scan.cancel", { jobId: job.jobId }, `scan.cancel:${job.jobId}`).catch(
          () => undefined,
        ),
      ),
    );
    setScanText("正在取消扫描…");
  }, [jobs]);

  useEffect(() => {
    if (jobs.length === 0) return;
    const timer = window.setInterval(() => {
      void (async () => {
        const results = await Promise.all(
          jobs.map(async (job) => {
            try {
              const coverage = await operation<{
                state: string;
                coverage?: { scannedDirectories: number; candidatesFound: number };
              }>("scan.coverage", { jobId: job.jobId });
              return { job, ...coverage.data };
            } catch {
              return null;
            }
          }),
        );

        const alive = results.filter((item): item is NonNullable<typeof item> => item !== null);
        const running = alive.filter((item) => !TERMINAL_JOB_STATES.includes(item.state));

        if (!mounted.current) return;

        if (running.length === 0) {
          window.clearInterval(timer);
          setJobs([]);
          setScanText(null);
          await refreshMeta();
          bump();
          return;
        }

        const directories = running.reduce(
          (sum, item) => sum + (item.coverage?.scannedDirectories ?? 0),
          0,
        );
        const found = running.reduce((sum, item) => sum + (item.coverage?.candidatesFound ?? 0), 0);
        setScanText(
          `扫描中 · ${running.length} 个目录树 · ${directories.toLocaleString()} 个目录 · ` +
            `${found.toLocaleString()} 个识别候选（含已入库）`,
        );
      })();
    }, SCAN_POLL_MS);
    return () => window.clearInterval(timer);
  }, [jobs, refreshMeta, bump]);

  const reviewCandidates = useCallback(
    async (items: CandidateItem[], action: "accept" | "defer" | "ignore") => {
      const failures: string[] = [];
      for (const candidate of items) {
        try {
          await operation(
            `candidates.${action}`,
            { candidateId: candidate.candidateId, expectedRevision: candidate.revision },
            `candidates.${action}:${candidate.candidateId}:${candidate.revision}`,
          );
        } catch (cause) {
          failures.push(`${candidate.relativePath || candidate.physicalPath}: ${describeFailure(cause)}`);
        }
      }

      await refreshMeta();
      bump();
      if (failures.length > 0) {
        throw new Error(`${failures.length} 项失败：${failures.slice(0, 3).join("；")}`);
      }
    },
    [refreshMeta, bump],
  );

  const launch = useCallback(
    async (gameId: string) => {
      const profiles = await operation<{ items: Array<{ profileId: string; isDefault: boolean }> }>(
        "profiles.list",
        { gameId },
      );
      const profile =
        profiles.data.items.find((item) => item.isDefault) ?? profiles.data.items[0];
      if (!profile) {
        throw new Error("尚未配置启动方式，请先在详情页配置原始游戏程序");
      }

      // 先 plan 再 execute：plan 失败时后端会给出 TranslationRouteUnavailable 等错误码
      // 并附 nextActions，describeFailure 会把它拼成用户能看懂的下一步指引。
      const plan = await operation<LaunchPlan>("launch.plan", {
        gameId,
        profileId: profile.profileId,
      });

      await operation(
        "launch.execute",
        { planId: plan.data.planId, profileId: profile.profileId },
        `launch.execute:${gameId}:${profile.profileId}`,
      );
      bump();
    },
    [bump],
  );

  const createTag = useCallback(
    async (name: string) => {
      await operation("tags.create", { name }, `tags.create:${name}`);
      await refreshMeta();
    },
    [refreshMeta],
  );

  const renameTag = useCallback(
    async (tag: TagItem, name: string) => {
      await operation(
        "tags.update",
        { tagId: tag.tagId, name, expectedRevision: tag.revision },
        `tags.update:${tag.tagId}:${tag.revision}`,
      );
      await refreshMeta();
    },
    [refreshMeta],
  );

  const removeTag = useCallback(
    async (tag: TagItem) => {
      await operation(
        "tags.remove",
        { tagId: tag.tagId, expectedRevision: tag.revision },
        `tags.remove:${tag.tagId}:${tag.revision}`,
      );
      await refreshMeta();
      bump();
    },
    [refreshMeta, bump],
  );

  const addRoot = useCallback(
    async (path: string) => {
      // roots.add 在 operations.v1.json 中 requiresIdempotencyKey=true，
      // 但 OperationSchemas.InputSpecs 未登记该参数——以契约为准，必须传。
      await operation("roots.add", { root: path }, `roots.add:${path}`);
      await refreshMeta();
    },
    [refreshMeta],
  );

  const removeRoot = useCallback(
    async (root: RootItem) => {
      await operation(
        "roots.remove",
        { rootId: root.rootId, expectedRevision: root.revision },
        `roots.remove:${root.rootId}:${root.revision}`,
      );
      await refreshMeta();
    },
    [refreshMeta],
  );

  const createView = useCallback(
    async (name: string, filters: GameFilters) => {
      await operation(
        "views.create",
        {
          name,
          search: filters.search || undefined,
          favoriteOnly: filters.favoriteOnly || undefined,
          sort: filters.sort,
        },
        `views.create:${name}`,
      );
      await refreshMeta();
    },
    [refreshMeta],
  );

  const removeView = useCallback(
    async (view: ViewItem) => {
      // views.remove 在契约中 requiresRevision=true；内置视图的 revision 为 null，
      // 已在 refreshMeta 过滤掉，这里兜底为 0 以避免把 null 发给后端。
      await operation(
        "views.remove",
        { viewId: view.viewId, expectedRevision: view.revision ?? 0 },
        `views.remove:${view.viewId}:${view.revision ?? 0}`,
      );
      await refreshMeta();
    },
    [refreshMeta],
  );

  const acknowledgeNotification = useCallback(
    async (notificationId: string) => {
      await operation(
        "notifications.acknowledge",
        { notificationId },
        `notifications.acknowledge:${notificationId}`,
      );
      await refreshMeta();
    },
    [refreshMeta],
  );

  const scanning = jobs.length > 0;

  return useMemo(
    () => ({
      tags,
      roots,
      views,
      notifications,
      candidates,
      gameTotal,
      candidateTotal,
      metaLoading,
      error,
      changeToken,
      scanning,
      scanText,
      refreshMeta,
      startScan,
      cancelScan,
      reviewCandidates,
      launch,
      createTag,
      renameTag,
      removeTag,
      addRoot,
      removeRoot,
      createView,
      removeView,
      acknowledgeNotification,
    }),
    [
      tags,
      roots,
      views,
      notifications,
      candidates,
      gameTotal,
      candidateTotal,
      metaLoading,
      error,
      changeToken,
      scanning,
      scanText,
      refreshMeta,
      startScan,
      cancelScan,
      reviewCandidates,
      launch,
      createTag,
      renameTag,
      removeTag,
      addRoot,
      removeRoot,
      createView,
      removeView,
      acknowledgeNotification,
    ],
  );
}

export type LibraryHook = LibraryController;

export function LibraryProvider({ children }: PropsWithChildren) {
  return createElement(LibraryContext.Provider, { value: useLibraryController() }, children);
}

export function useLibrary(): LibraryController {
  const value = useContext(LibraryContext);
  if (!value) throw new Error("useLibrary 必须在 LibraryProvider 内使用");
  return value;
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

  const fetchPage = useCallback(
    async (offset: number, append: boolean) => {
      const result = await operation<{ total: number; items: GameItem[] }>("games.list", {
        ...request,
        limit: PAGE_SIZE,
        offset,
      });
      setTotal(result.data.total);
      setGames((previous) => (append ? [...previous, ...result.data.items] : result.data.items));
      return result.data.items.length;
    },
    [request],
  );

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);
    void (async () => {
      try {
        await fetchPage(0, false);
      } catch (cause) {
        if (!cancelled) setError(describeFailure(cause));
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();
    return () => {
      cancelled = true;
    };
    // suppressAutoRefresh 期间（扫描进行中）不自动重查，避免与扫描写入争抢后端串行锁。
  }, [fetchPage, changeToken, suppressAutoRefresh]);

  const loadMore = useCallback(() => {
    if (loadingMore || games.length >= total) return;
    setLoadingMore(true);
    void (async () => {
      try {
        await fetchPage(games.length, true);
      } catch (cause) {
        setError(describeFailure(cause));
      } finally {
        setLoadingMore(false);
      }
    })();
  }, [loadingMore, games.length, total, fetchPage]);

  const reload = useCallback(async () => {
    setLoading(true);
    try {
      await fetchPage(0, false);
      setError(null);
    } catch (cause) {
      setError(describeFailure(cause));
    } finally {
      setLoading(false);
    }
  }, [fetchPage]);

  return { games, total, loading, loadingMore, error, hasMore: games.length < total, loadMore, reload };
}
