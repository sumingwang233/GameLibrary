import { useEffect, useRef } from "react";
import { invalidateAssets, operation, OperationError, showMainWindow } from "../api";
import type { LibraryEvent } from "../types";
import type { LaunchExitNotice } from "../state";
import { ALL_DOMAINS, type DataDomain } from "./useLibraryMetadata";

export function affectedDomains(events: readonly LibraryEvent[]): Set<DataDomain> {
  const domains = new Set<DataDomain>();
  for (const event of events) {
    const prefix = event.type.split(".")[0];
    if (prefix === "game" || prefix === "asset" || prefix === "profile" || event.type === "launch.exited") domains.add("games");
    if (prefix === "candidate" || prefix === "scan") domains.add("candidates");
    if (prefix === "notification" || prefix === "scan" || prefix === "candidate") domains.add("notifications");
    if (prefix === "tag") { domains.add("tags"); domains.add("games"); }
    if (prefix === "root") { domains.add("roots"); domains.add("games"); domains.add("candidates"); }
    if (prefix === "view") domains.add("views");
    if (prefix === "settings") for (const domain of ALL_DOMAINS) domains.add(domain);
  }
  return domains;
}

export function useLibraryEvents(
  initialize: () => Promise<void>, supports: (name: string) => boolean,
  refreshDomains: (domains: readonly DataDomain[]) => Promise<void>, bump: () => void,
  setLaunchExit: (notice: LaunchExitNotice) => void,
) {
  const seen = useRef(new Set<string>());
  const nonce = useRef(0);
  useEffect(() => {
    let cancelled = false;
    let cursor: number | undefined;
    let pollTimer: ReturnType<typeof setTimeout> | undefined;
    let refreshTimer: ReturnType<typeof setTimeout> | undefined;
    const pending = new Set<DataDomain>();
    const read = async (baseline = false) => {
      const result = await operation<{ nextCursor: number; latestCursor?: number; items: LibraryEvent[] }>(
        !baseline && supports("events.wait") ? "events.wait" : "events.read",
        { cursor, limit: baseline ? 1 : 4096, ...(!baseline && supports("events.wait") ? { timeoutMs: 25000 } : {}) });
      if (cancelled) return null;
      cursor = baseline ? result.data.latestCursor ?? result.data.nextCursor : result.data.nextCursor;
      return result.data;
    };
    const baseline = async () => {
      cursor = undefined;
      await initialize();
      await read(true);
      if (!cancelled) window.dispatchEvent(new Event("gamelibrary-settings-changed"));
      if (!cancelled) { await refreshDomains(ALL_DOMAINS); if (!cancelled) bump(); }
    };
    const flush = () => {
      refreshTimer = undefined;
      const domains = [...pending];
      pending.clear();
      if (cancelled) return;
      if (domains.includes("games")) bump();
      void refreshDomains(domains);
    };
    const poll = async () => {
      let failed = false;
      try {
        const batch = await read();
        if (!batch || cancelled) return;
        if (batch.items.some(event => event.type.startsWith("settings.")))
          window.dispatchEvent(new Event("gamelibrary-settings-changed"));
        if (batch.items.some(event => event.type.startsWith("unity_translation.")))
          window.dispatchEvent(new Event("gamelibrary-unity-changed"));
        if (batch.items.some(event => event.type.startsWith("asset."))) invalidateAssets();
        for (const event of batch.items) {
          if (event.type !== "launch.exited") continue;
          const payload = event.payload as { attemptId?: string; gameId?: string } | undefined;
          if (typeof payload?.gameId !== "string") continue;
          const id = typeof payload.attemptId === "string" ? payload.attemptId : `seq:${event.sequence}`;
          if (seen.current.has(id)) continue;
          seen.current.add(id);
          if (seen.current.size > 1024) seen.current = new Set([...seen.current].slice(-512));
          setLaunchExit({ attemptId: id, gameId: payload.gameId, nonce: ++nonce.current });
          void showMainWindow();
        }
        for (const domain of affectedDomains(batch.items)) pending.add(domain);
        if (pending.size && refreshTimer === undefined) refreshTimer = setTimeout(flush, 250);
      } catch (cause) {
        failed = true;
        if (cause instanceof OperationError && ["CursorExpired", "DataEpochMismatch", "LibraryInstanceMismatch"].includes(cause.code)) {
          invalidateAssets();
          await baseline();
        }
      } finally {
        if (!cancelled) pollTimer = setTimeout(() => void poll(), supports("events.wait") && !failed ? 0 : 2500);
      }
    };
    void baseline().catch(() => undefined).finally(() => { if (!cancelled) void poll(); });
    return () => {
      cancelled = true;
      clearTimeout(pollTimer);
      clearTimeout(refreshTimer);
      pending.clear();
    };
  }, [initialize, supports, refreshDomains, bump, setLaunchExit]);
}
