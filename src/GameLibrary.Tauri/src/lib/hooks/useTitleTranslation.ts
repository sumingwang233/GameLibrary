import { useCallback, useEffect, useRef, useState } from "react";
import { describeFailure, operation } from "../api";
import type { GameItem } from "../types";

export type TitlePatch = Partial<GameItem> & Pick<GameItem, "gameId" | "revision">;
export interface TitleProgress {
  total: number; completed: number; succeeded: number; failed: number; skipped: number; pending: number;
  items: { gameId: string; status: string; reason?: string | null; provider?: string | null; patch?: TitlePatch | null }[];
  unprocessedGameIds: string[];
  stopReason?: string | null;
}
export function retryTitleIds(progress: TitleProgress): string[] {
  return [...new Set([...progress.items.filter(item => item.status === "failed" || item.reason === "revisionConflict").map(item => item.gameId), ...progress.unprocessedGameIds])];
}
export function useTitleTranslation(onPatch: (patch: TitlePatch) => void, onFinished: () => Promise<void>) {
  const [busy, setBusy] = useState(false);
  const [progress, setProgress] = useState<TitleProgress | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [state, setState] = useState("");
  const callbacks = useRef({ onPatch, onFinished });
  callbacks.current = { onPatch, onFinished };
  const mounted = useRef(true);
  const running = useRef(false);
  const jobId = useRef<string | null>(null);
  const forceRef = useRef(false);
  useEffect(() => { mounted.current = true; return () => { mounted.current = false; }; }, []);
  const start = useCallback(async (gameIds: string[], force = false) => {
    if (running.current || gameIds.length === 0) return;
    running.current = true; forceRef.current = force;
    setBusy(true); setError(null); setProgress(null); setState("running");
    const patched = new Set<string>();
    try {
      const accepted = await operation<{ jobId: string }>("titles.translate", { gameIds, force }, `titles.translate:${gameIds.join(",")}:${force}`);
      jobId.current = accepted.jobId ?? accepted.data.jobId;
      while (mounted.current) {
        try {
          const result = await operation<{ state: string; result: TitleProgress | null }>("jobs.get", { jobId: jobId.current });
          if (!mounted.current) return;
          setError(null); setState(result.data.state);
          if (result.data.result) {
            setProgress(result.data.result);
            for (const item of result.data.result.items) {
              if (item.patch && !patched.has(item.gameId)) { callbacks.current.onPatch(item.patch); patched.add(item.gameId); }
            }
          }
          if (["succeeded", "failed", "cancelled"].includes(result.data.state)) break;
        } catch (cause) { if (!mounted.current) return; setError(describeFailure(cause)); }
        await new Promise(resolve => window.setTimeout(resolve, 500));
      }
      if (mounted.current) await callbacks.current.onFinished();
    } catch (cause) { if (mounted.current) setError(describeFailure(cause)); }
    finally { jobId.current = null; running.current = false; if (mounted.current) setBusy(false); }
  }, []);
  const cancel = useCallback(async () => {
    if (!jobId.current) return;
    try { await operation("jobs.cancel", { jobId: jobId.current }, `jobs.cancel:${jobId.current}`); setState("cancelRequested"); }
    catch (cause) { setError(describeFailure(cause)); }
  }, []);
  return { busy, progress, error, state, start, cancel,
    retry: () => progress ? start(retryTitleIds(progress), forceRef.current) : Promise.resolve() };
}
export type TitleTranslationController = ReturnType<typeof useTitleTranslation>;
