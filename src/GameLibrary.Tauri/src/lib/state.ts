import { t } from "./i18n";
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
import { operation } from "./api";
import { discoverLaunchProfiles, LaunchProfileSelectionRequired, selectLaunchProfile, type ProfileList } from "./launchProfiles";
import { useLibraryMetadata } from "./hooks/useLibraryMetadata";
import { useLibraryEvents } from "./hooks/useLibraryEvents";
import { useScanJobs } from "./hooks/useScanJobs";
import { useCandidateReview } from "./hooks/useCandidateReview";
import { useTagActions } from "./hooks/useTagActions";
export { useGamesQuery, PAGE_SIZE } from "./hooks/useGamesQuery";
import type {
  CandidateItem,
  LaunchPlan,
  NotificationItem,
  RootItem,
  SimilarGameSuggestion,
  TagItem,
  ViewItem,
} from "./types";


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

export interface ScanProgressState {
  phase: "starting" | "running" | "cancelling";
  activeRoots: number;
  scannedDirectories: number;
  candidatesFound: number;
  startedAt: number;
}

interface LibraryController {
  supports: (name: string) => boolean;
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
  launch: (gameId: string, profileId?: string) => Promise<void>;
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
  const { tags, roots, views, notifications, candidates, gameTotal, candidateTotal,
    metaLoading, error, setError, refreshMeta, refreshDomains, initialize, supports } = useLibraryMetadata();
  const [changeToken, setChangeToken] = useState(0);
  const [launchExit, setLaunchExit] = useState<LaunchExitNotice | null>(null);

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

  useLibraryEvents(initialize, supports, refreshDomains, bump, setLaunchExit);

  const { startScan, cancelScan, scanning, scanProgress } = useScanJobs(roots, refreshMeta, bump, setError);

  const refreshReview = useCallback(() => refreshDomains(["candidates", "notifications", "games", "tags"]), [refreshDomains]);
  const reviewCandidates = useCandidateReview(supports, refreshReview, bump);

  const launch = useCallback(
    async (gameId: string, profileId?: string) => {
      let profiles = await operation<ProfileList>(
        "profiles.list",
        { gameId },
      );
      if (!profileId && !selectLaunchProfile(profiles.data) && supports("profiles.discover")) {
        await discoverLaunchProfiles(gameId);
        profiles = await operation<ProfileList>("profiles.list", { gameId });
      }
      const profile = selectLaunchProfile(profiles.data, profileId);
      if (!profile) {
        throw new LaunchProfileSelectionRequired();
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
    [bump, supports],
  );

  const refreshTags = useCallback(() => refreshDomains(["tags"]), [refreshDomains]);
  const { createTag, renameTag, updateTag, reorderTags, removeTag } = useTagActions(supports, refreshTags, bump);

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


  return useMemo(
    () => ({
      supports,
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
      supports,
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
  if (!value) throw new Error(t("useLibrary 必须在 LibraryProvider 内使用"));
  return value;
}
