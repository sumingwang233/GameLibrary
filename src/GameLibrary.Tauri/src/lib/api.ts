import { invoke } from "@tauri-apps/api/core";
import type { Envelope } from "./types";

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

function acquireKey(intent?: string): string | null {
  if (!intent) return null;
  const existing = pendingIntents.get(intent);
  if (existing) return existing;
  const key = crypto.randomUUID();
  pendingIntents.set(intent, key);
  return key;
}

function releaseKey(intent?: string) {
  if (intent) pendingIntents.delete(intent);
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
  const requestId = `tauri-${Date.now()}-${++requestCounter}`;
  const key = acquireKey(intent);
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
      cause instanceof Error ? cause.message : "无法连接本地后台服务",
      cause,
    );
  }

  releaseKey(intent);
  if (!result.ok) {
    throw new OperationError(
      result.error?.code ?? "OperationFailed",
      result.error?.message ?? "操作失败",
      result as Envelope<unknown>,
    );
  }

  // 泛型只是编译期断言；跨进程返回值必须在进入渲染状态前校验。
  if (operationId === "games.get") validateGame(result.data);
  if (operationId === "games.list") {
    const data = result.data as { items?: unknown[] };
    if (!Array.isArray(data?.items)) throw new Error("游戏列表数据不完整，请刷新后重试");
    data.items.forEach(validateGame);
  }

  return result;
}

function validateGame(value: unknown) {
  const game = value as Record<string, unknown> | null;
  if (!game || typeof game.gameId !== "string" || typeof game.title !== "string"
    || typeof game.rootPath !== "string" || typeof game.kind !== "string"
    || typeof game.favorite !== "boolean" || !Number.isInteger(game.revision)) {
    throw new Error("游戏详情数据不完整，请刷新后重试");
  }
}

const ASSET_CACHE_LIMIT = 48;
const assetCache = new Map<string, string>();
const assetPending = new Map<string, Promise<string>>();

export async function assetDataUrl(assetId: string) {
  const cached = assetCache.get(assetId);
  if (cached) {
    assetCache.delete(assetId);
    assetCache.set(assetId, cached);
    return cached;
  }
  const pending = assetPending.get(assetId);
  if (pending) return pending;
  const request = operation<{ mimeType: string; dataBase64: string }>("assets.get", { assetId })
    .then((result) => {
      const value = `data:${result.data.mimeType};base64,${result.data.dataBase64}`;
      assetCache.set(assetId, value);
      while (assetCache.size > ASSET_CACHE_LIMIT) assetCache.delete(assetCache.keys().next().value!);
      return value;
    })
    .finally(() => assetPending.delete(assetId));
  assetPending.set(assetId, request);
  return request;
}

/** 把后端 nextActions 渲染成给用户看的一句话，避免只抛一个错误码。 */
export function describeFailure(cause: unknown): string {
  if (cause instanceof OperationError) {
    const next = cause.envelope.nextActions;
    if (next && next.length > 0) {
      return `${cause.message}（建议：${next.map(action => action.reason).join("；")}）`;
    }
    return cause.message;
  }
  return cause instanceof Error ? cause.message : "操作失败";
}
