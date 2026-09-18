import { invoke } from "@tauri-apps/api/core";
import type { Envelope } from "./types";

declare global {
  interface Window {
    __TAURI__?: { core?: { invoke: typeof invoke } };
  }
}

let requestCounter = 0;

export async function operation<T>(operationId: string, parameters?: Record<string, unknown>): Promise<Envelope<T>> {
  const requestId = `tauri-${Date.now()}-${++requestCounter}`;
  const tauriInvoke = window.__TAURI__?.core?.invoke ?? invoke;
  const result = await tauriInvoke<Envelope<T>>("bridge_request", { request: { requestId, operationId, parameters: parameters ?? {} } });
  if (!result.ok) throw new Error(`${result.error?.code ?? "OperationFailed"}: ${result.error?.message ?? "操作失败"}`);
  return result;
}

export async function assetDataUrl(assetId: string) {
  const result = await operation<{ mimeType: string; dataBase64: string }>("assets.get", { assetId });
  return `data:${result.data.mimeType};base64,${result.data.dataBase64}`;
}
