import { t } from "../i18n";
import { useCallback, useEffect, useRef, useState } from "react";
import { describeFailure, operation } from "../api";
import type { RootItem } from "../types";
import type { ScanProgressState } from "../state";
import {
  createPollFailureTracker,
  isPermanentPollFailure,
  isTerminalJobState,
  pollFailureMessage,
  shouldStopPolling,
} from "./useJobPolling";

const SCAN_POLL_MS = 700;
interface ScanJob { jobId: string; root: string; }
interface ScanCoverage {
  state: string;
  coverage?: { scannedDirectories: number; candidatesFound: number };
}
interface CoverageAnswer { job: ScanJob; coverage: ScanCoverage; error: null; }
interface CoverageFailure { job: ScanJob; coverage: null; error: unknown; }
type CoverageResult = CoverageAnswer | CoverageFailure;

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
  }, [roots, setError]);

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
    // 每个轮询循环独立计数：新一轮扫描重新获得 5 次容忍额度。
    const pollFailures = createPollFailureTracker();
    const timer = window.setInterval(() => {
      if (inFlight) return;
      inFlight = true;
      void (async () => {
        const results: CoverageResult[] = await Promise.all(
          jobs.map(async (job) => {
            try {
              const coverage = await operation<ScanCoverage>("scan.coverage", { jobId: job.jobId });
              return { job, coverage: coverage.data, error: null };
            } catch (cause) {
              return { job, coverage: null, error: cause };
            }
          }),
        );

        if (cancelled || !mounted.current) return;

        // NotFound＝该作业已被 Host 丢弃（后台重启/作业被清理）。只移除这一个作业，
        // 不能像以前那样记成失败、下一拍再问同一个已不存在的 jobId。
        const gone = new Set(results
          .filter((item): item is CoverageFailure => item.error !== null && isPermanentPollFailure(item.error))
          .map((item) => item.job.jobId));
        const remaining = jobs.filter((job) => !gone.has(job.jobId));
        const answers = results.filter((item): item is CoverageAnswer => item.error === null);

        const finish = async () => {
          window.clearInterval(timer);
          setJobs([]);
          setScanProgress(null);
          await refreshMeta();
          bump();
        };

        // 所有作业都消失（全部 NotFound，或 NotFound 与已完成混合）：与正常完成同一收尾。
        if (remaining.length === 0) {
          await finish();
          return;
        }
        if (gone.size > 0) setJobs(remaining);

        const terminalCount = answers.filter((item) => isTerminalJobState(item.coverage.state)).length;
        const running = answers.length - terminalCount;
        // 剩下的作业本轮全部应答且都已终态 ⇒ 扫描结束。
        if (answers.length === remaining.length && running === 0) {
          await finish();
          return;
        }

        if (answers.length === 0) {
          // 整轮没有任何作业应答才算一次连续失败，避免多根扫描把容忍次数放大。
          // 只统计仍在轮询的作业：同一拍里已 NotFound 的作业不参与分类，否则
          // 「一个作业消失 + 其它作业暂时失败」会被误判成 permanent 并提前清掉进度。
          const kind = pollFailures.recordFailure(
            results.find((item) => item.error !== null && !gone.has(item.job.jobId))?.error,
          );
          if (shouldStopPolling(kind)) {
            // 停轮询并给可读提示；清空进度条与作业，避免下次 render 重新起一个定时器。
            window.clearInterval(timer);
            setJobs([]);
            setScanProgress(null);
            setError(pollFailureMessage(kind === "permanent" ? "permanent" : "exhausted"));
          }
          // 未耗尽时保留上一帧进度，等待下一拍恢复。
          return;
        }
        pollFailures.recordSuccess();

        // 已完成的目录树仍计入累计数，避免多根扫描时某一根先结束后数字反向跳小。
        const directories = answers.reduce(
          (sum, item) => sum + (item.coverage.coverage?.scannedDirectories ?? 0),
          0,
        );
        const found = answers.reduce(
          (sum, item) => sum + (item.coverage.coverage?.candidatesFound ?? 0),
          0,
        );
        setScanProgress((previous) => ({
          phase: previous?.phase === "cancelling" ? "cancelling" : "running",
          activeRoots: remaining.length - terminalCount,
          scannedDirectories: Math.max(previous?.scannedDirectories ?? 0, directories),
          candidatesFound: Math.max(previous?.candidatesFound ?? 0, found),
          startedAt: previous?.startedAt ?? Date.now(),
        }));
      })().finally(() => { inFlight = false; });
    }, SCAN_POLL_MS);
    return () => { cancelled = true; window.clearInterval(timer); };
  }, [jobs, refreshMeta, bump, setError]);


  return { startScan, cancelScan, scanning: jobs.length > 0, scanProgress };
}
