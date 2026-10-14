import { useCallback, useEffect, useRef, useState } from "react";
import { describeFailure, operation } from "../api";
import type { CandidateItem, NotificationItem, RootItem, TagItem, ViewItem } from "../types";

export type DataDomain = "games" | "candidates" | "tags" | "roots" | "views" | "notifications";
export const ALL_DOMAINS: DataDomain[] = ["games", "candidates", "tags", "roots", "views", "notifications"];

export function useLibraryMetadata() {
  const [tags, setTags] = useState<TagItem[]>([]);
  const [roots, setRoots] = useState<RootItem[]>([]);
  const [views, setViews] = useState<ViewItem[]>([]);
  const [notifications, setNotifications] = useState<NotificationItem[]>([]);
  const [candidates, setCandidates] = useState<CandidateItem[]>([]);
  const [gameTotal, setGameTotal] = useState(0);
  const [candidateTotal, setCandidateTotal] = useState(0);
  const [candidateHasMore, setCandidateHasMore] = useState(false);
  const [candidateLoading, setCandidateLoading] = useState(false);
  const candidatePage = useRef({ items: [] as CandidateItem[], offset: 0, hasMore: false });
  const candidateRequest = useRef<number | null>(null);
  const [metaLoading, setMetaLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const supported = useRef(new Set<string>());
  const alive = useRef(true);
  const serial = useRef(new Map<DataDomain, number>());
  const refreshVersion = useRef(0);
  const initialization = useRef<Promise<void> | undefined>(undefined);
  useEffect(() => {
    alive.current = true;
    return () => {
      alive.current = false;
      for (const domain of ALL_DOMAINS) serial.current.set(domain, (serial.current.get(domain) ?? 0) + 1);
    };
  }, []);
  const initialize = useCallback(async () => {
    if (!initialization.current) initialization.current = (async () => {
      const status = await operation<{ libraryInitialized: boolean }>("host.status");
      if (!status.data.libraryInitialized) await operation("library.init", {}, "library.init");
      try {
        const capabilities = await operation<{ availableOperations: string[] }>("capabilities.get");
        supported.current = new Set(capabilities.data.availableOperations);
      } catch { supported.current = new Set(); }
    })().catch(cause => { initialization.current = undefined; throw cause; });
    await initialization.current;
  }, []);
  const supports = useCallback((name: string) => supported.current.has(name), []);

  const readCandidates = useCallback(async (append: boolean) => {
    const version = (serial.current.get("candidates") ?? 0) + 1;
    serial.current.set("candidates", version);
    candidateRequest.current = version;
    if (alive.current) setCandidateLoading(true);
    const valid = () => alive.current && serial.current.get("candidates") === version;
    const page = candidatePage.current;
    const items = new Map((append ? page.items : []).map(item => [item.candidateId, item]));
    let offset = append ? page.offset : 0;
    const target = append ? offset + 1000 : Math.max(1000, page.offset);
    let total = 0, progressing = true;
    try {
      do {
        const result = await operation<{ total: number; items: CandidateItem[] }>("candidates.list",
          { state: "pendingReview", limit: Math.min(1000, target - offset), offset });
        if (!valid()) return;
        total = result.data.total;
        const before = items.size;
        for (const item of result.data.items) items.set(item.candidateId, item);
        offset += result.data.items.length;
        progressing = items.size > before;
      } while (progressing && offset < target && offset < total);
      const next = { items: [...items.values()], offset, hasMore: progressing && offset < total };
      candidatePage.current = next;
      setCandidates(next.items); setCandidateTotal(total); setCandidateHasMore(next.hasMore);
    } catch (cause) {
      if (valid()) throw cause;
    } finally {
      if (candidateRequest.current === version) {
        candidateRequest.current = null;
        if (alive.current) setCandidateLoading(false);
      }
    }
  }, []);

  const loadMoreCandidates = useCallback(async () => {
    if (candidateRequest.current !== null || !candidatePage.current.hasMore) return;
    try { await readCandidates(true); }
    catch (cause) { if (alive.current) setError(describeFailure(cause)); }
  }, [readCandidates]);

  const refreshDomains = useCallback(async (domains: readonly DataDomain[]) => {
    const version = ++refreshVersion.current;
    if (alive.current) setMetaLoading(true);
    try {
      await initialize();
      await Promise.all([...new Set(domains)].map(async domain => {
        if (domain === "candidates") { await readCandidates(false); return; }
        const version = (serial.current.get(domain) ?? 0) + 1;
        serial.current.set(domain, version);
        const commit = (action: () => void) => {
          if (alive.current && serial.current.get(domain) === version) action();
        };
        switch (domain) {
          case "games": {
            const result = await operation<{ total: number }>("games.list", { limit: 1, offset: 0 });
            commit(() => setGameTotal(result.data.total)); break;
          }
          case "tags": {
            const result = await operation<{ items: TagItem[] }>("tags.list");
            commit(() => setTags(result.data.items)); break;
          }
          case "roots": {
            const result = await operation<{ items: RootItem[] }>("roots.list");
            commit(() => setRoots(result.data.items)); break;
          }
          case "views": {
            const result = await operation<{ items: ViewItem[] }>("views.list");
            commit(() => setViews(result.data.items.filter(view => view.kind !== "builtin" && view.revision !== null))); break;
          }
          case "notifications": {
            const result = await operation<{ items: NotificationItem[] }>("notifications.list", { state: "pending" });
            commit(() => setNotifications(result.data.items)); break;
          }
        }
      }));
      if (alive.current && refreshVersion.current === version) setError(null);
    } catch (cause) {
      if (alive.current && refreshVersion.current === version) setError(describeFailure(cause));
    } finally {
      if (alive.current && refreshVersion.current === version) setMetaLoading(false);
    }
  }, [initialize, readCandidates]);
  const refreshMeta = useCallback(() => refreshDomains(ALL_DOMAINS), [refreshDomains]);
  return { tags, roots, views, notifications, candidates, gameTotal, candidateTotal,
    candidateLoading, candidateHasMore, loadMoreCandidates,
    metaLoading, error, setError, refreshMeta, refreshDomains, initialize, supports };
}
