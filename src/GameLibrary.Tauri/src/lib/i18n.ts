import { useSyncExternalStore } from "react";
import catalog from "../../../GameLibrary.Contracts/Localization/ui.json";

export const LANGUAGES = [
  { value: "zh-CN", label: "简体中文" },
  { value: "zh-TW", label: "繁體中文" },
  { value: "en", label: "English" },
  { value: "ja", label: "日本語" },
] as const;
export type UiLanguage = (typeof LANGUAGES)[number]["value"];
let language: UiLanguage = "zh-CN";
const listeners = new Set<() => void>();
export function getLanguage() { return language; }
export function setLanguage(value: string | undefined) {
  const next = LANGUAGES.some((item) => item.value === value) ? value as UiLanguage : "zh-CN";
  document.documentElement.lang = next;
  if (next === language) return;
  language = next;
  listeners.forEach((notify) => notify());
}
export function useLanguage() {
  return useSyncExternalStore((notify) => {
    listeners.add(notify);
    return () => { listeners.delete(notify); };
  }, getLanguage);
}
export function t(source: string, ...values: unknown[]): string {
  const entry = (catalog as Record<string, string[]>)[source.replace(/\s+/g, " ").trim()];
  const text = language === "zh-CN" || !entry ? source : entry[{ "zh-TW": 0, en: 1, ja: 2 }[language]];
  return text.replace(/\{(\d+)\}/g, (match, index: string) =>
    Number(index) < values.length ? String(values[Number(index)]) : match);
}
