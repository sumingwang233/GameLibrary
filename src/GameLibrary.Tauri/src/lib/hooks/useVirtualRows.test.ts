import { expect, it } from "vitest";
import { virtualRowIndices, visibleRowRange } from "./useVirtualRows";

it("bounds a large library to viewport rows and pins only the focused row", () => {
  const range = visibleRowRange(10000, 100, 5000, 600);
  expect(range).toEqual({ first: 47, last: 59 });
  const rows = virtualRowIndices(range.first, range.last, 0, 10000);
  expect(rows).toHaveLength(13);
  expect(rows[0]).toBe(0);
  expect(rows[rows.length - 1]).toBe(58);
  expect(virtualRowIndices(47, 59, 50, 10000)).toHaveLength(12);
});

it("keeps empty, short, and shortened libraries within valid indices", () => {
  expect(visibleRowRange(0, 100, 5000, 600)).toEqual({ first: 0, last: 0 });
  expect(visibleRowRange(1, 100, 5000, 600)).toEqual({ first: 0, last: 1 });
  expect(virtualRowIndices(0, 0, 4, 0)).toEqual([]);
  expect(visibleRowRange(2, 100, -1000, 600)).toEqual({ first: 0, last: 1 });
});
