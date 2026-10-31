import { useLayoutEffect, useRef, useState, type RefObject } from "react";

const OVERSCAN = 3;

export function visibleRowRange(total: number, stride: number, top: number, height: number) {
  if (total === 0) return { first: 0, last: 0 };
  const first = Math.min(total - 1, Math.max(0, Math.floor(top / stride) - OVERSCAN));
  const last = Math.max(first + 1, Math.min(total, Math.ceil((top + height) / stride) + OVERSCAN));
  return { first, last };
}

export function virtualRowIndices(first: number, last: number, focused: number, total: number) {
  const rows = Array.from({ length: Math.max(0, last - first) }, (_, index) => first + index);
  // Keep the focused row mounted when the user scrolls with a mouse or touchpad.
  if (focused >= 0 && focused < total && (focused < first || focused >= last)) rows.push(focused);
  return rows.sort((a, b) => a - b);
}

// ponytail: rows share a measured height; use per-row measurement if variable-height cards are introduced.
export function useVirtualRows(
  count: number,
  layout: "grid" | "compact",
  enabled: boolean,
  scrollRoot?: RefObject<HTMLElement | null>,
) {
  const container = useRef<HTMLDivElement | null>(null);
  const [viewport, setViewport] = useState({ columns: 1, stride: 320, first: 0, last: 8 });
  const gap = layout === "grid" ? 16 : 8;

  // Re-measure after commits as well: a batch-action bar can change the list's offset.
  useLayoutEffect(() => {
    const node = container.current;
    if (!enabled || !node) return;
    const root = scrollRoot?.current ?? node.closest("main");
    let frame = 0;
    const measure = () => {
      frame = 0;
      const grid = node.querySelector<HTMLElement>("[data-virtual-grid]");
      const item = node.querySelector<HTMLElement>("[data-virtual-item]");
      if (!item) return;
      const tracks = grid ? getComputedStyle(grid).gridTemplateColumns : "none";
      const columns = layout === "grid" && tracks && tracks !== "none" ? tracks.split(/\s+/).length : 1;
      const measuredHeight = item.getBoundingClientRect().height;
      const stride = measuredHeight > 0 ? measuredHeight + gap : layout === "compact" ? 96 : 320;
      const top = (root ? root.getBoundingClientRect().top + root.clientTop : 0) - node.getBoundingClientRect().top;
      const height = root?.clientHeight || globalThis.innerHeight || 800;
      const range = visibleRowRange(Math.ceil(count / columns), stride, top, height);
      setViewport(previous => previous.columns === columns && previous.stride === stride
        && previous.first === range.first && previous.last === range.last
        ? previous : { columns, stride, ...range });
    };
    const schedule = () => {
      if (!frame) frame = requestAnimationFrame(measure);
    };
    const source = root ?? globalThis;
    source.addEventListener("scroll", schedule, { passive: true });
    globalThis.addEventListener("resize", schedule);
    const observer = typeof ResizeObserver === "undefined" ? undefined : new ResizeObserver(schedule);
    observer?.observe(node);
    if (root) observer?.observe(root);
    const item = node.querySelector<HTMLElement>("[data-virtual-item]");
    if (item) observer?.observe(item);
    measure();
    return () => {
      source.removeEventListener("scroll", schedule);
      globalThis.removeEventListener("resize", schedule);
      observer?.disconnect();
      if (frame) cancelAnimationFrame(frame);
    };
  });

  const columns = layout === "compact" ? 1 : viewport.columns;
  const total = Math.ceil(count / columns);
  const first = Math.min(viewport.first, Math.max(0, total - 1));
  const last = Math.min(total, Math.max(first + 1, viewport.last));
  return { container, columns, total, first, last, stride: viewport.stride,
    height: Math.max(0, total * viewport.stride - gap) };
}
