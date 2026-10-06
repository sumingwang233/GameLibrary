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
    title: t("GameLibrary — 你的游戏，值得好好收藏", "GameLibrary — 你的遊戲，值得好好收藏", "GameLibrary — Your games, thoughtfully collected", "GameLibrary — あなたのゲームを、大切なコレクションに"),
    description: t("扫描、审核、整理并启动硬盘里的游戏。无需账号，游戏文件留在原处。", "掃描、審核、整理並啟動硬碟裡的遊戲。無需帳號，遊戲檔案留在原處。", "Scan, review, organize and launch the games on your drives. No account required. Your files stay in place.", "フォルダーをスキャンし、確認・整理してゲームを起動。アカウント不要。ゲームのファイルは元の場所に残ります。"),
    alternates: {
      canonical: origin + (language === "zh-CN" ? "" : `${language}/`),
      languages: { ...Object.fromEntries(languages.map(locale => [locale, origin + (locale === "zh-CN" ? "" : `${locale}/`)])), "x-default": origin },
    },
    icons: { icon: { url: asset("icon.png"), type: "image/png" } },
  };
}
