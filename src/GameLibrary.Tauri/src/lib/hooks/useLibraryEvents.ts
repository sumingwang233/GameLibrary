import { useEffect, useRef } from "react";
import { invalidateAssetChanges, invalidateAssets, operation, OperationError, showMainWindow } from "../api";
import type { LibraryEvent } from "../types";
import type { LaunchExitNotice } from "../state";
import { ALL_DOMAINS, type DataDomain } from "./useLibraryMetadata";
import { watchWindowVisibility } from "../windowVisibility";

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
    let visible = true;
    let cursor: number | undefined;
    let pollTimer: ReturnType<typeof setTimeout> | undefined;
    let refreshTimer: ReturnType<typeof setTimeout> | undefined;
    const pending = new Set<DataDomain>();
    const notices = new Set<string>();
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
      if (!cancelled) {
        notices.add("gamelibrary-settings-changed");
        for (const domain of ALL_DOMAINS) pending.add(domain);
        await flush();
      }
    };
    const flush = async () => {
      refreshTimer = undefined;
      if (cancelled || !visible) return;
      const domains = [...pending];
      pending.clear();
      for (const notice of notices) window.dispatchEvent(new Event(notice));
      notices.clear();
      if (domains.includes("games")) bump();
      if (domains.length) await refreshDomains(domains);
    };
    const stopVisibility = watchWindowVisibility(value => {
      visible = value;
      clearTimeout(refreshTimer);
      refreshTimer = undefined;
      if (visible && (pending.size || notices.size)) void flush();
    });
    const poll = async () => {
      let failed = false;
      try {
        const batch = await read();
        if (!batch || cancelled) return;
        if (batch.items.some(event => event.type.startsWith("settings.")))
          notices.add("gamelibrary-settings-changed");
        if (batch.items.some(event => event.type.startsWith("unity_translation.")))
          notices.add("gamelibrary-unity-changed");
        if (batch.items.some(event => event.type.startsWith("mtool.")))
          notices.add("gamelibrary-mtool-changed");
        for (const event of batch.items)
          if (event.type.startsWith("asset.")) invalidateAssetChanges(event.payload);
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
        if (visible && (pending.size || notices.size) && refreshTimer === undefined)
          refreshTimer = setTimeout(() => void flush(), 250);
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
      stopVisibility();
      pending.clear();
      notices.clear();
    };
  }, [initialize, supports, refreshDomains, bump, setLaunchExit]);
}
