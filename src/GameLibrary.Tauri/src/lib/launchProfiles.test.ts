import { describe, expect, it } from "vitest";
import { selectLaunchProfile } from "./launchProfiles";
import type { ProfileItem } from "./types";

const profile = (profileId: string, changes: Partial<ProfileItem> = {}): ProfileItem =>
  ({ profileId, executablePath: "game.exe", isDefault: false, revision: 1, ...changes });

describe("launch profile selection", () => {
  it("preserves the default, then uses the backend recommendation", () => {
    const items = [profile("a"), profile("b", { isDefault: true }), profile("c")];
    expect(selectLaunchProfile({ items, recommendedProfileId: "c" })?.profileId).toBe("b");
    expect(selectLaunchProfile({ items: [items[0], items[2]], recommendedProfileId: "c" })?.profileId).toBe("c");
  });
  it("requires selection for ambiguous suggestions and blocks discarded entries", () => {
    const items = [profile("a"), profile("b", { validationStatus: "discarded" })];
    expect(selectLaunchProfile({ items, recommendedProfileId: null })).toBeUndefined();
    expect(selectLaunchProfile({ items }, "a")?.profileId).toBe("a");
    expect(selectLaunchProfile({ items }, "b")).toBeUndefined();
  });
  it("supports the old Host manual workflow", () => {
    expect(selectLaunchProfile({ items: [profile("a")] })?.profileId).toBe("a");
  });
});
