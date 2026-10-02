import MarketingPage from "../../../components/MarketingPage";
import { notFound } from "next/navigation";

export default async function Page({ params }: { params: Promise<{ locale: string }> }) {
  const { locale } = await params;
  if (locale !== "zh-TW" && locale !== "ja") notFound();
  return <MarketingPage language={locale} />;
}
