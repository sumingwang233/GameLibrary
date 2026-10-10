import type { Metadata } from "next";

export const languages = ["zh-CN", "zh-TW", "en", "ja"] as const;
export type Language = typeof languages[number];
export const languageNames: Record<Language, string> = { "zh-CN": "简体中文", "zh-TW": "繁體中文", en: "English", ja: "日本語" };
export const translate = (language: Language) => (zh: string, tw: string, en: string, ja: string) => [zh, tw, en, ja][languages.indexOf(language)];
export const stableVersion = "1.7.5";
export const prefix = process.env.NODE_ENV === "production" ? "/GameLibrary" : "";
export const repository = "https://github.com/sumingwang233/GameLibrary";
export const release = `${repository}/releases/download/v${stableVersion}/`;
export const asset = (name: string) => `${prefix}/assets/${name}`;
export const home = (language: Language) => `${prefix}/${language === "zh-CN" ? "" : `${language}/`}`;

export function metadata(language: Language): Metadata {
  const origin = "https://sumingwang233.github.io/GameLibrary/";
  const t = translate(language);
  return {
    title: t("GameLibrary — 在一个地方管理本地游戏", "GameLibrary — 在一個地方管理本機遊戲", "GameLibrary — Your local games in one library", "GameLibrary — ローカルゲームをひとつのライブラリで管理"),
    description: t("添加游戏目录，扫描后确认入库。用标签分类，从游戏库直接启动。无需账号，原文件留在原处。", "新增遊戲目錄，掃描後確認入庫。用標籤分類，從遊戲庫直接啟動。無需帳號，原始檔案留在原處。", "Scan your game folders, review the results, organize with tags and launch from one library. No account required. Files stay in place.", "ゲームフォルダーをスキャンし、確認して登録。タグで整理してライブラリから起動。アカウント不要で、ファイルは元の場所に残ります。"),
    alternates: {
      canonical: origin + (language === "zh-CN" ? "" : `${language}/`),
      languages: { ...Object.fromEntries(languages.map(locale => [locale, origin + (locale === "zh-CN" ? "" : `${locale}/`)])), "x-default": origin },
    },
    icons: { icon: { url: asset("icon.png"), type: "image/png" } },
  };
}
