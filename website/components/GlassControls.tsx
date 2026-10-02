"use client";

import { useEffect, useId, useRef, useState } from "react";
import { glass } from "../lib/glass";
import { asset, home, languages, languageNames, repository, translate, type Language } from "../lib/site";
import { applyTheme, themeKey, type Theme } from "../lib/theme";

function Icon({ kind }: { kind: "menu" | "close" | "down" | "expand" | "info" | "appearance" }) {
  const paths = {
    menu: "M4 6h16M4 12h16M4 18h16",
    close: "m6 6 12 12M6 18 18 6",
    down: "m6 9 6 6 6-6",
    expand: "M8 3H3v5m13-5h5v5M3 16v5h5m13-5v5h-5",
    info: "M12 11v6m0-10v.1",
    appearance: "M12 3a9 9 0 0 1 0 18Z",
  };
  return <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">{(kind === "info" || kind === "appearance") && <circle cx="12" cy="12" r="9" />}<path d={paths[kind]} fill={kind === "appearance" ? "currentColor" : "none"} /></svg>;
}

export function Header({ language }: { language: Language }) {
  const t = translate(language);
  const sidebar = useRef<HTMLDialogElement>(null);
  const menu = useRef<HTMLDetailsElement>(null);
  const themeMenu = useRef<HTMLDetailsElement>(null);
  const [expanded, setExpanded] = useState(false);
  const [theme, setTheme] = useState<Theme>("light");
  const [themeExpanded, setThemeExpanded] = useState(false);
  const themeLabels = { dark: t("深色", "深色", "Dark", "ダーク"), light: t("浅色", "淺色", "Light", "ライト"), system: t("跟随系统", "跟隨系統", "System", "システム") };
  const sections = [
    ["experience", t("体验", "體驗", "Experience", "使い方")],
    ["collection", t("收藏", "收藏", "Collection", "コレクション")],
    ["launch", t("启动", "啟動", "Launch", "起動")],
    ["safety", t("本地数据", "本機資料", "Local data", "ローカルデータ")],
    ["download", t("下载", "下載", "Download", "ダウンロード")],
    ["questions", "FAQ"],
  ];

  useEffect(() => {
    const media = matchMedia("(prefers-color-scheme: dark)");
    const sync = (value: unknown) => setTheme(applyTheme(value, media.matches));
    sync(document.documentElement.dataset.theme);
    const followSystem = () => sync(document.documentElement.dataset.theme);
    const storageChanged = (event: StorageEvent) => {
      if (event.key === themeKey || event.key === null) sync(event.newValue);
    };
    media.addEventListener("change", followSystem);
    window.addEventListener("storage", storageChanged);
    const closeMenu = (event: PointerEvent) => {
      for (const ref of [menu, themeMenu]) {
        if (event.target instanceof Node && !ref.current?.contains(event.target)) ref.current?.removeAttribute("open");
      }
    };
    const escape = (event: KeyboardEvent) => {
      for (const ref of [menu, themeMenu]) {
        if (event.key === "Escape" && ref.current?.open) {
          ref.current.removeAttribute("open");
          ref.current.querySelector("summary")?.focus();
        }
      }
    };
    document.addEventListener("pointerdown", closeMenu);
    document.addEventListener("keydown", escape);
    return () => {
      document.removeEventListener("pointerdown", closeMenu);
      document.removeEventListener("keydown", escape);
      media.removeEventListener("change", followSystem);
      window.removeEventListener("storage", storageChanged);
    };
  }, []);

  return <>
    <header className={glass("site-header")}>
      <a className="brand" href={home(language)} aria-label={t("GameLibrary 首页", "GameLibrary 首頁", "GameLibrary home", "GameLibrary ホーム")}><img src={asset("icon.png")} width="40" height="40" alt="" />GameLibrary</a>
      <nav className="desktop-nav" aria-label={t("主导航", "主導覽", "Main navigation", "メインナビゲーション")}>{sections.map(([id, label]) => <a href={`#${id}`} key={id}>{label}</a>)}</nav>
      <div className="header-controls">
        <details ref={menu} className="language-menu" onToggle={event => { setExpanded(event.currentTarget.open); if (event.currentTarget.open) themeMenu.current?.removeAttribute("open"); }}>
          <summary aria-label={t("选择语言", "選擇語言", "Choose language", "言語を選択")} aria-expanded={expanded}>{t("简中", "繁中", "EN", "日本語")}<Icon kind="down" /></summary>
          <div className={glass("language-dropdown")}>
            <p>{t("语言", "語言", "Language", "言語")}</p>
            {languages.map(locale => <a key={locale} href={home(locale)} lang={locale} hrefLang={locale} aria-current={locale === language ? "page" : undefined}>{languageNames[locale]}<span aria-hidden="true">{locale === language && "✓"}</span></a>)}
          </div>
        </details>
        <details ref={themeMenu} className="theme-menu" onToggle={event => { setThemeExpanded(event.currentTarget.open); if (event.currentTarget.open) menu.current?.removeAttribute("open"); }}>
          <summary className="icon-button" aria-expanded={themeExpanded} aria-label={`${t("外观", "外觀", "Appearance", "外観")}：${themeLabels[theme]}`} title={`${t("外观", "外觀", "Appearance", "外観")}：${themeLabels[theme]}`}><Icon kind="appearance" /></summary>
          <div className={glass("theme-dropdown")}><fieldset>
            <legend>{t("外观", "外觀", "Appearance", "外観")}</legend>
            {(["light", "dark", "system"] as const).map(value => <label key={value}><input type="radio" name="appearance" value={value} checked={theme === value} onChange={() => {
              setTheme(applyTheme(value, matchMedia("(prefers-color-scheme: dark)").matches));
              try { localStorage.setItem(themeKey, value); } catch { /* The selected appearance still works when storage is unavailable. */ }
              themeMenu.current?.removeAttribute("open");
              themeMenu.current?.querySelector("summary")?.focus();
            }} />{themeLabels[value]}</label>)}
          </fieldset></div>
        </details>
        <a className="github-link" href={repository} aria-label="GitHub"><svg width="22" height="22" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path d="M12 2a10 10 0 0 0-3.16 19.49c.5.09.68-.22.68-.48v-1.86c-2.78.6-3.37-1.18-3.37-1.18-.45-1.15-1.11-1.46-1.11-1.46-.91-.62.07-.61.07-.61 1 .07 1.53 1.03 1.53 1.03.89 1.52 2.34 1.08 2.91.82.09-.65.35-1.08.64-1.33-2.22-.25-4.55-1.11-4.55-4.94 0-1.09.39-1.99 1.03-2.69-.1-.25-.45-1.27.1-2.65 0 0 .84-.27 2.75 1.03a9.6 9.6 0 0 1 5 0c1.91-1.3 2.75-1.03 2.75-1.03.55 1.38.2 2.4.1 2.65.64.7 1.03 1.6 1.03 2.69 0 3.84-2.34 4.68-4.57 4.93.36.31.68.92.68 1.85v2.75c0 .27.18.58.69.48A10 10 0 0 0 12 2Z" /></svg></a>
        <button className="icon-button mobile-toggle" aria-label={t("打开导航", "開啟導覽", "Open navigation", "ナビゲーションを開く")} onClick={() => sidebar.current?.showModal()}><Icon kind="menu" /></button>
      </div>
    </header>
    <dialog ref={sidebar} className={glass("sidebar")} aria-labelledby="sidebar-title" onClick={event => { if (event.target === event.currentTarget) sidebar.current?.close(); }}>
      <div className="sidebar-inner">
        <div className="overlay-heading"><span id="sidebar-title">{t("页面导航", "頁面導覽", "On this page", "ページ内ナビゲーション")}</span><button className="icon-button" aria-label={t("关闭导航", "關閉導覽", "Close navigation", "ナビゲーションを閉じる")} onClick={() => sidebar.current?.close()} autoFocus><Icon kind="close" /></button></div>
        <a className="brand" href={home(language)}><img src={asset("icon.png")} width="46" height="46" alt="" />GameLibrary</a>
        <nav aria-label={t("章节导航", "章節導覽", "Section navigation", "セクションナビゲーション")}>{sections.map(([id, label]) => <a href={`#${id}`} key={id} onClick={() => sidebar.current?.close()}>{label}<span aria-hidden="true">→</span></a>)}</nav>
        <p>Windows 10 / 11 · x64</p>
        <a className="text-link" href={repository}>GitHub ↗</a>
      </div>
    </dialog>
  </>;
}

export function LocalTooltip({ language }: { language: Language }) {
  const id = useId();
  const [visible, setVisible] = useState(false);
  const t = translate(language);
  return <span className="tooltip-wrap" onMouseEnter={() => setVisible(true)} onMouseLeave={event => { if (!event.currentTarget.contains(document.activeElement)) setVisible(false); }}>
    <button className="tooltip-trigger" aria-describedby={visible ? id : undefined} onFocus={() => setVisible(true)} onBlur={() => setVisible(false)} onClick={() => setVisible(true)} onKeyDown={event => { if (event.key === "Escape") { setVisible(false); event.stopPropagation(); } }}>{t("本地收藏，无需账号", "本機收藏，無需帳號", "Local collection. No account.", "ローカル管理・登録不要")}<Icon kind="info" /></button>
    <span id={id} role="tooltip" className={glass("tooltip")} hidden={!visible}>{t("游戏库数据保存在本机，管理游戏无需注册账号。", "遊戲庫資料儲存在本機，管理遊戲無需註冊帳號。", "Library data is saved on this PC. No account is required to manage your games.", "ゲームの登録情報はこの PC に保存されます。管理にアカウントは不要です。")}</span>
  </span>;
}

export function Screenshot({ name, alt, caption, language, priority = false }: { name: string; alt: string; caption: string; language: Language; priority?: boolean }) {
  const modal = useRef<HTMLDialogElement>(null);
  const id = useId();
  const t = translate(language);
  const [failed, setFailed] = useState(false);
  return <>
    <figure className={glass(priority ? "screenshot hero-image" : "screenshot secondary-image")}>
      <button className="screenshot-button" aria-label={`${t("放大查看", "放大檢視", "Enlarge", "拡大表示")}: ${caption}`} disabled={failed} onClick={() => modal.current?.showModal()}>
        {failed ? <span className="image-error" role="status">{t("截图加载失败，请稍后刷新。", "截圖載入失敗，請稍後重新整理。", "Screenshot could not load. Please refresh later.", "画像を読み込めませんでした。後で再読み込みしてください。")}</span> : <img src={asset(name.replace(".png", "-1320.webp"))} srcSet={[440, 880, 1320].map(size => `${asset(name.replace(".png", `-${size}.webp`))} ${size}w`).join(", ")} sizes={priority ? "(max-width: 760px) calc(100vw - 32px), (max-width: 1080px) 55vw, 780px" : "(max-width: 760px) calc(100vw - 32px), (max-width: 1080px) 52vw, 680px"} width="1320" height="820" loading={priority ? "eager" : "lazy"} fetchPriority={priority ? "high" : undefined} decoding="async" alt={alt} onError={() => setFailed(true)} />}
        <span className={glass("expand-hint")} aria-hidden="true"><Icon kind="expand" /></span>
      </button>
      <figcaption><span>{caption}</span><span className="caption-action" aria-hidden="true">{t("查看大图", "檢視大圖", "Enlarge image", "画像を拡大")} ↗</span></figcaption>
    </figure>
    <dialog ref={modal} className={glass("screenshot-modal")} aria-labelledby={id} onClick={event => { if (event.target === event.currentTarget) modal.current?.close(); }}>
      <div className="modal-inner">
        <div className="overlay-heading"><span id={id}>{caption}</span><button className="icon-button" aria-label={t("关闭截图", "關閉截圖", "Close screenshot", "画像を閉じる")} autoFocus onClick={() => modal.current?.close()}><Icon kind="close" /></button></div>
        <img src={asset(name)} width="1320" height="820" loading="lazy" alt={alt} />
      </div>
    </dialog>
  </>;
}
