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

  const refreshDomains = useCallback(async (domains: readonly DataDomain[]) => {
    const version = ++refreshVersion.current;
    if (alive.current) setMetaLoading(true);
    try {
      await initialize();
      await Promise.all([...new Set(domains)].map(async domain => {
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
          case "candidates": {
            const result = await operation<{ total: number; items: CandidateItem[] }>("candidates.list",
              { state: "pendingReview", limit: 1000, offset: 0 });
            commit(() => { setCandidateTotal(result.data.total); setCandidates(result.data.items); }); break;
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
  }, [initialize]);
  const refreshMeta = useCallback(() => refreshDomains(ALL_DOMAINS), [refreshDomains]);
  return { tags, roots, views, notifications, candidates, gameTotal, candidateTotal,
    metaLoading, error, setError, refreshMeta, refreshDomains, initialize, supports };
}
