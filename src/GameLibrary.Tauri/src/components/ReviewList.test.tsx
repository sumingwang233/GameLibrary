import { act, cleanup, fireEvent, render, renderHook, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { ReviewList } from "./ReviewList";
import { useCandidateReview } from "../lib/hooks/useCandidateReview";
import type { CandidateItem, FlashCandidateInspection } from "../lib/types";

const { call } = vi.hoisted(() => ({ call: vi.fn() }));
vi.mock("../lib/api", async importOriginal => ({
  ...await importOriginal<typeof import("../lib/api")>(), operation: call,
}));
const candidate: CandidateItem = {
  candidateId: "flash", relativePath: "Collection", physicalPath: "C:/games/Collection", kind: "unknown",
  reviewState: "pendingReview", revision: 1,
  flash: { directoryPath: "C:/games/Collection", kind: "unknown", requiresReview: true,
    entryPaths: [], inventory: ["main.swf", "scene.swf"], complete: true, reasons: [] },
};
const detail: FlashCandidateInspection = {
  candidateId: "flash", revision: 2, flash: candidate.flash!, entryCandidates: [],
  adjustments: [{ gameId: "scene", expectedRevision: 3, title: "Scene", rootPath: "C:/games/Collection/scene.swf",
    entryPath: "C:/games/Collection/scene.swf", proposedAction: "removeFromLibrary" }],
};
const envelope = (data: unknown) => ({ ok: true, data, requestId: "test", status: "completed" });
beforeEach(() => { call.mockReset(); });
afterEach(cleanup);

it("resource confirmation previews an empty entry selection and requires a separate explicit apply", async () => {
  call.mockResolvedValue(envelope(detail));
  const review = vi.fn(async () => {});
  render(<ReviewList candidates={[candidate]} busy={false} onReview={review} />);
  fireEvent.click(screen.getByRole("button", { name: "展开目录审核" }));
  fireEvent.change(await screen.findByRole("combobox"), { target: { value: "resources" } });
  expect(screen.queryByRole("radio")).toBeNull();
  fireEvent.click(screen.getByRole("button", { name: "预览调整清单" }));
  const apply = await screen.findByRole("button", { name: "确认目录判断并应用" });
  expect(call).toHaveBeenLastCalledWith("candidates.inspect", { candidateId: "flash", flashKind: "resources", entryPaths: [] });
  expect(screen.getByText(/Scene ·/)).toBeTruthy();
  expect(review).not.toHaveBeenCalled();
  fireEvent.click(apply);
  await waitFor(() => expect(review).toHaveBeenCalledWith([expect.objectContaining({ revision: 2,
    flashReview: { kind: "resources", entryPaths: [], adjustments: [{ gameId: "scene", expectedRevision: 3 }] } })], "accept"));
});

it("changing a chosen project entry invalidates the inspected adjustment plan", async () => {
  call.mockResolvedValue(envelope(detail));
  render(<ReviewList candidates={[candidate]} busy={false} onReview={vi.fn(async () => {})} />);
  fireEvent.click(screen.getByRole("button", { name: "展开目录审核" }));
  fireEvent.change(await screen.findByRole("combobox"), { target: { value: "project" } });
  fireEvent.click(screen.getByRole("radio", { name: "main.swf" }));
  fireEvent.click(screen.getByRole("button", { name: "预览调整清单" }));
  await screen.findByRole("button", { name: "确认目录判断并应用" });
  fireEvent.click(screen.getByRole("radio", { name: "scene.swf" }));
  expect(screen.queryByRole("button", { name: "确认目录判断并应用" })).toBeNull();
});

it("batch review forwards the user's confirmed directory decision and adjustment revisions", async () => {
  const selection = { kind: "project" as const, entryPaths: ["main.swf"], adjustments: [{ gameId: "scene", expectedRevision: 3 }] };
  call.mockResolvedValue(envelope({ items: [{ candidateId: "flash", result: envelope({ reviewState: "accepted", revision: 2 }) }] }));
  const refresh = vi.fn(async () => {});
  const hook = renderHook(() => useCandidateReview(() => true, refresh, vi.fn()));
  await act(async () => { await hook.result.current([{ ...candidate, flashReview: selection }], "accept"); });
  expect(call).toHaveBeenCalledWith("candidates.review_batch", {
    action: "accept", items: [{ candidateId: "flash", expectedRevision: 1, flashKind: "project",
      entryPaths: ["main.swf"], adjustments: [{ gameId: "scene", expectedRevision: 3 }] }],
  }, expect.any(String));
  expect(refresh).toHaveBeenCalledOnce();
});
