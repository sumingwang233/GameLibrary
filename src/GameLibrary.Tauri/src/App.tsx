import { useCallback, useState } from "react";
import { AlertCircle, Bell, Library as LibraryIcon } from "lucide-react";
import { open } from "@tauri-apps/plugin-dialog";
import { describeFailure, operation } from "./lib/api";
import { emptyFilters, useGamesQuery, useLibrary, type GameFilters } from "./lib/state";
import type { CandidateItem, GameItem, ViewItem } from "./lib/types";
import { AppShell } from "./components/AppShell";
import { DetailSheet } from "./components/DetailSheet";
import { GameGrid } from "./components/GameGrid";
import { LibraryToolbar } from "./components/LibraryToolbar";
import { ReviewList, type ReviewAction } from "./components/ReviewList";
import { RootsPanel } from "./components/RootsPanel";
import { ScanProgressBar } from "./components/ScanProgressBar";
import { SettingsDialog } from "./components/SettingsDialog";
import { Sidebar, type Section } from "./components/Sidebar";
import { SimilarNotice, type SimilarNoticeData } from "./components/SimilarNotice";
import { TagsPanel } from "./components/TagsPanel";
import { Button } from "./components/ui/button";
import { PromptDialog } from "./components/ui/prompt-dialog";
import { TooltipProvider } from "./components/ui/tooltip";

type PromptKind = "tag" | "view" | null;

function App() {
  const library = useLibrary();
  const [section, setSection] = useState<Section>("library");
  const [filters, setFilters] = useState<GameFilters>(emptyFilters);
  const [layout, setLayout] = useState<"grid" | "compact">("grid");
  const [selected, setSelected] = useState<GameItem | null>(null);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [actionError, setActionError] = useState<string | null>(null);
  const [reviewBusy, setReviewBusy] = useState(false);
  const [prompt, setPrompt] = useState<PromptKind>(null);
  // accept 后的相似副本提示横幅：单例不堆叠，新批（含 M=0 的批）覆盖旧批；defer/ignore 不动它。
  const [similarNotice, setSimilarNotice] = useState<SimilarNoticeData | null>(null);

  const patchFilters = useCallback(
    (change: Partial<GameFilters>) => setFilters((previous) => ({ ...previous, ...change })),
    [],
  );

  // 扫描进行中不让事件流触发重查：后端所有请求经单一 _requestGate 串行，
  // 扫描写入期间频繁重查会与之争锁，正是 v1.1.5「扫描时很卡」的成因之一。
  const games = useGamesQuery(filters, library.changeToken, library.scanning);

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
      const selectedPath = await open({ directory: true, multiple: false, title: "选择游戏库目录" });
      if (!selectedPath) return;
      await library.addRoot(String(selectedPath));
    });

  const manualAdd = () =>
    run(async () => {
      const picked = await open({
        multiple: false,
        directory: false,
        title: "选择游戏主程序",
        filters: [{ name: "游戏程序", extensions: ["exe", "swf", "lnk"] }],
      });
      if (!picked) return;
      const sourcePath = String(picked);
      const root = sourcePath.replace(/[\\/][^\\/]+$/, "");
      await library.addRoot(root);
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

  const handleReview = async (items: CandidateItem[], action: ReviewAction) => {
    if (items.length === 0) return;
    setReviewBusy(true);
    setActionError(null);
    try {
      const accepted = await library.reviewCandidates(items, action);
      if (action === "accept") {
        // 聚合相似建议：组内按相似度降序（后端已降序，这里防御重放/后端变化），
        // 组间按各组最高相似度降序；undefined/[] 一律当无建议 → 横幅不渲染。
        const groups = accepted
          .filter((item) => item.similarTo.length > 0)
          .map((item) => ({
            sourceTitle: item.candidate.relativePath || item.candidate.physicalPath,
            entries: [...item.similarTo].sort((a, b) => b.similarity - a.similarity),
          }))
          .sort((a, b) => b.entries[0].similarity - a.entries[0].similarity);
        setSimilarNotice(groups.length > 0 ? { addedCount: accepted.length, groups } : null);
      }
      games.reload();
    } catch (cause) {
      setActionError(describeFailure(cause));
    } finally {
      setReviewBusy(false);
    }
  };

  /** 横幅/详情里的相似条目跳转：先查当前页（命中零成本），miss 则 games.get 兜底。
   * 失败（目标已被移除等）走 run() 的 actionError 既有路径，selected 保持原游戏。 */
  const openGameById = (gameId: string) =>
    void run(async () => {
      const local = games.games.find((game) => game.gameId === gameId);
      if (local) {
        setSelected(local);
        return;
      }
      const detail = await operation<GameItem>("games.get", { gameId });
      setSelected(detail.data);
    });

  const launch = (gameId: string) =>
    run(async () => {
      await library.launch(gameId);
    });

  const sidebar = (
    <Sidebar
      section={section}
      onSectionChange={setSection}
      viewId={filters.viewId}
      onViewChange={(viewId) => patchFilters({ viewId })}
      views={library.views}
      favoriteOnly={filters.favoriteOnly}
      onFavoriteChange={(favoriteOnly) => patchFilters({ favoriteOnly })}
      tagId={filters.tagId}
      onTagChange={(tagId) => patchFilters({ tagId })}
      tags={library.tags}
      candidateTotal={library.candidateTotal}
      gameTotal={library.gameTotal}
      notificationTotal={library.notifications.length}
      onCreateTag={() => setPrompt("tag")}
      onCreateView={() => setPrompt("view")}
      onRemoveView={(view: ViewItem) => void run(() => library.removeView(view))}
      onAddRoot={() => void addRoot()}
      onManualAdd={() => void manualAdd()}
      onScan={() => void run(library.startScan)}
      scanning={library.scanning}
      onSettings={() => setSettingsOpen(true)}
    />
  );

  const heading = sectionHeading(section, filters, library.views);

  return (
    <TooltipProvider>
      <AppShell sidebar={sidebar}>
        <main className="min-w-0 flex-1 overflow-y-auto bg-background p-6">
          <header className="mb-6 flex items-end justify-between gap-4">
            <div>
              <div className="mb-2 flex items-center gap-2 text-xs font-semibold tracking-[0.22em] text-steam uppercase">
                <LibraryIcon size={15} aria-hidden="true" />
                Your collection
              </div>
              <h1 className="text-3xl font-bold tracking-tight text-text-primary">{heading.title}</h1>
              <p className="mt-1 text-sm text-text-secondary">{heading.subtitle}</p>
            </div>
          </header>

          {library.notifications.length > 0 && section !== "pending" && (
            <div className="mb-4 flex items-center gap-3 rounded-lg border border-steam/40 bg-steam-soft/40 px-4 py-3 text-sm">
              <Bell size={16} aria-hidden="true" className="shrink-0 text-steam" />
              <span className="flex-1 text-text-primary">
                检测到 {library.notifications.length.toLocaleString()} 批新游戏等待你确认是否加入游戏库。
              </span>
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
              >
                去查看
              </Button>
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
                onSearch={(search) => patchFilters({ search })}
                sort={filters.sort}
                onSort={(sort) => patchFilters({ sort })}
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
              <GameGrid
                games={games.games}
                loading={games.loading}
                loadingMore={games.loadingMore}
                layout={layout}
                hasMore={games.hasMore}
                selectedId={selected?.gameId}
                emptyHint={
                  filters.search
                    ? `没有匹配「${filters.search}」的游戏。`
                    : "添加游戏库目录并扫描，游戏会出现在这里。"
                }
                onLoadMore={games.loadMore}
                onSelect={(gameId) =>
                  setSelected(games.games.find((game) => game.gameId === gameId) ?? null)
                }
                onPlay={(gameId) => void launch(gameId)}
              />
            </>
          )}

          {section === "pending" && (
            <ReviewList
              candidates={library.candidates}
              busy={reviewBusy}
              onReview={handleReview}
            />
          )}

          {section === "tags" && (
            <TagsPanel
              tags={library.tags}
              onCreate={library.createTag}
              onRename={library.renameTag}
              onRemove={library.removeTag}
            />
          )}

          {section === "roots" && (
            <RootsPanel roots={library.roots} onAddRoot={library.addRoot} onRemoveRoot={library.removeRoot} />
          )}
        </main>

        <DetailSheet
          game={selected}
          tags={library.tags}
          onClose={() => setSelected(null)}
          onPlay={(gameId) => void launch(gameId)}
          onChanged={() => {
            void library.refreshMeta();
            void games.reload();
          }}
          onNavigate={openGameById}
        />

        <SettingsDialog open={settingsOpen} onClose={() => setSettingsOpen(false)} />

        <PromptDialog
          open={prompt === "tag"}
          title="新建标签"
          label="标签名称"
          placeholder="例如：ANIM、已通关"
          confirmLabel="创建"
          onCancel={() => setPrompt(null)}
          onSubmit={(name) => {
            setPrompt(null);
            void run(() => library.createTag(name));
          }}
        />

        <PromptDialog
          open={prompt === "view"}
          title="保存为收藏夹"
          label="收藏夹名称"
          placeholder={`例如：待通关${filters.search ? `（搜索“${filters.search}”）` : ""}`}
          confirmLabel="保存"
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

function sectionHeading(section: Section, filters: GameFilters, views: ViewItem[]) {
  if (section === "pending") {
    return { title: "待确认", subtitle: "扫描发现的新游戏，确认后才会进入你的游戏库。" };
  }
  if (section === "tags") {
    return { title: "管理标签", subtitle: "重命名或删除标签，引擎标签由扫描自动维护。" };
  }
  if (section === "roots") {
    return { title: "游戏库目录", subtitle: "管理扫描范围与过滤名单。" };
  }
  if (filters.viewId) {
    const view = views.find((item) => item.viewId === filters.viewId);
    return { title: view?.name ?? "收藏夹", subtitle: "自定义收藏夹" };
  }
  if (filters.favoriteOnly) {
    return { title: "收藏", subtitle: "你标记为收藏的游戏。" };
  }
  if (filters.tagId) {
    return { title: "按标签筛选", subtitle: "只显示带该标签的游戏。" };
  }
  return { title: "游戏库", subtitle: "本地优先，数据只存在这台电脑上。" };
}

export default App;
