// GitHub Pages serves this export under /GameLibrary. Vercel serves it at the domain root.
const sitePrefix = process.env.NODE_ENV === "production" && !process.env.VERCEL ? "/GameLibrary" : "";

/** @type {import('next').NextConfig} */
const config = {
  output: "export",
  trailingSlash: true,
  basePath: sitePrefix,
  // Compile this prefix into the server render and the browser bundle together.
  // Next's `env` post-compile pass drops an empty string, so on Vercel the
  // browser would otherwise disagree with the rendered markup.
  compiler: {
    define: {
      "process.env.NEXT_PUBLIC_SITE_PREFIX": sitePrefix,
    },
  },
  distDir: process.env.NODE_ENV === "production" ? ".next" : ".next-dev",
  devIndicators: false,
  images: { unoptimized: true },
  poweredByHeader: false,
  agentRules: false,
};

export default config;
