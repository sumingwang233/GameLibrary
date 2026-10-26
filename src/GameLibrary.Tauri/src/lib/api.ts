import { t } from "./i18n";
import { invoke } from "@tauri-apps/api/core";
import type { Envelope } from "./types";
import { DataUrlCache } from "./assetCache";

const assetCache = new DataUrlCache();
let libraryIdentity: string | undefined;
let libraryGeneration = 0;
export function invalidateAssets(assetId?: string) { assetCache.clear(assetId); }

export function invalidateAssetChanges(data: unknown) {
  if (!data || typeof data !== "object") return;
  const changes = data as Record<string, unknown>;
  // 导入和裁切创建新资产；只淘汰回执中明确受影响的资产，库切换仍全量失效。
  for (const key of ["assetId", "newAssetId", "sourceAssetId", "previousAssetId"])
    if (typeof changes[key] === "string") invalidateAssets(changes[key]);
}

declare global {
  interface Window {
    __TAURI__?: { core?: { invoke: typeof invoke } };
  }
}

let requestCounter = 0;

/**
 * 后端为每个 (actor, operationId, idempotencyKey) 保留幂等收据，同键重放返回原结果
 * （OperationDispatcher 的收据中间件）。因此键必须按「用户意图」稳定，而不是按调用时刻生成：
 * 用 Date.now() 时双击与重试都会拿到新键，收据机制形同虚设。
 *
 * 但收据对业务失败也是终态——若一直复用同一个键，用户修正输入后重试会永远拿回旧的失败结果。
 * 所以这里的策略是：传输层失败（invoke 抛错，请求可能没到 Host）保留键以便重试命中重放；
 * 一旦收到任何 Envelope（成功或业务失败）立即释放键。
 */
const pendingIntents = new Map<string, string>();

/**
 * 稳定序列化：对象键递归排序，数组保持顺序（位置参数有语义）。
 * undefined 与缺失字段等价（本来也不会进 JSON），null 单独保留。
 */
function stableStringify(value: unknown): string {
  if (value === undefined) return "undefined";
  if (value === null) return "null";
  if (Array.isArray(value)) return `[${value.map(stableStringify).join(",")}]`;
  if (typeof value === "object") {
    const record = value as Record<string, unknown>;
    const body = Object.keys(record)
      .filter((key) => record[key] !== undefined)
      .sort()
      .map((key) => `${JSON.stringify(key)}:${stableStringify(record[key])}`)
      .join(",");
    return `{${body}}`;
  }
  return JSON.stringify(value) ?? String(value);
}

/**
 * 幂等键必须同时绑定 intent 与参数指纹：只按 intent 时，同一个 intent 换一组参数
 * 重试会命中上一组参数的收据（例如批量翻译 5 个后再重试 2 个，后端直接重放 5 个的结果）。
 * idempotencyKey 由本模块注入，不能参与指纹，否则每次算出的键都不同。
 */
function intentSignature(intent: string, parameters?: Record<string, unknown>): string {
  const rest = { ...(parameters ?? {}) };
  delete rest.idempotencyKey;
  return `${intent}\u0000${stableStringify(rest)}`;
}

function acquireKey(signature: string | null): string | null {
  if (!signature) return null;
  const existing = pendingIntents.get(signature);
  if (existing) return existing;
  const key = crypto.randomUUID();
  pendingIntents.set(signature, key);
  return key;
}

function releaseKey(signature: string | null) {
  if (signature) pendingIntents.delete(signature);
}

/** 业务失败：已收到 Envelope，可读取 nextActions 给用户结构化引导。 */
export class OperationError extends Error {
  readonly code: string;
  readonly envelope: Envelope<unknown>;

  constructor(code: string, message: string, envelope: Envelope<unknown>) {
    super(message);
    this.name = "OperationError";
    this.code = code;
    this.envelope = envelope;
  }
}

/** 传输层失败：未收到 Envelope，intent 对应的幂等键被保留，重试可命中后端收据重放。 */
export class TransportError extends Error {
  constructor(message: string, readonly cause?: unknown) {
    super(message);
    this.name = "TransportError";
  }
}

export async function operation<T>(
  operationId: string,
  parameters?: Record<string, unknown>,
  intent?: string,
): Promise<Envelope<T>> {
  const generation = libraryGeneration;
  const requestId = `tauri-${Date.now()}-${++requestCounter}`;
  const signature = intent ? intentSignature(intent, parameters) : null;
  const key = acquireKey(signature);
  const payload: Record<string, unknown> = { ...(parameters ?? {}) };
  if (key) payload.idempotencyKey = key;

  const tauriInvoke = window.__TAURI__?.core?.invoke ?? invoke;
  let result: Envelope<T>;
  try {
    result = await tauriInvoke<Envelope<T>>("bridge_request", {
      request: { requestId, operationId, parameters: payload },
    });
  } catch (cause) {
    throw new TransportError(
      cause instanceof Error ? cause.message : t("无法连接本地后台服务"),
      cause,
    );
  }

  const identity = result.libraryInstanceId && result.dataEpoch
    ? result.libraryInstanceId + ":" + result.dataEpoch : undefined;
  if (identity && identity !== libraryIdentity) {
    if (generation !== libraryGeneration && libraryIdentity !== undefined)
      throw new TransportError(t("库已切换，已丢弃旧响应"));
    libraryGeneration++;
    if (libraryIdentity !== undefined) invalidateAssets();
    libraryIdentity = identity;
  }
  if (result.ok && operationId.startsWith("assets.") && !["assets.get", "assets.list"].includes(operationId))
    invalidateAssetChanges(result.data);
  releaseKey(signature);
  if (!result.ok) {
    throw new OperationError(
      result.error?.code ?? "OperationFailed",
      result.error?.message ?? t("操作失败"),
      result as Envelope<unknown>,
    );
  }

  // 泛型只是编译期断言；跨进程返回值必须在进入渲染状态前校验。
  if (operationId === "games.get") validateGame(result.data);
  if (operationId === "games.list") {
    const data = result.data as { items?: unknown[] };
    if (!Array.isArray(data?.items)) throw new Error(t("游戏列表数据不完整，请刷新后重试"));
    data.items.forEach(validateGame);
  }

  return result;
}

function validateGame(value: unknown) {
  const game = value as Record<string, unknown> | null;
  if (!game || typeof game.gameId !== "string" || typeof game.title !== "string"
    || typeof game.rootPath !== "string" || typeof game.kind !== "string"
    || typeof game.favorite !== "boolean" || !Number.isInteger(game.revision)) {
    throw new Error(t("游戏详情数据不完整，请刷新后重试"));
  }
}

export function assetDataUrl(assetId: string) {
  return assetCache.load(assetId, async () => {
    const result = await operation<{ mimeType: string; dataBase64: string }>("assets.get", { assetId });
    return `data:${result.data.mimeType};base64,${result.data.dataBase64}`;
  });
}

/**
 * 置前主窗口（feat-2：launch.exited 事件到达时调用；托盘"显示"/单实例唤醒共用
 * lib.rs show_main_window 同一实现）。浏览器开发环境没有窗口概念，静默忽略。
 */
export async function showMainWindow(): Promise<void> {
  try {
    const tauriInvoke = window.__TAURI__?.core?.invoke ?? invoke;
    await tauriInvoke("show_main_window");
  } catch {
    // 非 Tauri 运行环境（vite dev）或命令未注册：不打断事件轮询主流程。
  }
}

/** 把后端 nextActions 渲染成给用户看的一句话，避免只抛一个错误码。 */
export function describeFailure(cause: unknown): string {
  if (cause instanceof OperationError) {
    const next = cause.envelope.nextActions;
    if (next && next.length > 0) {
      return t("{0}（建议：{1}）", cause.message, next.map(action => action.reason).join("；"));
    }
    return cause.message;
  }
  return cause instanceof Error ? cause.message : t("操作失败");
}
