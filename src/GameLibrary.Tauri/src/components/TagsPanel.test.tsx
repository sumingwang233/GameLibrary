import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { TagsPanel } from "./TagsPanel";
import { TAG_PALETTE, tagStyle } from "../lib/tags";

afterEach(cleanup);

it("keeps defaults themed and applies custom colors without a background", () => {
  expect(TAG_PALETTE).toContain("#ffffff");
  expect(tagStyle({ color: null })).toEqual({ color: "var(--color-text-primary)" });
  expect(tagStyle({ color: "#ffffff" })).toEqual({ color: "#ffffff" });
  expect(tagStyle({ color: "#000000" })).toEqual({ color: "#000000" });
  expect(tagStyle({ color: "invalid" })).toEqual(tagStyle({ color: null }));
});

it("shows colored text and offers white and explicit default color patches", async () => {
  const tag = { tagId: "a", kind: "user", name: "标签", color: "#ffffff", revision: 1 };
  const update = vi.fn(async () => {});
  render(<TagsPanel tags={[tag]} onUpdate={update} onCreate={vi.fn()} onRename={vi.fn()} onReorder={vi.fn()} onRemove={vi.fn()} />);
  expect(screen.getByText("标签").parentElement).toHaveStyle({ color: "#ffffff" });
  expect(screen.getByText("标签").parentElement!.style.backgroundColor).toBe("");
  fireEvent.click(screen.getByRole("button", { name: "编辑颜色 标签" }));
  fireEvent.click(screen.getByRole("button", { name: "使用颜色 #ffffff" }));
  await waitFor(() => expect(update).toHaveBeenCalledWith(tag, { color: "#ffffff" }));
  await waitFor(() => expect(screen.queryByRole("button", { name: "默认（无自定义颜色）" })).not.toBeInTheDocument());
  fireEvent.click(screen.getByRole("button", { name: "编辑颜色 标签" }));
  fireEvent.click(screen.getByRole("button", { name: "默认（无自定义颜色）" }));
  await waitFor(() => expect(update).toHaveBeenCalledWith(tag, { color: null }));
});
