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
import { describeFailure, operation, showMainWindow } from "./api";
import type {
  CandidateItem,
  CandidateReviewResult,
  GameItem,
  LaunchPlan,
  LibraryEvent,
  NotificationItem,
  RootItem,
  SimilarGameSuggestion,
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

/** 单次增量读取上限；扫描候选风暴由事件类型过滤，不触发游戏列表刷新。 */
const EVENT_READ_LIMIT = 4096;

function eventAffectsVisibleLibrary(event: LibraryEvent) {
  return (
    event.type.startsWith("game.") ||
    event.type.startsWith("tag.") ||
    event.type.startsWith("view.") ||
    event.type.startsWith("root.") ||
    event.type.startsWith("settings.") ||
    event.type.startsWith("notification.") ||
    // feat-2：游戏进程退出 → 累计时长/最近游玩变化，游戏列表需要失效重查。
    event.type === "launch.exited" ||
    event.type === "scan.completed" ||
    event.type === "scan.failed"
  );
}

/** launch.exited 事件负载（HostRuntime.WireAttemptPersistence：attemptId/gameId/exitCode/durationSeconds）。 */
function parseLaunchExit(event: LibraryEvent): { attemptId: string; gameId: string } | null {
  if (event.type !== "launch.exited") return null;
  const payload = event.payload as { attemptId?: unknown; gameId?: unknown } | null | undefined;
  if (typeof payload?.gameId !== "string") return null;
  const attemptId = typeof payload.attemptId === "string" ? payload.attemptId : `seq:${event.sequence}`;
  return { attemptId, gameId: payload.gameId };
}

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

/**
 * sort 白名单（bug-1）：games.list / views.create / views.update 三处完全一致
 * （GamesHandler.cs:118、ViewSettingsHandler.cs:368-369 六值同源）。
 * LibraryToolbar SORT_OPTIONS 的四个值（accepted-desc/updated-desc/title-asc/title-desc）
 * 逐一比对均在白名单内；createView 对历史/未知名兜底映射到 "title"。
 */
export const SORT_WHITELIST: readonly string[] = [
  "title",
  "title-asc",
  "title-desc",
  "recent",
  "updated-desc",
  "accepted-desc",
];

/** feat-3：tags.update 可变字段（TagsHandler.TagsUpdate；displayName 传 null 清除回落 name）。 */
export interface TagChanges {
  color?: string;
  category?: string;
  sortOrder?: number;
  /** 星级评分 0–5（0=清除评分）。 */
  starred?: number;
  displayName?: string | null;
}

/** feat-2：launch.exited 到达时向 App 层广播的定位信号；nonce 单调递增使同游戏多次退出也能逐次触发。 */
export interface LaunchExitNotice {
  attemptId: string;
  gameId: string;
  nonce: number;
}

interface ScanJob {
  jobId: string;
  root: string;
}

export interface ScanProgressState {
  phase: "starting" | "running" | "cancelling";
  activeRoots: number;
  scannedDirectories: number;
  candidatesFound: number;
  startedAt: number;
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
  /**
   * feat-2：最近一次 launch.exited 事件（游戏进程退出）。事件流按游标消费、
   * Host 侧同一 attempt 只发一次（HostRuntime.cs:235 状态门），这里再按 attemptId
   * 去重兜底——用户主动关闭到托盘不产生该事件，不存在反复弹窗的冲突。
   */
  launchExit: LaunchExitNotice | null;
  scanning: boolean;
  scanProgress: ScanProgressState | null;
  refreshMeta: () => Promise<void>;
  startScan: () => Promise<void>;
  cancelScan: () => Promise<void>;
  reviewCandidates: (
    items: CandidateItem[],
    action: "accept" | "defer" | "ignore",
  ) => Promise<Array<{ candidate: CandidateItem; similarTo: SimilarGameSuggestion[] }>>;
  launch: (gameId: string) => Promise<void>;
  createTag: (name: string, category?: string) => Promise<void>;
  renameTag: (tag: TagItem, name: string) => Promise<void>;
  /** feat-3：color/category/sortOrder/starred/displayName 增量更新（engine 标签同样允许）。 */
  updateTag: (tag: TagItem, changes: TagChanges) => Promise<void>;
  /** feat-3：组内重排——批量写 sortOrder 后只刷新一次元数据。 */
  reorderTags: (updates: Array<{ tag: TagItem; sortOrder: number }>) => Promise<void>;
  removeTag: (tag: TagItem) => Promise<void>;
  addRoot: (path: string, kind?: "library" | "manual") => Promise<void>;
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
  const [launchExit, setLaunchExit] = useState<LaunchExitNotice | null>(null);
  const [jobs, setJobs] = useState<ScanJob[]>([]);
  const [scanProgress, setScanProgress] = useState<ScanProgressState | null>(null);

  const cursorRef = useRef<number | undefined>(undefined);
  const mounted = useRef(true);
  /** launch.exited 按 attemptId 去重（Host 状态门保证单发，这里防游标回拨等异常重放）。 */
  const seenAttemptIds = useRef<Set<string>>(new Set());
  const exitNonce = useRef(0);

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

  // 事件流：初次挂载直接以 Host 返回的末尾游标建立基线，避免把最多 1 万条历史事件
  // 当成“刚发生的变化”。基线建立后再读一次当前快照，避免两者并发产生漏事件窗口。
  // 之后只对影响可见库的增量事件失效查询。
  useEffect(() => {
    let cancelled = false;
    let timer: number | undefined;
    let pollInFlight = false;

    const readBatch = async (limit = EVENT_READ_LIMIT) => {
      const result = await operation<{
        nextCursor: number;
        latestCursor?: number;
        items: LibraryEvent[];
      }>(
        "events.read",
        { cursor: cursorRef.current, limit },
      );
      cursorRef.current = result.data.nextCursor;
      return result.data;
    };

    const establishBaseline = async () => {
      cursorRef.current = undefined;
      const baseline = await readBatch(1);
      cursorRef.current = baseline.latestCursor ?? baseline.nextCursor;
    };

    const poll = async () => {
      if (pollInFlight) return;
      pollInFlight = true;
      try {
        const batch = await readBatch();
        if (cancelled) return;

        // feat-2：launch.exited 到达 → 置前主窗口 + 广播给 App 层定位该游戏。
        // 只在该事件到达时动作一次：attemptId 去重 + 游标只前进，用户关闭到托盘
        // 不产生该事件，不会触发任何窗口动作。
        const exits = batch.items
          .map(parseLaunchExit)
          .filter((item): item is { attemptId: string; gameId: string } => item !== null);
        for (const exit of exits) {
          if (seenAttemptIds.current.has(exit.attemptId)) continue;
          seenAttemptIds.current.add(exit.attemptId);
          if (seenAttemptIds.current.size > 1024) {
            seenAttemptIds.current = new Set([...seenAttemptIds.current].slice(-512));
          }
          exitNonce.current += 1;
          if (mounted.current) {
            setLaunchExit({ attemptId: exit.attemptId, gameId: exit.gameId, nonce: exitNonce.current });
          }
          void showMainWindow();
        }

        if (batch.items.some(eventAffectsVisibleLibrary)) {
          await refreshMeta();
          bump();
        }
      } catch (cause) {
        if (describeFailure(cause).includes("CursorExpired")) {
          await establishBaseline();
          if (!cancelled) {
            await refreshMeta();
            bump();
          }
        }
      } finally {
        pollInFlight = false;
      }
    };

    void (async () => {
      await establishBaseline().catch(() => undefined);
      if (!cancelled) await refreshMeta();
    })().finally(() => {
      if (!cancelled) {
        timer = window.setInterval(() => void poll().catch(() => undefined), EVENT_POLL_MS);
      }
    });

    return () => {
      cancelled = true;
      if (timer !== undefined) window.clearInterval(timer);
    };
  }, [bump, refreshMeta]);

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
    setScanProgress({
      phase: "starting",
      activeRoots: started.length,
      scannedDirectories: 0,
      candidatesFound: 0,
      startedAt: Date.now(),
    });
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
    setScanProgress((previous) => previous && { ...previous, phase: "cancelling" });
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
        const terminalCount = alive.length - running.length;

        if (!mounted.current) return;

        if (alive.length === jobs.length && running.length === 0) {
          window.clearInterval(timer);
          setJobs([]);
          setScanProgress(null);
          await refreshMeta();
          bump();
          return;
        }

        // 单次 coverage 请求失败不代表扫描结束；保留上一帧，等待下次轮询恢复。
        if (alive.length === 0) return;

        // 已完成的目录树仍计入累计数，避免多根扫描时某一根先结束后数字反向跳小。
        const directories = alive.reduce(
          (sum, item) => sum + (item.coverage?.scannedDirectories ?? 0),
          0,
        );
        const found = alive.reduce((sum, item) => sum + (item.coverage?.candidatesFound ?? 0), 0);
        setScanProgress((previous) => ({
          phase: previous?.phase === "cancelling" ? "cancelling" : "running",
          activeRoots: jobs.length - terminalCount,
          scannedDirectories: Math.max(previous?.scannedDirectories ?? 0, directories),
          candidatesFound: Math.max(previous?.candidatesFound ?? 0, found),
          startedAt: previous?.startedAt ?? Date.now(),
        }));
      })();
    }, SCAN_POLL_MS);
    return () => window.clearInterval(timer);
  }, [jobs, refreshMeta, bump]);

  const reviewCandidates = useCallback(
    async (items: CandidateItem[], action: "accept" | "defer" | "ignore") => {
      const failures: string[] = [];
      const accepted: Array<{ candidate: CandidateItem; similarTo: SimilarGameSuggestion[] }> = [];
      for (const candidate of items) {
        try {
          const result = await operation<CandidateReviewResult>(
            `candidates.${action}`,
            { candidateId: candidate.candidateId, expectedRevision: candidate.revision },
            `candidates.${action}:${candidate.candidateId}:${candidate.revision}`,
          );
          // similarTo 只在首次 accept 建卡时返回；幂等重放/defer/ignore/旧后端一律 undefined → []，
          // 调用方按「无建议」静默处理，不放大失败面（单项失败走下方 failures 链）。
          if (action === "accept") {
            accepted.push({ candidate, similarTo: result.data.similarTo ?? [] });
          }
        } catch (cause) {
          failures.push(`${candidate.relativePath || candidate.physicalPath}: ${describeFailure(cause)}`);
        }
      }

      await refreshMeta();
      bump();
      if (failures.length > 0) {
        throw new Error(`${failures.length} 项失败：${failures.slice(0, 3).join("；")}`);
      }
      return accepted;
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
    async (name: string, category?: string) => {
      // feat-3：category 缺省由后端按 kind 推断（user→special）；显式传入时校验留给后端。
      await operation(
        "tags.create",
        { name, ...(category ? { category } : {}) },
        `tags.create:${name}:${category ?? "special"}`,
      );
      await refreshMeta();
    },
    [refreshMeta],
  );

  const renameTag = useCallback(
    async (tag: TagItem, name: string) => {
      // feat-3：engine 标签 name 是身份键不可变（TagsHandler.cs:169），改名走 displayName；
      // user 标签直接改 name。displayName 无显式清除入口，清除走 updateTag({ displayName: null })。
      const isEngine = tag.kind === "engine";
      await operation(
        "tags.update",
        isEngine
          ? { tagId: tag.tagId, expectedRevision: tag.revision, displayName: name }
          : { tagId: tag.tagId, expectedRevision: tag.revision, name },
        `tags.update:${tag.tagId}:${tag.revision}:${isEngine ? "displayName" : "name"}:${name}`,
      );
      await refreshMeta();
    },
    [refreshMeta],
  );

  const updateTag = useCallback(
    async (tag: TagItem, changes: TagChanges) => {
      const parameters: Record<string, unknown> = {
        tagId: tag.tagId,
        expectedRevision: tag.revision,
      };
      if (changes.color !== undefined) parameters.color = changes.color;
      if (changes.category !== undefined) parameters.category = changes.category;
      if (changes.sortOrder !== undefined) parameters.sortOrder = changes.sortOrder;
      if (changes.starred !== undefined) parameters.starred = changes.starred;
      // displayName: null 显式清除（后端按 JSON Null 走 clearDisplayName 分支，TagsHandler.cs:225）。
      if (changes.displayName !== undefined) parameters.displayName = changes.displayName;

      // 幂等键随变更内容稳定：同一次修改重试命中收据重放，不同变更互不串键。
      const changedKeys = Object.keys(changes).sort();
      await operation(
        "tags.update",
        parameters,
        `tags.update:${tag.tagId}:${tag.revision}:${changedKeys.join(",")}:${JSON.stringify(changes)}`,
      );
      await refreshMeta();
    },
    [refreshMeta],
  );

  const reorderTags = useCallback(
    async (updates: Array<{ tag: TagItem; sortOrder: number }>) => {
      // 同组重排：逐条写 sortOrder（各 tag 只更新一次，快照 revision 互不失效），末尾单次刷新。
      for (const { tag, sortOrder } of updates) {
        await operation(
          "tags.update",
          { tagId: tag.tagId, expectedRevision: tag.revision, sortOrder },
          `tags.update:${tag.tagId}:${tag.revision}:sortOrder:${sortOrder}`,
        );
      }
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
    async (path: string, kind?: "library" | "manual") => {
      // roots.add 在 operations.v1.json 中 requiresIdempotencyKey=true，
      // 但 OperationSchemas.InputSpecs 未登记该参数——以契约为准，必须传。
      // bug-5：kind='manual' 注册手动添加游戏的边界根——参与路径包含校验（RootRegistry.Contains），
      // 但 roots.list 默认不返回、扫描枚举也不取（CatalogingHandler.cs:167-182 / ListScannable）。
      // roots.add 对同一规范化路径幂等返回既有根（RootRegistry.Add:75-78）。
      await operation(
        "roots.add",
        { root: path, ...(kind ? { kind } : {}) },
        `roots.add:${kind ?? "library"}:${path}`,
      );
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
      bump();
    },
    [refreshMeta, bump],
  );

  const createView = useCallback(
    async (name: string, filters: GameFilters) => {
      await operation(
        "views.create",
        {
          name,
          search: filters.search || undefined,
          favoriteOnly: filters.favoriteOnly || undefined,
          tagId: filters.tagId || undefined,
          // bug-1：views.create 的 sort 白名单与 games.list 完全一致（六值，ViewSettingsHandler.cs:375）。
          // LibraryToolbar 四个排序值均合法；未知名兜底映射到 "title"，避免保存收藏夹被后端拒绝。
          sort: SORT_WHITELIST.includes(filters.sort) ? filters.sort : "title",
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
      launchExit,
      scanning,
      scanProgress,
      refreshMeta,
      startScan,
      cancelScan,
      reviewCandidates,
      launch,
      createTag,
      renameTag,
      updateTag,
      reorderTags,
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
      launchExit,
      scanning,
      scanProgress,
      refreshMeta,
      startScan,
      cancelScan,
      reviewCandidates,
      launch,
      createTag,
      renameTag,
      updateTag,
      reorderTags,
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

  const applyResult = useCallback((items: GameItem[], totalNow: number) => {
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
    async (preserveLoaded: boolean) => {
      const first = await queryPage(0);
      const previousCount = preserveLoaded ? gamesRef.current.length : 0;
      let items = first.items;
      // 偏移分页在数据插入/删除时可能跨页重复同一游戏（边界位移），按 gameId 去重。
      const seen = new Set(items.map((item) => item.gameId));
      while (items.length < previousCount && items.length < first.total) {
        const more = await queryPage(items.length);
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
    [queryPage, applyResult],
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
        await enqueue(() => refresh(!isFilterChange && hasLoadedRef.current));
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
    // suppressAutoRefresh 期间（扫描进行中）不自动重查，避免与扫描写入争抢后端串行锁。
  }, [enqueue, refresh, changeToken, suppressAutoRefresh]);

  const loadMore = useCallback(() => {
    if (loadingMore || gamesRef.current.length >= totalRef.current) return;
    setLoadingMore(true);
    void enqueue(async () => {
      const page = await queryPage(gamesRef.current.length);
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
      .catch((cause) => setError(describeFailure(cause)))
      .finally(() => setLoadingMore(false));
  }, [loadingMore, enqueue, queryPage, applyResult]);

  const reload = useCallback(async () => {
    if (!hasLoadedRef.current) setLoading(true);
    try {
      // 手动刷新同样保留已加载页（详情页"添加封面"等 onChanged → reload 的路径与
      // 事件流 bump() 共用同一语义：后台刷新不得改变已加载条数与顺序）。
      await enqueue(() => refresh(true));
      setError(null);
    } catch (cause) {
      setError(describeFailure(cause));
    } finally {
      hasLoadedRef.current = true;
      setLoading(false);
    }
  }, [enqueue, refresh]);

  return { games, total, loading, loadingMore, error, hasMore: games.length < total, loadMore, reload };
}
