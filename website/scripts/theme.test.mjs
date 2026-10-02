import assert from "node:assert/strict";
import { test } from "node:test";
import { runInNewContext } from "node:vm";
import { themeBootstrap, themeKey } from "../lib/theme.ts";

function boot(saved, systemDark, storageBlocked = false) {
  const classes = new Set(["dark"]);
  const root = { classList: { toggle: (name, enabled) => enabled ? classes.add(name) : classes.delete(name) }, dataset: {}, style: {} };
  const meta = {};
  runInNewContext(themeBootstrap, {
    document: { documentElement: root, querySelector: () => ({ setAttribute: (name, value) => { meta[name] = value; } }) },
    localStorage: { getItem: key => { assert.equal(key, themeKey); if (storageBlocked) throw new Error("Blocked"); return saved; } },
    matchMedia: () => ({ matches: systemDark }),
  });
  return { root, meta, dark: classes.has("dark") };
}

test("explicit themes override the system; system follows both preferences", () => {
  for (const [mode, osDark, dark] of [["dark", false, true], ["light", true, false], ["system", false, false], ["system", true, true]]) {
    const result = boot(mode, osDark);
    assert.equal(result.root.dataset.theme, mode);
    assert.equal(result.dark, dark);
    assert.equal(result.root.style.colorScheme, dark ? "dark" : "light");
    assert.equal(result.meta.content, dark ? "#151c21" : "#f4faff");
  }
});

test("new visitors, invalid values, and blocked storage use light even on a dark system", () => {
  for (const [saved, blocked] of [[null, false], ["unexpected", false], ["dark", true]]) {
    assert.equal(boot(saved, false, blocked).root.dataset.theme, "light");
    assert.equal(boot(saved, false, blocked).dark, false);
    assert.equal(boot(saved, true, blocked).dark, false);
  }
});
