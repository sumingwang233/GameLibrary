import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { watchWindowVisibility } from "./windowVisibility";

const native = vi.hoisted(() => ({
  enabled: true,
  callback: undefined as ((event: { payload: boolean }) => void) | undefined,
  stop: vi.fn(), visible: vi.fn(), minimized: vi.fn(), listen: vi.fn(),
}));
vi.mock("@tauri-apps/api/core", () => ({ isTauri: () => native.enabled }));
vi.mock("@tauri-apps/api/event", () => ({ listen: native.listen }));
vi.mock("@tauri-apps/api/window", () => ({ getCurrentWindow: () => ({ isVisible: native.visible, isMinimized: native.minimized }) }));
beforeEach(() => {
  native.enabled = true;
  native.stop.mockReset(); native.visible.mockReset(); native.minimized.mockReset(); native.listen.mockReset();
  native.visible.mockResolvedValue(true); native.minimized.mockResolvedValue(false);
  native.listen.mockImplementation(async (_name, callback: typeof native.callback) => {
    native.callback = callback;
    return native.stop;
  });
});
afterEach(() => vi.restoreAllMocks());

it("keeps a newer native hide event instead of a late visible snapshot and deduplicates state", async () => {
  let finish!: (value: boolean) => void;
  native.visible.mockImplementation(() => new Promise(resolve => { finish = resolve; }));
  const changed = vi.fn();
  const stop = watchWindowVisibility(changed);
  await Promise.resolve();
  native.callback?.({ payload: false });
  native.callback?.({ payload: false });
  finish(true);
  await Promise.resolve(); await Promise.resolve();
  expect(changed.mock.calls).toEqual([[false]]);
  native.callback?.({ payload: true });
  expect(changed.mock.calls).toEqual([[false], [true]]);
  stop();
  native.callback?.({ payload: false });
  expect(changed).toHaveBeenCalledTimes(2);
  expect(native.stop).toHaveBeenCalledOnce();
});

it("removes a native listener that finishes registering after disposal", async () => {
  let ready!: (stop: () => void) => void;
  native.listen.mockImplementation(() => new Promise(resolve => { ready = resolve; }));
  const stop = watchWindowVisibility(vi.fn());
  stop(); ready(native.stop);
  await Promise.resolve();
  expect(native.stop).toHaveBeenCalledOnce();
  expect(native.visible).not.toHaveBeenCalled();
});

it("uses document visibility in browser mode", () => {
  native.enabled = false;
  const hidden = vi.spyOn(document, "hidden", "get").mockReturnValue(true);
  const changed = vi.fn();
  const stop = watchWindowVisibility(changed);
  document.dispatchEvent(new Event("visibilitychange"));
  hidden.mockReturnValue(false);
  document.dispatchEvent(new Event("visibilitychange"));
  expect(changed.mock.calls).toEqual([[false], [true]]);
  expect(native.listen).not.toHaveBeenCalled();
  stop();
});
