import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { SettingsProvider } from "../lib/settings";
import { TitleBar } from "./TitleBar";

const { operation } = vi.hoisted(() => ({ operation: vi.fn() }));
vi.mock("../lib/api", () => ({ operation, describeFailure: (cause: unknown) => String(cause) }));
vi.mock("@tauri-apps/api/core", () => ({ invoke: vi.fn().mockResolvedValue(undefined) }));
vi.mock("@tauri-apps/api/window", () => ({ getCurrentWindow: () => ({
  isMaximized: async () => false, onResized: async () => () => {},
}) }));

afterEach(() => { cleanup(); operation.mockReset(); vi.unstubAllGlobals(); document.documentElement.classList.remove("light"); });

function renderTitleBar() {
  return render(<SettingsProvider><TitleBar scanning={false} onTags={vi.fn()} onRoots={vi.fn()}
    onManualAdd={vi.fn()} onAddRoot={vi.fn()} onScan={vi.fn()} onSettings={vi.fn()} /></SettingsProvider>);
}

it.each([
  ["light", false, "dark"], ["dark", false, "light"],
  ["system", true, "dark"], ["system", false, "light"],
] as const)("toggles and persists %s (system light: %s) to %s", async (theme, systemLight, target) => {
  vi.stubGlobal("matchMedia", () => ({ matches: systemLight, addEventListener() {}, removeEventListener() {} }));
  let saved = { revision: 1, theme, closeToTray: false };
  operation.mockImplementation(async (name, patch) => {
    if (name === "settings.update") saved = { ...saved, ...patch, revision: saved.revision + 1 };
    return { data: saved };
  });
  const view = renderTitleBar();
  const button = screen.getByRole("button", { name: "切换深色/浅色模式" });
  await waitFor(() => expect(button).toBeEnabled());
  expect(button).not.toHaveAttribute("data-tauri-drag-region");
  expect(button.nextElementSibling).toHaveAttribute("aria-label", "窗口控制");
  expect(document.documentElement.classList.contains("light")).toBe(target === "dark");
  fireEvent.click(button);
  await waitFor(() => expect(document.documentElement.classList.contains("light")).toBe(target === "light"));
  expect(operation).toHaveBeenCalledWith("settings.update", { theme: target, expectedRevision: 1 }, "settings.update");
  view.unmount();
  renderTitleBar();
  await waitFor(() => expect(screen.getByRole("button", { name: "切换深色/浅色模式" })).toBeEnabled());
  expect(document.documentElement.classList.contains("light")).toBe(target === "light");
});

it("prevents repeated saves and reports a failure without changing the theme", async () => {
  operation.mockResolvedValueOnce({ data: { revision: 1, theme: "light", closeToTray: false } });
  let rejectSave!: (cause: Error) => void;
  operation.mockImplementationOnce(() => new Promise((_, reject) => { rejectSave = reject; }));
  renderTitleBar();
  const button = screen.getByRole("button", { name: "切换深色/浅色模式" });
  await waitFor(() => expect(button).toBeEnabled());
  fireEvent.click(button);
  expect(button).toBeDisabled();
  fireEvent.click(button);
  expect(operation).toHaveBeenCalledTimes(2);
  rejectSave(new Error("save failed"));
  expect(await screen.findByRole("alert")).toHaveTextContent("save failed");
  await waitFor(() => expect(button).toBeEnabled());
  expect(document.documentElement).toHaveClass("light");
});
