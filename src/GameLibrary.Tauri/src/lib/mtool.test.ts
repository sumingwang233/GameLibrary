import { afterEach, expect, it, vi } from "vitest";
import { operation, OperationError } from "./api";
import { planWithMTool } from "./mtool";

vi.mock("./api", async original => ({ ...await original<typeof import("./api")>(), operation: vi.fn() }));
afterEach(() => vi.mocked(operation).mockReset());
const blocked = (operationId = "mtool.prepare") => new OperationError("TranslationRouteUnavailable", "missing recipe", {
  ok: false, requestId: "plan", status: "needsUserAction", data: null, nextActions: [{ operationId, reason: "configure" }],
});

it("prepares once then replans the same selected entry before execution", async () => {
  vi.mocked(operation).mockRejectedValueOnce(blocked()).mockResolvedValueOnce({ data: { state: "ready" } } as never)
    .mockResolvedValueOnce({ data: { planId: "new-plan" } } as never);
  expect((await planWithMTool("game", "selected", true)).data.planId).toBe("new-plan");
  expect(vi.mocked(operation).mock.calls).toEqual([
    ["launch.plan", { gameId: "game", profileId: "selected" }],
    ["mtool.prepare", { gameId: "game", profileId: "selected" }, "mtool.prepare:game:selected"],
    ["launch.plan", { gameId: "game", profileId: "selected" }],
  ]);
});

it("stops on generation failure and does not fall back to direct game launch", async () => {
  const failure = new Error("cache missing");
  vi.mocked(operation).mockRejectedValueOnce(blocked()).mockRejectedValueOnce(failure);
  await expect(planWithMTool("game", "profile", true)).rejects.toBe(failure);
  expect(operation).toHaveBeenCalledTimes(2);
});

it.each([[true, "tools.discover"], [false, "mtool.prepare"]])("does not prepare Unity or unsupported hosts", async (supported, nextAction) => {
  const failure = blocked(nextAction);
  vi.mocked(operation).mockRejectedValueOnce(failure);
  await expect(planWithMTool("game", "profile", supported)).rejects.toBe(failure);
  expect(operation).toHaveBeenCalledTimes(1);
});
