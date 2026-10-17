import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { Sidebar, type SidebarProps } from "./Sidebar";

afterEach(cleanup);

it("gives the saved-folder area to tags and keeps the favorites filter", () => {
  render(<Sidebar section="library" onSectionChange={vi.fn()} viewId="" onViewChange={vi.fn()}
    favoriteOnly={false} onFavoriteChange={vi.fn()} tagId="" onTagChange={vi.fn()}
    playedOnly={false} onPlayedChange={vi.fn()}
    tags={[{ tagId: "unity", kind: "engine", name: "unity", revision: 1, gameCount: 10 }]}
    candidateTotal={0} gameTotal={10} notificationTotal={0} onCreateTag={vi.fn()} />);
  expect(screen.getByRole("button", { name: "收藏" })).toBeInTheDocument();
  expect(screen.getByRole("button", { name: /unity/ })).toBeInTheDocument();
  expect(screen.queryByText("收藏夹")).not.toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "新建收藏夹" })).not.toBeInTheDocument();
});

it("switches played and main libraries, clears filters, and keeps tags within the chosen library", () => {
  const props: SidebarProps = {
    section: "library", onSectionChange: vi.fn(), viewId: "", onViewChange: vi.fn(),
    favoriteOnly: false, onFavoriteChange: vi.fn(), playedOnly: false, onPlayedChange: vi.fn(),
    tagId: "", onTagChange: vi.fn(), tags: [{ tagId: "unity", name: "unity", kind: "engine", revision: 1, gameCount: 2 }],
    candidateTotal: 0, gameTotal: 1, notificationTotal: 0, onCreateTag: vi.fn(),
  };
  const view = render(<Sidebar {...props} />);
  expect(screen.getByRole("button", { name: /全部游戏/ })).toHaveAttribute("aria-current", "page");
  fireEvent.click(screen.getByRole("button", { name: "已游玩" }));
  expect(props.onSectionChange).toHaveBeenCalledWith("library");
  expect(props.onPlayedChange).toHaveBeenLastCalledWith(true);
  view.rerender(<Sidebar {...props} playedOnly />);
  expect(screen.getByRole("button", { name: "已游玩" })).toHaveAttribute("aria-current", "page");
  expect(screen.getByRole("button", { name: /全部游戏/ })).not.toHaveAttribute("aria-current");
  vi.mocked(props.onPlayedChange).mockClear();
  fireEvent.click(screen.getByRole("button", { name: /unity/ }));
  expect(props.onTagChange).toHaveBeenCalledWith("unity", "unity");
  expect(props.onPlayedChange).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole("button", { name: /全部游戏/ }));
  expect(props.onPlayedChange).toHaveBeenLastCalledWith(false);
  expect(props.onFavoriteChange).toHaveBeenLastCalledWith(false);
});
