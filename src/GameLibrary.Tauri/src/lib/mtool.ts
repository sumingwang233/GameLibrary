import { operation, OperationError } from "./api";
import type { LaunchPlan } from "./types";

/** plan 保持只读；用户启动时显式生成，再对同一个入口重新 plan。 */
export async function planWithMTool(gameId: string, profileId: string, supported: boolean) {
  try {
    return await operation<LaunchPlan>("launch.plan", { gameId, profileId });
  } catch (cause) {
    if (!supported || !(cause instanceof OperationError) || cause.code !== "TranslationRouteUnavailable"
      || !cause.envelope.nextActions?.some(action => action.operationId === "mtool.prepare")) throw cause;
    await operation("mtool.prepare", { gameId, profileId }, `mtool.prepare:${gameId}:${profileId}`);
    return operation<LaunchPlan>("launch.plan", { gameId, profileId });
  }
}
