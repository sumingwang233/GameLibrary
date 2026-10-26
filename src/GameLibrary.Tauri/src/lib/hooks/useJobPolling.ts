import { t } from "../i18n";

/**
 * 扫描与标题翻译共用的作业轮询契约。
 *
 * 背景：两处轮询此前把任何异常都当成「下一拍再试」——useScanJobs 每 700ms、
 * useTitleTranslation 每 500ms——一旦作业已被 Host 丢弃（OperationError.code ===
 * "NotFound"），循环永远不会结束：界面一直转圈，后端被永久地每 500/700ms 打一次。
 * 这里把「这次失败要不要继续」收敛成一处判断，两个调用点共用：
 *
 * - NotFound：作业已不存在（后台重启、作业被清理），永久失败，调用方立即结束；
 * - 其它异常：容忍连续 MAX_CONSECUTIVE_POLL_FAILURES 次，任一次成功即清零；
 * - 连续失败耗尽：停止轮询并给出可读中文提示，不再无上限重试。
 */
export const MAX_CONSECUTIVE_POLL_FAILURES = 5;
export const PERMANENT_POLL_ERROR_CODE = "NotFound";
export const TERMINAL_JOB_STATES = ["succeeded", "failed", "cancelled"] as const;

export type PollFailureKind = "transient" | "permanent" | "exhausted";

/** 作业是否已到终态：继续轮询也不会再变化。 */
export function isTerminalJobState(state: string | null | undefined): boolean {
  return typeof state === "string" && (TERMINAL_JOB_STATES as readonly string[]).includes(state);
}

/**
 * 用结构化判断而不是 `cause instanceof OperationError`：本模块被多个 hook 复用，
 * 测试里也常只 mock ../api 的 operation（没有导出 OperationError 类），
 * 跨模块副本时 instanceof 会失效，返回 false 反而把永久失败当成可重试。
 */
export function isPermanentPollFailure(cause: unknown): boolean {
  if (typeof cause !== "object" || cause === null) return false;
  return (cause as { code?: unknown }).code === PERMANENT_POLL_ERROR_CODE;
}

/** 停止轮询时给用户看的原因，避免只留一个永远不动的进度条。 */
export function pollFailureMessage(kind: "permanent" | "exhausted"): string {
  return kind === "permanent"
    ? t("后台作业已不存在，可能因后台服务重启而丢失，已停止刷新")
    : t("与后台服务的连接连续 {0} 次失败，已停止自动刷新，请稍后重试", MAX_CONSECUTIVE_POLL_FAILURES);
}

/** 这两类失败都是终止信号，transient 才是「下一拍再试」。 */
export function shouldStopPolling(kind: PollFailureKind): boolean {
  return kind !== "transient";
}

export interface PollFailureTracker {
  readonly consecutive: number;
  /** 任意一次成功即清零：偶发抖动不累积。 */
  recordSuccess(): void;
  /** 记录一次失败并给出处置；permanent/exhausted 由调用方停止轮询。 */
  recordFailure(cause: unknown): PollFailureKind;
  reset(): void;
}

/**
 * 连续失败计数器。每个轮询循环各自持有一个实例，不跨次 start 复用；
 * 扫描侧一轮里多个作业全部失败才算一次，避免多根扫描把容忍次数放大。
 */
export function createPollFailureTracker(max = MAX_CONSECUTIVE_POLL_FAILURES): PollFailureTracker {
  let consecutive = 0;
  return {
    get consecutive() { return consecutive; },
    recordSuccess() { consecutive = 0; },
    reset() { consecutive = 0; },
    recordFailure(cause) {
      if (isPermanentPollFailure(cause)) {
        consecutive = 0;
        return "permanent";
      }
      consecutive += 1;
      return consecutive >= max ? "exhausted" : "transient";
    },
  };
}
