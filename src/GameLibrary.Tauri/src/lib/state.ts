import { createContext, createElement, useCallback, useContext, useEffect, useMemo, useState, type PropsWithChildren } from "react";
import { operation } from "./api";
import type { CandidateItem, GameItem, LibrarySnapshot, RootItem, TagItem } from "./types";

function useLibraryController() {
  const [snapshot, setSnapshot] = useState<LibrarySnapshot>({ games: [], gameTotal: 0, candidates: [], candidateTotal: 0, tags: [], roots: [] });
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [scanJobId, setScanJobId] = useState<string | null>(null);
  const [scanText, setScanText] = useState<string | null>(null);

  const refresh = useCallback(async () => {
    setLoading(true); setError(null);
    try {
      const status = await operation<{ libraryInitialized: boolean }>("host.status");
      if (!status.data.libraryInitialized) await operation("library.init", { idempotencyKey: `tauri-init-${Date.now()}` });
      const [games, candidates, tags, roots] = await Promise.all([
        operation<{ total: number; items: GameItem[] }>("games.list", { limit: 500, offset: 0, sort: "recent" }),
        operation<{ total: number; items: CandidateItem[] }>("candidates.list", { state: "pendingReview", limit: 500, offset: 0 }),
        operation<{ total: number; items: TagItem[] }>("tags.list"),
        operation<{ total: number; items: RootItem[] }>("roots.list"),
      ]);
      setSnapshot({ games: games.data.items, gameTotal: games.data.total, candidates: candidates.data.items, candidateTotal: candidates.data.total, tags: tags.data.items, roots: roots.data.items });
    } catch (cause) { setError(cause instanceof Error ? cause.message : "无法连接本地后台服务"); }
    finally { setLoading(false); }
  }, []);

  useEffect(() => { void refresh(); }, [refresh]);

  const startScan = useCallback(async () => {
    const root = snapshot.roots[0]?.path;
    if (!root) throw new Error("请先添加游戏库目录");
    const result = await operation<{ jobId: string }>("scan.start", { root, idempotencyKey: `tauri-scan-${Date.now()}` });
    setScanJobId(result.jobId ?? result.data.jobId); setScanText("已请求扫描");
  }, [snapshot.roots]);

  const cancelScan = useCallback(async () => { if (scanJobId) { await operation("scan.cancel", { jobId: scanJobId }); setScanText("正在取消扫描…"); } }, [scanJobId]);

  useEffect(() => {
    if (!scanJobId) return;
    const timer = window.setInterval(async () => {
      try {
        const result = await operation<{ state: string; coverage?: { scannedDirectories: number; candidatesFound: number } }>("scan.coverage", { jobId: scanJobId });
        const coverage = result.data.coverage;
        if (coverage) setScanText(`扫描中 · ${coverage.scannedDirectories.toLocaleString()} 个目录 · ${coverage.candidatesFound.toLocaleString()} 个候选`);
        if (["succeeded", "failed", "cancelled"].includes(result.data.state)) { window.clearInterval(timer); setScanJobId(null); await refresh(); }
      } catch { /* durable refresh handles the next state */ }
    }, 500);
    return () => window.clearInterval(timer);
  }, [scanJobId, refresh]);

  const reviewCandidate = useCallback(async (candidate: CandidateItem, action: "accept" | "defer" | "ignore") => { await operation(`candidates.${action}`, { candidateId: candidate.candidateId, expectedRevision: candidate.revision, idempotencyKey: `tauri-${action}-${candidate.candidateId}-${Date.now()}` }); }, []);
  const reviewCandidates = useCallback(async (candidates: CandidateItem[], action: "accept" | "defer" | "ignore") => { for (const candidate of candidates) await reviewCandidate(candidate, action); await refresh(); }, [refresh, reviewCandidate]);
  const launch = useCallback(async (gameId: string) => {
    const profiles = await operation<{ items: Array<{ profileId: string; isDefault: boolean }> }>("profiles.list", { gameId });
    const profile = profiles.data.items.find(item => item.isDefault) ?? profiles.data.items[0];
    if (!profile) throw new Error("尚未配置启动方式，请先配置原始游戏 EXE");
    return operation("launch.execute", { profileId: profile.profileId, idempotencyKey: `tauri-play-${gameId}-${Date.now()}` });
  }, []);

  return { snapshot, loading, error, scanJobId, scanText, refresh, startScan, cancelScan, reviewCandidates, launch };
}

export type LibraryHook = ReturnType<typeof useLibraryController>;

const LibraryContext = createContext<LibraryHook | null>(null);

export function LibraryProvider({ children }: PropsWithChildren) {
  return createElement(LibraryContext.Provider, { value: useLibraryController() }, children);
}

export function useLibrary() {
  const value = useContext(LibraryContext);
  if (!value) throw new Error("useLibrary 必须在 LibraryProvider 内使用");
  return value;
}

export function useFilteredGames(games: GameItem[], search: string, view: string, tagId: string) {
  return useMemo(() => games.filter(game => {
    const text = `${game.title} ${game.rootPath}`.toLowerCase();
    if (search && !text.includes(search.toLowerCase())) return false;
    if (view === "favorites" && !game.favorite) return false;
    if (tagId && !(game.tags ?? []).some(tag => tag.tagId === tagId)) return false;
    return true;
  }), [games, search, tagId, view]);
}
