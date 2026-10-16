import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { useState } from "react";
import { LibraryToolbar } from "./LibraryToolbar";

afterEach(cleanup);

it("changes the cover filter through all three choices", () => {
  const changed = vi.fn();
  const scroll = Object.getOwnPropertyDescriptor(HTMLElement.prototype, "scrollIntoView");
  Object.defineProperty(HTMLElement.prototype, "scrollIntoView", { configurable: true, value: vi.fn() });
  function Toolbar() {
    const [hasCover, setHasCover] = useState<boolean | null>(null);
    return <LibraryToolbar search="" onSearch={() => {}} sort="accepted-desc" onSort={() => {}}
      hasCover={hasCover} onHasCover={value => { changed(value); setHasCover(value); }}
      onRefresh={() => {}} layout="grid" onLayout={() => {}} total={0} shown={0} />;
  }
  try {
    render(<Toolbar />);
    const trigger = screen.getByRole("combobox", { name: "封面筛选" });
    expect(trigger).toHaveTextContent("不限封面");
    for (const [label, value] of [["有封面", true], ["无封面", false], ["不限封面", null]] as const) {
      fireEvent.keyDown(trigger, { key: "ArrowDown" });
      fireEvent.click(screen.getByRole("option", { name: label }));
      expect(changed).toHaveBeenLastCalledWith(value);
      expect(trigger).toHaveTextContent(label);
    }
  } finally {
    if (scroll) Object.defineProperty(HTMLElement.prototype, "scrollIntoView", scroll);
    else Reflect.deleteProperty(HTMLElement.prototype, "scrollIntoView");
  }
});
