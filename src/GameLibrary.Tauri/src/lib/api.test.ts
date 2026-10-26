import { beforeEach, expect, it, vi } from "vitest";
import { operation, TransportError } from "./api";

const { invoke } = vi.hoisted(() => ({ invoke: vi.fn() }));
// operation() 在没有 window.__TAURI__ 时走静态 import 的 invoke（jsdom 下即此 mock）。
vi.mock("@tauri-apps/api/core", () => ({ invoke }));

const envelope = (data: unknown = {}) => ({ ok: true, data, requestId: "test", status: "completed" });

/** 取第 index 次 bridge_request 请求里实际发给后端的幂等键。 */
const keyOf = (index: number) => {
  const payload = invoke.mock.calls[index][1] as { request: { parameters: Record<string, unknown> } };
  return payload.request.parameters.idempotencyKey;
};

beforeEach(() => { invoke.mockReset(); });

it("reuses the same idempotency key for identical intent and parameters after a transport failure", async () => {
  invoke.mockRejectedValueOnce(new Error("offline")).mockResolvedValue(envelope());
  await expect(operation("jobs.get", { jobId: "job-1" }, "jobs.get:job-1")).rejects.toBeInstanceOf(TransportError);
  await operation("jobs.get", { jobId: "job-1" }, "jobs.get:job-1");
  expect(keyOf(0)).toBeTruthy();
  expect(keyOf(1)).toBe(keyOf(0));
});

it("scopes the key by the parameter fingerprint, not the intent alone", async () => {
  invoke.mockRejectedValueOnce(new Error("offline")).mockResolvedValue(envelope());
  await expect(operation("titles.translate", { gameIds: ["a"] }, "titles.translate:a"))
    .rejects.toBeInstanceOf(TransportError);
  // 同一 intent、不同参数：必须换新键，否则会重放上一组参数的收据（例如只翻译 2 个却拿回 5 个的结果）。
  await operation("titles.translate", { gameIds: ["a", "b"] }, "titles.translate:a,b");
  expect(keyOf(1)).not.toBe(keyOf(0));
});

it("ignores parameter key order when fingerprinting", async () => {
  invoke.mockRejectedValueOnce(new Error("offline")).mockResolvedValue(envelope());
  await expect(operation("jobs.get", { jobId: "job-9", timeoutSeconds: 5 }, "jobs.get:job-9"))
    .rejects.toBeInstanceOf(TransportError);
  await operation("jobs.get", { timeoutSeconds: 5, jobId: "job-9" }, "jobs.get:job-9");
  expect(keyOf(1)).toBe(keyOf(0));
});

it("releases the key as soon as an envelope arrives, so a corrected retry gets a fresh receipt", async () => {
  invoke.mockResolvedValueOnce(envelope());
  await operation("jobs.get", { jobId: "job-7" }, "jobs.get:job-7");
  const first = keyOf(0);
  invoke.mockResolvedValueOnce(envelope());
  await operation("jobs.get", { jobId: "job-7" }, "jobs.get:job-7");
  expect(keyOf(1)).not.toBe(first);
});
