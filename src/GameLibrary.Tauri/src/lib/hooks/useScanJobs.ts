import { t } from "../i18n";
import { useCallback, useEffect, useRef, useState } from "react";
import { describeFailure, operation } from "../api";
import type { RootItem } from "../types";
import type { ScanProgressState } from "../state";

const SCAN_POLL_MS = 700;
const TERMINAL_JOB_STATES = ["succeeded", "failed", "cancelled"];
interface ScanJob { jobId: string; root: string; }

export function useScanJobs(roots: RootItem[], refreshMeta: () => Promise<void>, bump: () => void,
  setError: (message: string) => void) {
  const [jobs, setJobs] = useState<ScanJob[]>([]);
  const [scanProgress, setScanProgress] = useState<ScanProgressState | null>(null);
  const mounted = useRef(true);
  useEffect(() => { mounted.current = true; return () => { mounted.current = false; }; }, []);
  const startScan = useCallback(async () => {
    if (roots.length === 0) {
      throw new Error(t("请先添加游戏库目录"));
    }

    // v1.1.5 只扫 roots[0]，多库根时其余静默不扫。这里逐个根启动并聚合进度。
    const started: ScanJob[] = [];
    const failures: string[] = [];
    for (const root of roots) {
      try {
        const result = await operation<{ jobId: string }>(
          "scan.start",
          { root: root.path },
          `scan.start:${root.rootId}`,
        );
        const jobId = result.jobId ?? result.data.jobId;
        if (jobId) started.push({ jobId, root: root.path });
      } catch (cause) {
        failures.push(`${root.path}: ${describeFailure(cause)}`);
      }
    }

    if (started.length === 0) {
      throw new Error(failures.join("；") || t("扫描未能启动"));
    }

    setJobs(started);
    setScanProgress({
      phase: "starting",
      activeRoots: started.length,
      scannedDirectories: 0,
      candidatesFound: 0,
      startedAt: Date.now(),
    });
    if (failures.length > 0) setError(failures.join("；"));
  }, [roots]);

  const cancelScan = useCallback(async () => {
    await Promise.all(
      jobs.map((job) =>
        operation("scan.cancel", { jobId: job.jobId }, `scan.cancel:${job.jobId}`).catch(
          () => undefined,
        ),
      ),
    );
    setScanProgress((previous) => previous && { ...previous, phase: "cancelling" });
  }, [jobs]);

  useEffect(() => {
    if (jobs.length === 0) return;
    let cancelled = false;
    let inFlight = false;
    const timer = window.setInterval(() => {
      if (inFlight) return;
      inFlight = true;
      void (async () => {
        const results = await Promise.all(
          jobs.map(async (job) => {
            try {
              const coverage = await operation<{
                state: string;
                coverage?: { scannedDirectories: number; candidatesFound: number };
              }>("scan.coverage", { jobId: job.jobId });
              return { job, ...coverage.data };
            } catch {
              return null;
            }
          }),
        );

        const alive = results.filter((item): item is NonNullable<typeof item> => item !== null);
        const running = alive.filter((item) => !TERMINAL_JOB_STATES.includes(item.state));
        const terminalCount = alive.length - running.length;

        if (cancelled || !mounted.current) return;

        if (alive.length === jobs.length && running.length === 0) {
          window.clearInterval(timer);
          setJobs([]);
          setScanProgress(null);
          await refreshMeta();
          bump();
          return;
        }

        // 单次 coverage 请求失败不代表扫描结束；保留上一帧，等待下次轮询恢复。
        if (alive.length === 0) return;

        // 已完成的目录树仍计入累计数，避免多根扫描时某一根先结束后数字反向跳小。
        const directories = alive.reduce(
          (sum, item) => sum + (item.coverage?.scannedDirectories ?? 0),
          0,
        );
        const found = alive.reduce((sum, item) => sum + (item.coverage?.candidatesFound ?? 0), 0);
        setScanProgress((previous) => ({
          phase: previous?.phase === "cancelling" ? "cancelling" : "running",
          activeRoots: jobs.length - terminalCount,
          scannedDirectories: Math.max(previous?.scannedDirectories ?? 0, directories),
          candidatesFound: Math.max(previous?.candidatesFound ?? 0, found),
          startedAt: previous?.startedAt ?? Date.now(),
        }));
      })().finally(() => { inFlight = false; });
    }, SCAN_POLL_MS);
    return () => { cancelled = true; window.clearInterval(timer); };
  }, [jobs, refreshMeta, bump]);


  return { startScan, cancelScan, scanning: jobs.length > 0, scanProgress };
}
