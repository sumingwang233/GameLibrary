import type { Metadata } from "next";

export type Language = "zh-CN" | "en";
export const prefix = process.env.NODE_ENV === "production" ? "/GameLibrary" : "";
export const repository = "https://github.com/sumingwang233/GameLibrary";
export const release = `${repository}/releases/download/v1.5.5/`;
export const asset = (name: string) => `${prefix}/assets/${name}`;
export const home = (language: Language) => `${prefix}/${language === "en" ? "en/" : ""}`;

export function metadata(language: Language): Metadata {
  const origin = "https://sumingwang233.github.io/GameLibrary/";
  return {
    title: language === "en" ? "GameLibrary — Local game manager for Windows" : "GameLibrary — Windows 本地游戏管理",
    description: language === "en"
      ? "Scan your Windows game folders, review the results, and organize local games with tags and favorites. Game files stay in their original locations."
      : "扫描 Windows 游戏目录，确认入库，再用标签和收藏整理本地游戏。支持搜索和保存启动方式，游戏文件留在原处。",
    alternates: {
      canonical: origin + (language === "en" ? "en/" : ""),
      languages: { "zh-CN": origin, en: origin + "en/", "x-default": origin },
    },
    icons: { icon: { url: asset("icon.png"), type: "image/png" } },
  };
}
