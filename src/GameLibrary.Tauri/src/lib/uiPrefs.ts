import { useCallback, useEffect, useRef, useState } from "react";
import { invoke } from "@tauri-apps/api/core";

/**
 * 视图偏好写在 `%APPDATA%\GameLibrary\ui.json`，与 WPF 的 leftWidth 同一份文件。
 * Rust 命令做合并写入，避免盖掉另一端已经记下的字段。
 *
 * 只持久化「怎么看库」：排序、网格/紧凑。搜索、封面筛选、标签、收藏、已游玩、
 * 自定义视图都是这一次会话的筛选；重启后把它们恢复回来，会像游戏凭空少了一批。
 */
export const PERSISTED_SORTS = ["accepted-desc", "updated-desc", "title-asc", "title-desc"] as const;
export type PersistedSort = (typeof PERSISTED_SORTS)[number];
export type PersistedLayout = "grid" | "compact";

export interface ParsedUiViewPrefs {
  sort?: PersistedSort;
  layout?: PersistedLayout;
}

export function parseUiViewPrefs(value: unknown): ParsedUiViewPrefs {
  if (!value || typeof value !== "object") return {};
  const record = value as Record<string, unknown>;
  const sort = typeof record.sort === "string" && (PERSISTED_SORTS as readonly string[]).includes(record.sort)
    ? record.sort as PersistedSort
    : undefined;
  const layout = record.layout === "grid" || record.layout === "compact" ? record.layout : undefined;
  return { sort, layout };
}

export function useStoredViewPrefs(onHydrate: (prefs: ParsedUiViewPrefs) => void) {
  const onHydrateRef = useRef(onHydrate);
  onHydrateRef.current = onHydrate;
  const [ready, setReady] = useState(false);
  const dirty = useRef(false);

  useEffect(() => {
    let cancelled = false;
    invoke<unknown>("load_ui_prefs")
      .then((value) => {
        if (cancelled) return;
        // 用户已经改过排序时，迟到的读盘不能把选择盖回去。
        if (!dirty.current) onHydrateRef.current(parseUiViewPrefs(value));
        setReady(true);
      })
      .catch(() => {
        if (!cancelled) setReady(true);
      });
    return () => {
      cancelled = true;
    };
  }, []);

  const save = useCallback((patch: ParsedUiViewPrefs) => {
    dirty.current = true;
    void invoke("save_ui_prefs", { patch }).catch(() => undefined);
  }, []);

  return { ready, save };
}
