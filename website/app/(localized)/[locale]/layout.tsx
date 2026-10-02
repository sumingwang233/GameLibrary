import type { ReactNode } from "react";
import { notFound } from "next/navigation";
import { metadata } from "../../../lib/site";
import { themeBootstrap } from "../../../lib/theme";
import "../../globals.css";

type Params = Promise<{ locale: string }>;
async function getLanguage(params: Params) {
  const { locale } = await params;
  if (locale !== "zh-TW" && locale !== "ja") notFound();
  return locale;
}
export const dynamicParams = false;
export const generateStaticParams = () => [{ locale: "zh-TW" }, { locale: "ja" }];
export const generateMetadata = async ({ params }: { params: Params }) => metadata(await getLanguage(params));
export const viewport = { themeColor: "#f4faff", colorScheme: "light dark" };

export default async function Layout({ children, params }: { children: ReactNode; params: Params }) {
  const language = await getLanguage(params);
  return <html lang={language} suppressHydrationWarning><head><script id="theme-init" dangerouslySetInnerHTML={{ __html: themeBootstrap }} /></head><body>{children}</body></html>;
}
