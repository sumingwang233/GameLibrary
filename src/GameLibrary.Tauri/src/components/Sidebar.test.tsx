import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { Sidebar } from "./Sidebar";

afterEach(cleanup);

it("gives the saved-folder area to tags and keeps the favorites filter", () => {
  render(<Sidebar section="library" onSectionChange={vi.fn()} viewId="" onViewChange={vi.fn()}
    favoriteOnly={false} onFavoriteChange={vi.fn()} tagId="" onTagChange={vi.fn()}
    tags={[{ tagId: "unity", kind: "engine", name: "unity", revision: 1, gameCount: 10 }]}
    candidateTotal={0} gameTotal={10} notificationTotal={0} onCreateTag={vi.fn()} />);
  expect(screen.getByRole("button", { name: "收藏" })).toBeInTheDocument();
  expect(screen.getByRole("button", { name: /unity/ })).toBeInTheDocument();
  expect(screen.queryByText("收藏夹")).not.toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "新建收藏夹" })).not.toBeInTheDocument();
});
