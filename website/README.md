# GameLibrary website

Chinese `/` and English `/en/` promotional pages built with Next.js, React, and Tailwind. Production exports to `out/` with the `/GameLibrary` project prefix; GitHub Pages needs no Node.js server. Development uses the root path. See [Next.js static exports](https://nextjs.org/docs/app/guides/static-exports) and [basePath](https://nextjs.org/docs/app/api-reference/config/next-config-js/basePath).

## Check and preview

From the repository root (Node.js 24 and Python 3):

```powershell
npm --prefix website ci
npm --prefix website run build
npm --prefix website run typecheck
npm --prefix website test
python scripts/check_website.py --self-test
python scripts/check_website.py
npm --prefix website run dev
```

Open `http://127.0.0.1:4173/` or `/en/`. The image generation step runs before development and production builds. It derives 440, 880, and 1320 pixel WebP previews from the original PNG screenshots. Generated previews, build output, and dependencies are ignored. Production output is served at `/GameLibrary/`, including `/GameLibrary/en/`.

## Glass components

`lib/glass.ts` supplies the shared treatment: `bg-white/10 dark:bg-black/10 backdrop-blur-2xl border border-white/20 shadow-lg`. `components/GlassControls.tsx` uses it for the screenshot modal, language and appearance dropdowns, navigation sidebar, and local-data tooltip. Native dialogs handle modal behavior and Escape dismissal; dropdowns use native `details`. Color comes from radial-gradient orbs behind the surfaces, animated with transforms. Reduced motion disables animation; reduced transparency and higher contrast replace glass with opaque surfaces. No analytics or remote fonts are included.

The header appearance button offers **Dark**, **Light**, and **System**. System is the default. A browser-local preference survives refreshes and language changes; system changes apply immediately when System is selected. The same theme function runs before page content paints and from the selector. Invalid or unavailable storage falls back to System. No theme dependency is needed. `scripts/theme.test.mjs` uses Node's built-in test runner to cover the initial theme, explicit overrides, both system preferences, invalid values, and blocked storage.

## Publishing

The target is `https://sumingwang233.github.io/GameLibrary/`. The repository currently needs Pages enabled with **GitHub Actions** as its source. `.github/workflows/website.yml` installs, builds, typechecks, validates, and uploads only `website/out`. Pull requests validate without deploying; approved changes on `main` publish through GitHub Actions.

Source pushes and deployment require the owner's explicit authorization. The v1.6.0 repository sync includes the website source; it does not enable Pages or deploy it. Pushes run website checks; deployment requires an explicitly approved workflow_dispatch with publish=true. Validate the actual HTTPS address, both language pages, assets, and release links after deployment.

## Content and assets

- Download links describe public stable release **v1.5.5**. When changing the version, update both pages together and verify package names against the public GitHub release. The latest-release link remains available separately.
- The app icon is the 128px derivative from `src/GameLibrary.Tauri/src-tauri/icons/128x128.png`; its master is `src/GameLibrary.Tauri/app-icon.png`. It matches the desktop and tray icons.
- `public/assets/library.png` and `public/assets/tags.png` are captures of the actual desktop app in an isolated, synthetic sample library. Covers use the app's defaults; no real game files or third-party cover art are included. The desktop interface is Chinese; the English page states this explicitly. Modal enlargement uses the original PNGs.
- Capture the app without user directories, private paths, or personal collection data. Never fabricate UI or imply that sample entries represent included games.
- Preserve the HTML image dimensions when replacing screenshots, or update them to the actual PNG dimensions. Run the offline gate afterward.

## Validation — 2026-09-30

- Production export, TypeScript check, offline gate and its negative regression checks pass: both languages, navigation anchors, assets, responsive image sources, PNG dimensions, canonical/hreflang metadata, matching release links, sitemap, accessible dialog names, and all four glass surfaces. Production dependency audit reports no known vulnerabilities.
- Chinese and English layouts checked at 320, 375, 768, and 1440 CSS pixels: no horizontal overflow or broken images.
- `/GameLibrary/` and `/GameLibrary/en/` verified with a local export preview, including stylesheet, images, and both directions of language navigation.
- Screenshot modal and sidebar open, close with Escape, and restore focus. Modal opening locks page scrolling. Sidebar links close the sheet and navigate to their section. Language dropdown supports keyboard activation and Escape. Tooltip opens on focus or click and dismisses with Escape. Native FAQ remains keyboard accessible.
- Reduced-motion emulation stops all three orbs and changes scrolling to `auto`. Reduced-transparency emulation produces opaque surfaces with no backdrop blur; a regression check verifies the reset survives production CSS compilation.
- Appearance switching, dark-mode refresh persistence, system-change events, and shared language preference were checked in the browser. The 320px header still fits. The light export scores 100 for accessibility, best practices, and SEO; report: `artifacts/website-liquid-glass/lighthouse-theme-light.json`.
- The redesigned English page's 200% zoom check remains **unverified**: the earlier browser permission denial was respected. No alternate route was used to perform that denied check.
- Download links retain the verified public v1.5.5 release: three packages and checksums, with a separate latest-release link.

Lighthouse **13.5.0**, mobile simulation against the local static server:

| Page | Performance | Accessibility | Best practices | SEO | LCP | CLS | TBT |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Chinese | 87 | 100 | 100 | 100 | 4.1 s | 0 | 20 ms |
| English | 87 | 100 | 100 | 100 | 4.1 s | 0 | 20 ms |

These are local lab measurements, not field performance or a deployed-site audit. INP requires real interactions and field data; TBT is reported separately. The local Python server provides no compression or production cache headers; the export includes Next.js hydration JavaScript. Live-site performance has not been measured.

Local evidence (ignored build artifacts): `artifacts/website-liquid-glass/lighthouse-{cn,en}.report.{html,json}`, `desktop-cn.jpg`, `mobile-cn.jpg`, and `sidebar.jpg`. The application capture used its own data directory and stopped the demo app afterward; the existing installed app remains separate.

**Publication is pending final owner approval.** No website commit, push, Pages configuration change, or production deployment has been performed. After approval, publish the website-only changes and verify the live HTTPS URL, both languages, images, and download links.
