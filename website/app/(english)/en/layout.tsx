import type { ReactNode } from "react";
import { metadata } from "../../../lib/site";
import { themeBootstrap } from "../../../lib/theme";
import "../../globals.css";

export const generateMetadata = () => metadata("en");
export const viewport = { themeColor: "#111214", colorScheme: "light dark" };

export default function Layout({ children }: { children: ReactNode }) {
  return <html lang="en" className="dark" suppressHydrationWarning><head><script id="theme-init" dangerouslySetInnerHTML={{ __html: themeBootstrap }} /></head><body>{children}</body></html>;
}
