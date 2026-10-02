import type { ReactNode } from "react";
import { metadata } from "../../lib/site";
import { themeBootstrap } from "../../lib/theme";
import "../globals.css";

export const generateMetadata = () => metadata("zh-CN");
export const viewport = { themeColor: "#f4faff", colorScheme: "light dark" };

export default function Layout({ children }: { children: ReactNode }) {
  return <html lang="zh-CN" suppressHydrationWarning><head><script id="theme-init" dangerouslySetInnerHTML={{ __html: themeBootstrap }} /></head><body>{children}</body></html>;
}
