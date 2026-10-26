import { act, cleanup, render, screen } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import type { ScanProgressState } from "../lib/state";
import { ScanProgressBar } from "./ScanProgressBar";

afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

it("只播报随 phase 变化的标题，并把每秒跳动的计时藏起来", () => {
  vi.useFakeTimers();
  const progress: ScanProgressState = {
    phase: "running",
    activeRoots: 1,
    scannedDirectories: 0,
    candidatesFound: 0,
    startedAt: Date.now(),
  };
  render(<ScanProgressBar progress={progress} onCancel={vi.fn()} />);

  const status = screen.getByRole("status");
  expect(status).toHaveTextContent("正在扫描游戏库…");
  act(() => {
    vi.advanceTimersByTime(3000);
  });
  expect(status).toHaveTextContent("正在扫描游戏库…");
  expect(screen.getByText(/已运行/)).toHaveAttribute("aria-hidden", "true");
  expect(screen.getByRole("progressbar", { name: "正在扫描游戏库" })).toBeInTheDocument();
  expect(screen.getByRole("button", { name: "取消扫描" })).toBeEnabled();
});
