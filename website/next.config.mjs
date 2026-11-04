/** @type {import('next').NextConfig} */
const config = {
  output: "export",
  trailingSlash: true,
  basePath: process.env.NODE_ENV === "production" && !process.env.VERCEL ? "/GameLibrary" : "",
  distDir: process.env.NODE_ENV === "production" ? ".next" : ".next-dev",
  devIndicators: false,
  images: { unoptimized: true },
  poweredByHeader: false,
  agentRules: false,
};

export default config;
