import { useMemo, useState } from "react";
import { AlertCircle, Library } from "lucide-react";
import { open } from "@tauri-apps/plugin-dialog";
import { operation } from "./lib/api";
import { useFilteredGames, useLibrary } from "./lib/state";
import type { CandidateItem, GameItem } from "./lib/types";
import { Badge } from "./components/ui/badge";
import { Sidebar } from "./components/Sidebar";
import { LibraryToolbar } from "./components/LibraryToolbar";
import { GameGrid } from "./components/GameGrid";
import { DetailSheet } from "./components/DetailSheet";
import { ReviewBatchBar } from "./components/ReviewBatchBar";
import { ScanProgressBar } from "./components/ScanProgressBar";
import { SettingsDialog } from "./components/SettingsDialog";
import { AppShell } from "./components/AppShell";
import { EmptyState } from "./components/EmptyState";
import { Checkbox } from "./components/ui/checkbox";
import { TooltipProvider } from "./components/ui/tooltip";

function App() {
  const library = useLibrary();
  const [libraryView, setLibraryView] = useState("all");
  const [layoutView, setLayoutView] = useState("grid");
  const [search, setSearch] = useState("");
  const [sort, setSort] = useState("recent");
  const [tagId, setTagId] = useState("");
  const [selected, setSelected] = useState<GameItem | null>(null);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [actionError, setActionError] = useState<string | null>(null);

  const games = useFilteredGames(library.snapshot.games, search, libraryView, tagId);
  const sortedGames = useMemo(() => [...games].sort((a, b) => {
    if (sort === "title-asc") return a.title.localeCompare(b.title, "zh");
    if (sort === "title-desc") return b.title.localeCompare(a.title, "zh");
    return (b.updatedUtc ?? b.acceptedUtc ?? "").localeCompare(a.updatedUtc ?? a.acceptedUtc ?? "");
  }), [games, sort]);

  const run = async (work: () => Promise<unknown>) => {
    setActionError(null);
    try { await work(); } catch (error) { setActionError(error instanceof Error ? error.message : "操作失败"); }
  };

  const addRoot = () => run(async () => {
    const selected = await open({ directory: true, multiple: false, title: "选择游戏库目录" });
    if (!selected) return;
    await operation("roots.add", { root: String(selected), idempotencyKey: `tauri-root-${Date.now()}` });
    await library.refresh();
  });

  const createTag = () => run(async () => {
    const name = window.prompt("标签名称")?.trim();
    if (!name) return;
    await operation("tags.create", { name, idempotencyKey: `tauri-tag-create-${Date.now()}` });
    await library.refresh();
  });

  const manualAdd = () => run(async () => {
    const selected = await open({ multiple: false, directory: false, title: "选择游戏主程序", filters: [{ name: "游戏程序", extensions: ["exe", "swf", "lnk"] }] });
    if (!selected) return;
    const sourcePath = String(selected);
    const root = sourcePath.replace(/[\\/][^\\/]+$/, "");
    await operation("roots.add", { root, idempotencyKey: `tauri-root-${Date.now()}` });
    const created = await operation<{ gameId: string; launchSuggestion?: { executablePath: string; argv: string[]; cwd: string } }>("games.create", { sourcePath, idempotencyKey: `tauri-game-${Date.now()}` });
    if (created.data.launchSuggestion) await operation("profiles.create", { ...created.data.launchSuggestion, gameId: created.data.gameId, isDefault: true, idempotencyKey: `tauri-profile-${Date.now()}` });
    await library.refresh();
  });

  const sidebar = <Sidebar
        view={libraryView}
        onViewChange={setLibraryView}
        tagId={tagId}
        tags={library.snapshot.tags}
        onTagChange={setTagId}
        candidateTotal={library.snapshot.candidateTotal}
        gameTotal={library.snapshot.gameTotal}
        onCreateTag={() => void createTag()}
        onAddRoot={() => void addRoot()}
        onManualAdd={() => void manualAdd()}
        onScan={() => void run(library.startScan)}
        onSettings={() => setSettingsOpen(true)}
      />;

  return (
    <TooltipProvider><AppShell sidebar={sidebar}>
      <main className="min-w-0 flex-1 overflow-y-auto bg-[radial-gradient(circle_at_80%_-20%,#2a475e55,transparent_40%),#1b2838] p-6">
        <header className="mb-6 flex items-end justify-between gap-4">
          <div>
            <div className="mb-2 flex items-center gap-2 text-xs font-semibold uppercase tracking-[0.22em] text-steam"><Library size={15} />Your collection</div>
            <h1 className="text-3xl font-bold tracking-tight text-text-primary">游戏库</h1>
            <p className="mt-1 text-sm text-text-secondary">{libraryView === "pending" ? `${library.snapshot.candidateTotal.toLocaleString()} 个项目等待确认` : `${library.snapshot.gameTotal.toLocaleString()} 个游戏 · 本地优先`}</p>
          </div>
          <div className="flex items-center gap-2">
            <Badge>{layoutView === "grid" ? "网格" : "紧凑"}</Badge>
            {library.error && <span className="flex items-center gap-1 text-xs text-danger"><AlertCircle size={14} />{library.error}</span>}
          </div>
        </header>
        <LibraryToolbar search={search} onSearch={setSearch} sort={sort} onSort={setSort} onRefresh={() => void library.refresh()} view={layoutView} onView={setLayoutView} />
        <ScanProgressBar text={library.scanText} onCancel={() => void run(library.cancelScan)} />
        {actionError && <div className="mb-4 flex items-center gap-2 rounded-md border border-danger/40 bg-danger/10 px-4 py-3 text-sm text-danger"><AlertCircle size={16} />{actionError}</div>}
        {libraryView === "pending"
          ? <ReviewList candidates={library.snapshot.candidates} onReview={(items, action) => void run(() => library.reviewCandidates(items, action))} />
          : <GameGrid games={sortedGames} loading={library.loading} layout={layoutView} selectedId={selected?.gameId} onSelect={setSelected} onPlay={(game) => void run(() => library.launch(game.gameId))} />}
      </main>
      <DetailSheet game={selected} tags={library.snapshot.tags} onClose={() => setSelected(null)} onPlay={(game) => void run(() => library.launch(game.gameId))} onUpdated={library.refresh} />
      <SettingsDialog open={settingsOpen} onClose={() => setSettingsOpen(false)} />
    </AppShell></TooltipProvider>
  );
}

function ReviewList({ candidates, onReview }: { candidates: CandidateItem[]; onReview: (items: CandidateItem[], action: "accept" | "defer" | "ignore") => void }) {
  const [selected, setSelected] = useState<Set<string>>(new Set());
  if (!candidates.length) {
    return <EmptyState symbol="✓" title="没有待确认项目" message="扫描结果会在这里等待你的确认。" />;
  }
  const chosen = candidates.filter(candidate => selected.has(candidate.candidateId));
  const apply = (action: "accept" | "defer" | "ignore") => { onReview(chosen, action); setSelected(new Set()); };
  return <div><ReviewBatchBar candidates={candidates} selectedCount={chosen.length} onAccept={() => apply("accept")} onDefer={() => apply("defer")} onIgnore={() => apply("ignore")} /><div className="space-y-2">{candidates.map(candidate => <label key={candidate.candidateId} className="flex cursor-pointer items-center gap-4 rounded-lg border border-border bg-surface p-4"><Checkbox checked={selected.has(candidate.candidateId)} onCheckedChange={() => setSelected(previous => { const next = new Set(previous); next.has(candidate.candidateId) ? next.delete(candidate.candidateId) : next.add(candidate.candidateId); return next; })} aria-label={`选择 ${candidate.relativePath}`} /><div className="flex h-12 w-12 items-center justify-center rounded-md bg-steam-soft text-xl text-steam">◇</div><div className="min-w-0 flex-1"><div className="truncate font-semibold text-text-primary">{candidate.relativePath || candidate.physicalPath}</div><div className="mt-1 truncate text-xs text-text-secondary">{candidate.kind} · {candidate.physicalPath}</div></div></label>)}</div></div>;
}

export default App;
