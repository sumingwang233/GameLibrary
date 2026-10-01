import type { ReactNode } from "react";
import { metadata } from "../../lib/site";
import { themeBootstrap } from "../../lib/theme";
import "../globals.css";

export const generateMetadata = () => metadata("zh-CN");
export const viewport = { themeColor: "#0b1020", colorScheme: "dark light" };

export default function Layout({ children }: { children: ReactNode }) {
  return <html lang="zh-CN" className="dark" suppressHydrationWarning><head><script id="theme-init" dangerouslySetInnerHTML={{ __html: themeBootstrap }} /></head><body>{children}</body></html>;
}
