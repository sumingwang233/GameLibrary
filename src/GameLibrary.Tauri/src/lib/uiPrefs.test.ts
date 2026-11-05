import { act, renderHook } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { parseUiViewPrefs, useStoredViewPrefs } from "./uiPrefs";

const invoke = vi.hoisted(() => vi.fn());
vi.mock("@tauri-apps/api/core", () => ({ invoke }));

afterEach(() => { invoke.mockReset(); });

it("keeps only the toolbar sort and layout from ui.json", () => {
  expect(parseUiViewPrefs({ leftWidth: 290, sort: "title-asc", layout: "compact" })).toEqual({
    sort: "title-asc",
    layout: "compact",
  });
  expect(parseUiViewPrefs({ sort: "title", layout: "list", hasCover: false })).toEqual({});
  expect(parseUiViewPrefs(null)).toEqual({});
  expect(parseUiViewPrefs("title-asc")).toEqual({});
});

it("applies a stored sort once, and does not let a late read erase a newer choice", async () => {
  let finishLoad!: (value: unknown) => void;
  invoke.mockImplementation((command: string) => {
    if (command === "load_ui_prefs") return new Promise((resolve) => { finishLoad = resolve; });
    return Promise.resolve();
  });
  const hydrated = vi.fn();
  const { result } = renderHook(() => useStoredViewPrefs(hydrated));
  expect(result.current.ready).toBe(false);

  act(() => result.current.save({ sort: "title-desc" }));
  expect(invoke).toHaveBeenCalledWith("save_ui_prefs", { patch: { sort: "title-desc" } });

  await act(async () => { finishLoad({ sort: "accepted-desc", leftWidth: 290, layout: "compact" }); });
  expect(hydrated).not.toHaveBeenCalled();
  expect(result.current.ready).toBe(true);
});

it("hydrates sort and layout from ui.json before the library query starts", async () => {
  invoke.mockResolvedValue({ sort: "title-asc", layout: "compact", leftWidth: 320 });
  const hydrated = vi.fn();
  const { result } = renderHook(() => useStoredViewPrefs(hydrated));
  expect(result.current.ready).toBe(false);
  await act(async () => { await Promise.resolve(); });
  expect(hydrated).toHaveBeenCalledWith({ sort: "title-asc", layout: "compact" });
  expect(result.current.ready).toBe(true);
});
