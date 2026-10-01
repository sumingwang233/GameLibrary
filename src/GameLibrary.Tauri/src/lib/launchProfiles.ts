import { operation } from "./api";
import { t } from "./i18n";
import type { ProfileItem } from "./types";

export interface ProfileList { items: ProfileItem[]; recommendedProfileId?: string | null; }

export class LaunchProfileSelectionRequired extends Error {
  constructor() { super(t("请在启动方式中选择一个建议入口试运行")); }
}

export function selectLaunchProfile(list: ProfileList, explicitId?: string) {
  const profile = explicitId ? list.items.find(item => item.profileId === explicitId)
    : list.items.find(item => item.isDefault)
      ?? list.items.find(item => item.profileId === list.recommendedProfileId)
      ?? (list.recommendedProfileId === undefined ? list.items[0] : undefined);
  return profile && !["discarded", "deleted", "verifying"].includes(profile.validationStatus ?? "") ? profile : undefined;
}

export async function discoverLaunchProfiles(gameId: string) {
  const started = await operation<unknown>("profiles.discover", { gameId }, `profiles.discover:${gameId}`);
  if (!started.jobId) return;
  for (let count = 0; count < 120; count++) {
    const job = await operation<{ state: string; error?: string }>("jobs.get", { jobId: started.jobId });
    if (job.data.state === "succeeded") return;
    if (["failed", "cancelled"].includes(job.data.state)) throw new Error(job.data.error ?? t("识别启动方式失败"));
    await new Promise(resolve => setTimeout(resolve, 250));
  }
  throw new Error(t("识别仍在后台进行，请稍后重试"));
}
