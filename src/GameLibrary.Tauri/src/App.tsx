import { t } from "./lib/i18n";
import { lazy, Suspense, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { useLanguage } from "./lib/i18n";
import { AlertCircle, Bell } from "lucide-react";
import { open } from "@tauri-apps/plugin-dialog";
import { describeFailure, operation } from "./lib/api";
import { LaunchProfileSelectionRequired } from "./lib/launchProfiles";
import { emptyFilters, useGamesQuery, useLibrary, type GameFilters } from "./lib/state";
import { resolveSelectedGame } from "./lib/selection";
import type { CandidateItem, GameItem, ViewItem } from "./lib/types";
import { AppShell } from "./components/AppShell";
import { TitleBar } from "./components/TitleBar";
import { GameGrid } from "./components/GameGrid";
import { TitleTranslationStatus } from "./components/TitleTranslationStatus";
import { useTitleTranslation } from "./lib/hooks/useTitleTranslation";
import type { ReviewOutcome } from "./lib/hooks/useCandidateReview";
import { UnityTranslationDialog, type UnityTranslationRequest, type UnityTranslationItem } from "./components/UnityTranslationDialog";
import { LibraryToolbar } from "./components/LibraryToolbar";
import type { ReviewAction } from "./components/ReviewList";
import { ScanProgressBar } from "./components/ScanProgressBar";
import { Sidebar, type Section } from "./components/Sidebar";
import { SimilarNotice, type SimilarNoticeData } from "./components/SimilarNotice";
import { Button } from "./components/ui/button";
import { PromptDialog } from "./components/ui/prompt-dialog";
import { TooltipProvider } from "./components/ui/tooltip";

const DetailSheet = lazy(() => import("./components/DetailSheet").then(module => ({ default: module.DetailSheet })));
const SettingsDialog = lazy(() => import("./components/SettingsDialog").then(module => ({ default: module.SettingsDialog })));
const ReviewList = lazy(() => import("./components/ReviewList").then(module => ({ default: module.ReviewList })));
const RootsPanel = lazy(() => import("./components/RootsPanel").then(module => ({ default: module.RootsPanel })));
const TagsPanel = lazy(() => import("./components/TagsPanel").then(module => ({ default: module.TagsPanel })));

type PromptKind = "tag" | "view" | null;

function App() {
  useLanguage();
  const library = useLibrary();
  const [section, setSection] = useState<Section>("library");
  const [filters, setFilters] = useState<GameFilters>(emptyFilters);
  const [layout, setLayout] = useState<"grid" | "compact">("grid");
  // 详情只持有 gameId（外加列表外游戏的回退快照），渲染时从当前列表取最新对象：
  // 后台 reload / titleTranslation patch 之后标题、收藏、封面不会停留在打开瞬间的旧值。
  const [selectedGameId, setSelectedGameId] = useState<string | null>(null);
  const [selectedFallback, setSelectedFallback] = useState<GameItem | null>(null);
  const [ratingRequest, setRatingRequest] = useState<{ gameId: string; nonce: number } | null>(null);
  const [launchSelectionGameId, setLaunchSelectionGameId] = useState<string | null>(null);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [unitySettingsOpen, setUnitySettingsOpen] = useState(false);
  const [unityRequest, setUnityRequest] = useState<UnityTranslationRequest | null>(null);
  const [unityStatus, setUnityStatus] = useState<UnityTranslationItem[]>([]);
  const [unityReviewTagId, setUnityReviewTagId] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const [reviewBusy, setReviewBusy] = useState(false);
  const [prompt, setPrompt] = useState<PromptKind>(null);
  const mainRef = useRef<HTMLElement | null>(null);
  const pendingScrollTop = useRef<number | null>(null);
  // accept 后的相似副本提示横幅：单例不堆叠，新批（含 M=0 的批）覆盖旧批；defer/ignore 不动它。
  const [similarNotice, setSimilarNotice] = useState<SimilarNoticeData | null>(null);

  const patchFilters = useCallback(
    (change: Partial<GameFilters>) => setFilters((previous) => ({ ...previous, ...change })),
    [],
  );

  const [translationBusy, setTranslationBusy] = useState(false);
  const games = useGamesQuery(filters, library.changeToken, translationBusy, library.gameChanges);
  const selected = useMemo(
    () => resolveSelectedGame(games.games, selectedGameId, selectedFallback),
    [games.games, selectedGameId, selectedFallback],
  );
  const titleTranslation = useTitleTranslation(patch => {
    games.patch(patch);
    // 列表命中的情况由 games.patch 更新；这里同步回退快照，列表外的游戏也能拿到新标题。
    setSelectedFallback(previous => previous?.gameId === patch.gameId && previous.revision <= patch.revision ? { ...previous, ...patch } : previous);
  }, async () => { await games.reload(); await library.refreshMeta(); });
  useEffect(() => { setTranslationBusy(titleTranslation.busy); }, [titleTranslation.busy]);

  /** 详情操作会触发列表失效重查。记录主滚动容器的位置，等新一帧列表提交后恢复，
   * 避免启动游戏、导入封面等操作把用户从当前卡片跳回列表顶部。 */
  const preserveLibraryScroll = useCallback(async (work: () => Promise<void>) => {
    pendingScrollTop.current = mainRef.current?.scrollTop ?? 0;
    await work();
  }, []);

  useLayoutEffect(() => {
    if (pendingScrollTop.current === null || games.loading) return;
    mainRef.current?.scrollTo({ top: pendingScrollTop.current, behavior: "auto" });
    pendingScrollTop.current = null;
  }, [games.games, games.loading, library.changeToken]);

  const run = async (work: () => Promise<unknown>) => {
    setActionError(null);
    try {
      await work();
    } catch (cause) {
      setActionError(describeFailure(cause));
    }
  };

  const addRoot = () =>
    run(async () => {
      const selectedPath = await open({ directory: true, multiple: false, title: t("选择游戏库目录") });
      if (!selectedPath) return;
      await library.addRoot(String(selectedPath));
    });

  const manualAdd = () =>
    run(async () => {
      const picked = await open({
        multiple: false,
        directory: false,
        title: t("选择游戏主程序"),
        filters: [{ name: t("游戏程序"), extensions: ["exe", "swf", "lnk"] }],
      });
      if (!picked) return;
      const sourcePath = String(picked);
      const root = sourcePath.replace(/[\\/][^\\/]+$/, "");
      // bug-5：手动添加只注册 kind='manual' 的边界根（仍参与路径包含校验），
      // roots.list 默认不返回 manual 根（CatalogingHandler.cs:180-182），
      // 扫描枚举也不取（RootRegistry.ListScannable）——游戏库目录列表保持干净。
      await library.addRoot(root, "manual");
      const created = await operation<{
        gameId: string;
        launchSuggestion?: { executablePath: string; argv: string[]; cwd: string };
      }>("games.create", { sourcePath }, `games.create:${sourcePath}`);
      if (created.data.launchSuggestion) {
        await operation(
          "profiles.create",
          {
            ...created.data.launchSuggestion,
            gameId: created.data.gameId,
            isDefault: true,
          },
          `profiles.create:${created.data.gameId}`,
        );
      }
      await library.refreshMeta();
      games.reload();
    });

  /** 部分失败不再抛整批错误：失败计数写进 actionError，成功项 ID 回传给 ReviewList，
   * 由它只清除成功的勾选，失败项保持选中便于重试。 */
  const handleReview = async (items: CandidateItem[], action: ReviewAction): Promise<ReviewOutcome> => {
    if (items.length === 0) return { succeededIds: [], accepted: [], failureMessage: null };
    setReviewBusy(true);
    setActionError(null);
    try {
      const outcome = await library.reviewCandidates(items, action);
      if (outcome.failureMessage) setActionError(outcome.failureMessage);
      if (action === "accept") {
        // 聚合相似建议：组内按相似度降序（后端已降序，这里防御重放/后端变化），
        // 组间按各组最高相似度降序；undefined/[] 一律当无建议 → 横幅不渲染。
        const groups = outcome.accepted
          .filter((item) => item.similarTo.length > 0)
          .map((item) => ({
            sourceTitle: item.candidate.relativePath || item.candidate.physicalPath,
            entries: [...item.similarTo].sort((a, b) => b.similarity - a.similarity),
          }))
          .sort((a, b) => b.entries[0].similarity - a.entries[0].similarity);
        setSimilarNotice(groups.length > 0 ? { addedCount: outcome.accepted.length, groups } : null);
      }
      if (outcome.succeededIds.length > 0) games.reload();
      return outcome;
    } catch (cause) {
      // 整批级失败（批量请求本身挂了）：整批错误照旧提示，勾选保持不动。
      setActionError(describeFailure(cause));
      return { succeededIds: [], accepted: [], failureMessage: null };
    } finally {
      setReviewBusy(false);
    }
  };

  /** 横幅/详情里的相似条目跳转：先查当前页（命中零成本），miss 则 games.get 兜底。
   * 失败（目标已被移除等）走 run() 的 actionError 既有路径，当前详情保持不变。
   * useCallback：launch.exited 到达时（feat-2）经它把退出的游戏设为选中。
   * 状态只在取到对象后才落，避免 games.get 失败时把详情切成一个空 id。 */
  const openGameById = useCallback(
    (gameId: string) => {
      void run(async () => {
        const local = games.games.find((game) => game.gameId === gameId);
        if (local) {
          setSelectedFallback(local);
          setSelectedGameId(gameId);
          return;
        }
        const detail = await operation<GameItem>("games.get", { gameId });
        setSelectedFallback(detail.data);
        setSelectedGameId(gameId);
      });
    },
    [games.games],
  );

  // feat-2：launch.exited 事件到达 → state.ts 已置前主窗口，这里把该游戏设为选中
  // （openGameById 兜底 games.get）。nonce 去重保证每个事件只动作一次：
  // openGameById 依赖 games.games，若不按 nonce 挡住，退出后每次列表刷新都会重复选中。
  // 关闭到托盘不产生 launch.exited，与"退出自动弹窗"互不冲突。
  const handledExitNonce = useRef(-1);
  useEffect(() => {
    const exit = library.launchExit;
    if (!exit || exit.nonce === handledExitNonce.current) return;
    handledExitNonce.current = exit.nonce;
    setLaunchSelectionGameId(null);
    setRatingRequest({ gameId: exit.gameId, nonce: exit.nonce });
    openGameById(exit.gameId);
  }, [library.launchExit, openGameById]);

  const launch = (gameId: string) =>
    run(async () => {
      try { await preserveLibraryScroll(() => library.launch(gameId)); }
      catch (cause) {
        if (cause instanceof LaunchProfileSelectionRequired) { setLaunchSelectionGameId(gameId); openGameById(gameId); }
        throw cause;
      }
    });

  const sidebar = (
    <Sidebar
      section={section}
      onSectionChange={setSection}
      viewId={filters.viewId}
      onViewChange={(viewId) => {
        const view = library.views.find((item) => item.viewId === viewId);
        patchFilters({
          viewId,
          search: view?.search ?? "",
          favoriteOnly: view?.favoriteOnly ?? false,
          hasCover: view?.hasCover ?? null,
          tagId: view?.tagId ?? "",
          sort: view?.sort ?? "accepted-desc",
        });
      }}
      favoriteOnly={filters.favoriteOnly}
      playedOnly={filters.playedOnly}
      onPlayedChange={(playedOnly) => patchFilters({ ...emptyFilters, playedOnly })}
      onFavoriteChange={(favoriteOnly) => patchFilters({
        viewId: "",
        favoriteOnly,
        hasCover: null,
        tagId: "",
        search: "",
        sort: "accepted-desc",
      })}
      tagId={filters.tagId}
      onTagChange={(tagId, clickedTagId) => {
        patchFilters({ tagId, viewId: "", favoriteOnly: false });
        setUnityReviewTagId(clickedTagId ?? tagId);
      }}
      tags={library.tags}
      candidateTotal={library.candidateTotal}
      gameTotal={library.gameTotal}
      onCreateTag={() => setPrompt("tag")}
    />
  );

  const heading = sectionHeading(section, filters, library.views);
  const loadingPanel = <p role="status" className="fixed top-16 right-6 z-50 rounded-md bg-panel p-3 text-sm text-text-secondary">{t("正在加载…")}</p>;

  return (
    <TooltipProvider>
      <AppShell sidebar={sidebar} titleBar={<TitleBar onTags={() => setSection("tags")} onRoots={() => setSection("roots")} onManualAdd={() => void manualAdd()} onAddRoot={() => void addRoot()} onScan={() => void run(library.startScan)} onSettings={() => setSettingsOpen(true)} scanning={library.scanning} />}>
        <main ref={mainRef} className="min-w-0 flex-1 overflow-y-auto bg-background p-6">
          <header className="mb-6">
            <h1 className="text-3xl font-bold tracking-tight text-text-primary">{heading}</h1>
          </header>

          <Suspense fallback={loadingPanel}>

          {library.notifications.length > 0 && section !== "pending" && (
            <div className="mb-4 flex items-center gap-3 rounded-lg border border-steam/40 bg-steam-soft/40 px-4 py-3 text-sm">
              <Bell size={16} aria-hidden="true" className="shrink-0 text-steam" />
              <span className="flex-1 text-text-primary">{t("有 {0} 批新游戏等待确认是否加入游戏库。", library.notifications.length.toLocaleString())}</span>
              <Button
                size="sm"
                variant="outline"
                onClick={() => {
                  setSection("pending");
                  // 确认后未读计数才会消失；候选本身的加入/忽略仍在待确认列表里做。
                  void run(async () => {
                    for (const notification of library.notifications) {
                      await library.acknowledgeNotification(notification.notificationId);
                    }
                  });
                }}
              >{t("去查看")}</Button>
            </div>
          )}

          <SimilarNotice
            notice={similarNotice}
            onDismiss={() => setSimilarNotice(null)}
            onNavigate={openGameById}
          />

          {library.error && (
            <p className="mb-4 flex items-center gap-2 text-xs text-danger">
              <AlertCircle size={14} aria-hidden="true" />
              {library.error}
            </p>
          )}

          <ScanProgressBar
            progress={library.scanProgress}
            onCancel={() => void run(library.cancelScan)}
          />

          {actionError && (
            <div
              role="alert"
              className="mb-4 flex items-start gap-2 rounded-md border border-danger/40 bg-danger/10 px-4 py-3 text-sm text-danger"
            >
              <AlertCircle size={16} aria-hidden="true" className="mt-0.5 shrink-0" />
              <span className="break-words">{actionError}</span>
            </div>
          )}

          {section === "library" && (
            <>
              <LibraryToolbar
                search={filters.search}
                onSearch={(search) => patchFilters({ search, viewId: "" })}
                sort={filters.sort}
                onSort={(sort) => patchFilters({ sort, viewId: "" })}
                hasCover={filters.hasCover}
                onHasCover={(hasCover) => patchFilters({ hasCover, viewId: "" })}
                onRefresh={() => {
                  void games.reload();
                  void library.refreshMeta();
                }}
                layout={layout}
                onLayout={setLayout}
                total={games.total}
                shown={games.games.length}
              />
              {games.error && (
                <p role="alert" className="mb-4 text-sm text-danger">
                  {games.error}
                </p>
              )}
              {library.supports("titles.translate") && <TitleTranslationStatus controller={titleTranslation} />}
              <GameGrid
                scrollRoot={mainRef}
                key={JSON.stringify(filters)}
                tags={library.tags}
                titleTranslation={library.supports("titles.translate") ? titleTranslation : undefined}
                onChanged={() => preserveLibraryScroll(async () => {
                  setSelectedGameId(null);
                  setSelectedFallback(null);
                  await library.refreshMeta();
                  await games.reload();
                })}
                games={games.games}
                loading={games.loading}
                loadingMore={games.loadingMore}
                layout={layout}
                hasMore={games.hasMore}
                selectedId={selectedGameId ?? undefined}
                emptyHint={
                  filters.search
                    ? t("没有匹配「{0}」的游戏。", filters.search)
                    : filters.playedOnly
                      ? t("标记为已游玩的游戏会显示在这里。")
                      : t("添加游戏，或到侧栏“已游玩”查看玩过的游戏。")
                }
                onLoadMore={games.loadMore}
                onSelect={(gameId) => {
                  setRatingRequest(null);
                  setSelectedFallback(games.games.find((game) => game.gameId === gameId) ?? null);
                  setSelectedGameId(gameId);
                }}
                onPlay={(gameId) => void launch(gameId)}
              />
            </>
          )}

          {section === "pending" && (
            <ReviewList
              candidates={library.candidates}
              total={library.candidateTotal}
              hasMore={library.candidateHasMore}
              loadingMore={library.candidateLoading}
              onLoadMore={() => void library.loadMoreCandidates()}
              busy={reviewBusy}
              onReview={handleReview}
            />
          )}

          {section === "tags" && (
            <TagsPanel
              tags={library.tags}
              onCreate={library.createTag}
              onRename={library.renameTag}
              onUpdate={library.updateTag}
              onReorder={library.reorderTags}
              onRemove={library.removeTag}
            />
          )}

          {section === "roots" && (
            <RootsPanel roots={library.roots} onAddRoot={library.addRoot} onRemoveRoot={library.removeRoot} />
          )}
          </Suspense>
        </main>

        <Suspense fallback={loadingPanel}>
        {selected &&
        <DetailSheet
          game={selected}
          ratingPromptToken={ratingRequest?.gameId === selectedGameId ? ratingRequest?.nonce : undefined}
          initialTab={selectedGameId === launchSelectionGameId ? "launch" : "overview"}
          tags={library.tags}
          onUpdateTag={library.supports("tags.update") ? library.updateTag : undefined}
          refreshToken={library.changeToken}
          supportsSuggestions={library.supports("profiles.discover")}
          onUnityTranslation={library.supports("unity_translation.status") ? (gameId, action) => setUnityRequest({ gameId, action }) : undefined}
          unityTranslationState={unityStatus.find(item => item.gameId === selectedGameId)}
          titleTranslation={library.supports("titles.translate") ? titleTranslation : undefined}
          onClose={() => { setSelectedGameId(null); setSelectedFallback(null); setRatingRequest(null); }}
          onPlay={(gameId, profileId) => preserveLibraryScroll(() => library.launch(gameId, profileId))}
          onChanged={() => {
            return preserveLibraryScroll(async () => {
              await library.refreshMeta();
              await games.reload();
            });
          }}
          onNavigate={(gameId) => { setRatingRequest(null); openGameById(gameId); }}
        />}
        </Suspense>

        <Suspense fallback={loadingPanel}>
        {settingsOpen && <SettingsDialog open={settingsOpen} onClose={() => setSettingsOpen(false)} supportsTitleTranslation={library.supports("titles.translate")}
          onUnitySettings={library.supports("unity_translation.status") ? () => { setSettingsOpen(false); setUnitySettingsOpen(true); } : undefined} />}
        </Suspense>
        <UnityTranslationDialog enabled={library.supports("unity_translation.status")} settingsOpen={unitySettingsOpen} onCloseSettings={() => setUnitySettingsOpen(false)}
          reviewTagId={unityReviewTagId} onReviewHandled={() => setUnityReviewTagId(null)}
          onStatusChange={setUnityStatus}
          request={unityRequest} onRequestHandled={() => setUnityRequest(null)} onPlay={(gameId, profileId) => library.launch(gameId, profileId)}
          onChanged={async () => { await library.refreshMeta(); await games.reload(); }} onNavigate={openGameById} />

        <PromptDialog
          open={prompt === "tag"}
          title={t("新建标签")}
          label={t("标签名称")}
          placeholder={t("例如：ANIM、已通关")}
          confirmLabel={t("创建")}
          onCancel={() => setPrompt(null)}
          onSubmit={(name) => {
            setPrompt(null);
            void run(() => library.createTag(name));
          }}
        />

        <PromptDialog
          open={prompt === "view"}
          title={t("保存为收藏夹")}
          label={t("收藏夹名称")}
          placeholder={t("例如：待通关{0}", filters.search ? `（搜索“${filters.search}”）` : "")}
          confirmLabel={t("保存")}
          onCancel={() => setPrompt(null)}
          onSubmit={(name) => {
            setPrompt(null);
            void run(() => library.createView(name, filters));
          }}
        />
      </AppShell>
    </TooltipProvider>
  );
}

/** ui-2：只保留各页大标题，副标题全部删除。 */
function sectionHeading(section: Section, filters: GameFilters, views: ViewItem[]): string {
  if (section === "pending") return t("待确认");
  if (section === "tags") return t("管理标签");
  if (section === "roots") return t("游戏库目录");
  if (filters.viewId) {
    const view = views.find((item) => item.viewId === filters.viewId);
    return view?.name ?? t("收藏夹");
  }
  if (filters.playedOnly) return t("已游玩");
  if (filters.favoriteOnly) return t("收藏");
  if (filters.tagId) return t("按标签筛选");
  return t("游戏库");
}

export default App;
