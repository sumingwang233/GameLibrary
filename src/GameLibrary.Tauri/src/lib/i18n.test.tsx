import { act, cleanup, fireEvent, render, renderHook, screen, waitFor } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import catalog from "../../../GameLibrary.Contracts/Localization/ui.json";
import { getLanguage, LANGUAGES, setLanguage, t, useLanguage } from "./i18n";
import { SettingsProvider, useSettings } from "./settings";
import { SettingsDialog } from "../components/SettingsDialog";
import { TagsPanel } from "../components/TagsPanel";

const { operation, invoke } = vi.hoisted(() => ({ operation: vi.fn(), invoke: vi.fn().mockResolvedValue(undefined) }));
vi.mock("./api", () => ({ operation, describeFailure: (e: unknown) => String(e) }));
vi.mock("@tauri-apps/api/core", () => ({ invoke }));
vi.mock("@tauri-apps/api/app", () => ({ getVersion: async () => "1.5.5" }));
afterEach(() => { cleanup(); setLanguage("zh-CN"); operation.mockReset(); invoke.mockClear(); vi.unstubAllGlobals(); });

it("defaults to Simplified Chinese, switches live, and preserves data in interpolation", () => {
  expect(getLanguage()).toBe("zh-CN");
  const { result, unmount } = renderHook(useLanguage);
  for (const [locale, expected] of [["zh-TW", "設定"], ["en", "Settings"], ["ja", "設定"], ["zh-CN", "设置"]]) {
    act(() => setLanguage(locale));
    expect(result.current).toBe(locale);
    expect(t("设置")).toBe(expected);
    expect(document.documentElement.lang).toBe(locale);
  }
  act(() => setLanguage("en"));
  expect(t("从游戏库移除「{0}」", "设置 $& {1}" )).toBe("Remove “设置 $& {1}” from library");
  unmount();
  setLanguage("unsupported");
  expect(getLanguage()).toBe("zh-CN");
});

it("keeps every translation's interpolation fields intact", () => {
  const fields = (text: string) => [...text.matchAll(/\{\d+(?:[^}]*)\}/g)].map(hit => hit[0]).sort();
  for (const [source, translations] of Object.entries(catalog)) {
    expect(translations).toHaveLength(3);
    for (const translation of translations) {
      expect(translation.trim()).not.toBe("");
      expect(fields(translation), source).toEqual(fields(source));
    }
  }
});

it("persists the settings selector and refreshes memoized tag headings without changing tag names", async () => {
  vi.stubGlobal("matchMedia", () => ({ matches: false, addEventListener() {}, removeEventListener() {} }));
  let saved = { revision: 2, uiLanguage: "zh-CN", theme: "dark", closeToTray: true, scanIntervalMinutes: 60 };
  operation.mockImplementation(async (name, patch) => {
    if (name === "settings.update") saved = { ...saved, ...patch, revision: saved.revision + 1 };
    return { data: saved };
  });
  const tags = [{ tagId: "tag", name: "设置", kind: "user", category: "gameplay", revision: 1 }];
  function View() {
    useLanguage();
    return <SettingsProvider>
      <SettingsDialog open onClose={() => {}} />
      <TagsPanel tags={tags} onCreate={async () => {}} onRename={async () => {}} onReorder={async () => {}} onUpdate={async () => {}} onRemove={async () => {}} />
    </SettingsProvider>;
  }
  const view = render(<View />);
  const select = await screen.findByRole("combobox", { name: "语言" });
  expect([...select.querySelectorAll("option")].map(item => item.textContent)).toEqual(LANGUAGES.map(item => item.label));
  fireEvent.change(select, { target: { value: "en" } });
  await waitFor(() => expect(screen.getByRole("combobox", { name: "Language" })).toHaveValue("en"));
  expect(operation).toHaveBeenCalledWith("settings.update", { uiLanguage: "en", expectedRevision: 2 }, "settings.update");
  expect(invoke).toHaveBeenCalledWith("set_ui_language", { language: "en" });
  expect(screen.getAllByText(/Gameplay/).length).toBeGreaterThan(0);
  expect(screen.getByText("设置")).toBeInTheDocument();
  view.unmount();
  render(<View />);
  expect(await screen.findByRole("combobox", { name: "Language" })).toHaveValue("en");
});

it("discards an older settings response after a language change", async () => {
  const initial = { revision: 1, uiLanguage: "zh-CN", theme: "dark", closeToTray: false, scanIntervalMinutes: 60 };
  let finishReload!: (value: unknown) => void;
  operation.mockResolvedValueOnce({ data: initial });
  const { result } = renderHook(useSettings, { wrapper: SettingsProvider });
  await waitFor(() => expect(result.current.loading).toBe(false));
  operation.mockImplementationOnce(() => new Promise(resolve => { finishReload = resolve; }));
  let reload!: Promise<void>;
  act(() => { reload = result.current.reload(); });
  operation.mockResolvedValueOnce({ data: { ...initial, revision: 2, uiLanguage: "ja" } });
  await act(async () => { await result.current.update({ uiLanguage: "ja" }); });
  await act(async () => { finishReload({ data: initial }); await reload; });
  expect(result.current.settings?.uiLanguage).toBe("ja");
  expect(getLanguage()).toBe("ja");
  expect(result.current.loading).toBe(false);
});
