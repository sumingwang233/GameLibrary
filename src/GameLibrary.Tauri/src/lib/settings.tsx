import { t } from "./i18n";
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
import { setLanguage } from "./i18n";

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
  setLanguage(settings.uiLanguage);
  void invoke("set_ui_language", { language: settings.uiLanguage ?? "zh-CN" }).catch(() => undefined);
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
  const requestVersion = useRef(0);

  const pushCloseToTray = useCallback((value: boolean) => {
    void invoke("set_close_to_tray", { enabled: value }).catch(() => undefined);
  }, []);

  const reload = useCallback(async () => {
    const version = ++requestVersion.current;
    setLoading(true);
    try {
      const result = await operation<LibrarySettings>("settings.get");
      if (version !== requestVersion.current) return;
      revisionRef.current = result.data.revision;
      setSettings(result.data);
      applySettings(result.data);
      pushCloseToTray(result.data.closeToTray);
      setError(null);
    } catch (cause) {
      if (version === requestVersion.current) setError(describeFailure(cause));
    } finally {
      if (version === requestVersion.current) setLoading(false);
    }
  }, [pushCloseToTray]);

  useEffect(() => {
    void reload();
    const changed = () => { void reload(); };
    window.addEventListener("gamelibrary-settings-changed", changed);
    return () => {
      ++requestVersion.current;
      window.removeEventListener("gamelibrary-settings-changed", changed);
    };
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
      const version = ++requestVersion.current;
      setError(null);
      try {
        const result = await operation<LibrarySettings>(
          "settings.update",
          { ...patch, expectedRevision: revisionRef.current },
          "settings.update",
        );
        if (version !== requestVersion.current) return null;
        revisionRef.current = result.data.revision;
        setSettings(result.data);
        applySettings(result.data);
        pushCloseToTray(result.data.closeToTray);
        return result.data;
      } catch (cause) {
        if (version === requestVersion.current) setError(describeFailure(cause));
        return null;
      } finally {
        if (version === requestVersion.current) setLoading(false);
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
  if (!value) throw new Error(t("useSettings 必须在 SettingsProvider 内使用"));
  return value;
}
