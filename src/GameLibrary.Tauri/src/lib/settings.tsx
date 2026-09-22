import {
  createContext,
  createElement,
  useCallback,
  useContext,
  useEffect,
  useRef,
  useState,
  type PropsWithChildren,
} from "react";
import { invoke } from "@tauri-apps/api/core";
import { describeFailure, operation } from "./api";
import type { LibrarySettings } from "./types";

/**
 * settings.theme / uiFontFamily 应用到 documentElement，closeToTray 下发给 Rust
 * （窗口关闭行为只能在原生侧决定）。Rust 不查库，业务状态仍全部留在 Host。
 * v1.5.0 起界面缩放（uiFontScale）整体下线：前端不再读写该字段，
 * 后端 settings 保留字段以兼容旧库；根字号由 index.css 的 --gl-font-scale 回退值 1 固定。
 */

const LIGHT_QUERY = "(prefers-color-scheme: light)";

function resolveTheme(mode: LibrarySettings["theme"]): "dark" | "light" {
  if (mode !== "system") return mode;
  return window.matchMedia(LIGHT_QUERY).matches ? "light" : "dark";
}

function applySettings(settings: LibrarySettings) {
  const root = document.documentElement;
  root.classList.toggle("light", resolveTheme(settings.theme) === "light");
  if (settings.uiFontFamily) {
    root.style.setProperty("--gl-font-family", settings.uiFontFamily);
  }
}

interface SettingsContextValue {
  settings: LibrarySettings | null;
  loading: boolean;
  error: string | null;
  reload: () => Promise<void>;
  update: (patch: Partial<LibrarySettings>) => Promise<LibrarySettings | null>;
}

const SettingsContext = createContext<SettingsContextValue | null>(null);

function useSettingsController(): SettingsContextValue {
  const [settings, setSettings] = useState<LibrarySettings | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const revisionRef = useRef(0);

  const pushCloseToTray = useCallback((value: boolean) => {
    void invoke("set_close_to_tray", { enabled: value }).catch(() => undefined);
  }, []);

  const reload = useCallback(async () => {
    setLoading(true);
    try {
      const result = await operation<LibrarySettings>("settings.get");
      revisionRef.current = result.data.revision;
      setSettings(result.data);
      applySettings(result.data);
      pushCloseToTray(result.data.closeToTray);
      setError(null);
    } catch (cause) {
      setError(describeFailure(cause));
    } finally {
      setLoading(false);
    }
  }, [pushCloseToTray]);

  useEffect(() => {
    void reload();
  }, [reload]);

  // theme=system 时跟随系统深色设置变化，无需重开应用。
  useEffect(() => {
    if (settings?.theme !== "system") return;
    const media = window.matchMedia(LIGHT_QUERY);
    const onChange = () => applySettings(settings);
    media.addEventListener("change", onChange);
    return () => media.removeEventListener("change", onChange);
  }, [settings]);

  const update = useCallback(
    async (patch: Partial<LibrarySettings>) => {
      setError(null);
      try {
        const result = await operation<LibrarySettings>(
          "settings.update",
          { ...patch, expectedRevision: revisionRef.current },
          "settings.update",
        );
        revisionRef.current = result.data.revision;
        setSettings(result.data);
        applySettings(result.data);
        pushCloseToTray(result.data.closeToTray);
        return result.data;
      } catch (cause) {
        setError(describeFailure(cause));
        return null;
      }
    },
    [pushCloseToTray],
  );

  return { settings, loading, error, reload, update };
}

export function SettingsProvider({ children }: PropsWithChildren) {
  return createElement(SettingsContext.Provider, { value: useSettingsController() }, children);
}

export function useSettings() {
  const value = useContext(SettingsContext);
  if (!value) throw new Error("useSettings 必须在 SettingsProvider 内使用");
  return value;
}
