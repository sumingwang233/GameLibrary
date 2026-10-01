"use client";

import { useEffect, useId, useRef, useState } from "react";
import { glass } from "../lib/glass";
import { asset, home, repository, type Language } from "../lib/site";
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
  const en = language === "en";
  const sidebar = useRef<HTMLDialogElement>(null);
  const menu = useRef<HTMLDetailsElement>(null);
  const themeMenu = useRef<HTMLDetailsElement>(null);
  const [expanded, setExpanded] = useState(false);
  const [theme, setTheme] = useState<Theme>("system");
  const [themeExpanded, setThemeExpanded] = useState(false);
  const themeLabels = { dark: en ? "Dark" : "深色", light: en ? "Light" : "浅色", system: en ? "System" : "跟随系统" };
  const sections = [
    ["experience", en ? "Scan & review" : "扫描与审核"],
    ["collection", en ? "Your collection" : "整理游戏"],
    ["safety", en ? "Local data" : "本地数据"],
    ["download", en ? "Downloads" : "下载"],
    ["questions", en ? "Common questions" : "常见问题"],
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
      <a className="brand" href={home(language)} aria-label={en ? "GameLibrary home" : "GameLibrary 首页"}><img src={asset("icon.png")} width="34" height="34" alt="" />GameLibrary</a>
      <nav aria-label={en ? "Main navigation" : "主导航"}>
        <a className="nav-section" href="#experience">{en ? "Experience" : "体验"}</a>
        <a className="nav-section" href="#questions">{en ? "FAQ" : "常见问题"}</a>
        <details ref={menu} className="language-menu" onToggle={event => { setExpanded(event.currentTarget.open); if (event.currentTarget.open) themeMenu.current?.removeAttribute("open"); }}>
          <summary aria-label={en ? "Choose language, current: EN" : "选择语言，当前为中文"} aria-expanded={expanded}>{en ? "EN" : "中文"}<Icon kind="down" /></summary>
          <div className={glass("language-dropdown")}>
            <p>{en ? "Language" : "语言"}</p>
            <a href={home("zh-CN")} lang="zh-CN" hrefLang="zh-CN" aria-current={!en ? "page" : undefined}>中文 <span aria-hidden="true">{!en && "✓"}</span></a>
            <a href={home("en")} lang="en" hrefLang="en" aria-current={en ? "page" : undefined}>English <span aria-hidden="true">{en && "✓"}</span></a>
          </div>
        </details>
        <details ref={themeMenu} className="theme-menu" onToggle={event => { setThemeExpanded(event.currentTarget.open); if (event.currentTarget.open) menu.current?.removeAttribute("open"); }}>
          <summary className="icon-button" aria-expanded={themeExpanded} aria-label={`${en ? "Appearance" : "外观"}：${themeLabels[theme]}`} title={`${en ? "Appearance" : "外观"}：${themeLabels[theme]}`}><Icon kind="appearance" /></summary>
          <div className={glass("theme-dropdown")}><fieldset>
            <legend>{en ? "Appearance" : "外观"}</legend>
            {(["dark", "light", "system"] as const).map(value => <label key={value}><input type="radio" name="appearance" value={value} checked={theme === value} onChange={() => {
              setTheme(applyTheme(value, matchMedia("(prefers-color-scheme: dark)").matches));
              try { localStorage.setItem(themeKey, value); } catch { /* The selected appearance still works when storage is unavailable. */ }
              themeMenu.current?.removeAttribute("open");
              themeMenu.current?.querySelector("summary")?.focus();
            }} />{themeLabels[value]}</label>)}
          </fieldset></div>
        </details>
        <a className="button button-small nav-download" href="#download">{en ? "Download" : "下载 Windows 版"}</a>
        <button className="icon-button" aria-label={en ? "Open navigation" : "打开导航"} onClick={() => sidebar.current?.showModal()}><Icon kind="menu" /></button>
      </nav>
    </header>
    <dialog ref={sidebar} className={glass("sidebar")} aria-labelledby="sidebar-title" onClick={event => { if (event.target === event.currentTarget) sidebar.current?.close(); }}>
      <div className="sidebar-inner">
        <div className="overlay-heading"><span id="sidebar-title">{en ? "On this page" : "页面导航"}</span><button className="icon-button" aria-label={en ? "Close navigation" : "关闭导航"} onClick={() => sidebar.current?.close()} autoFocus><Icon kind="close" /></button></div>
        <a className="brand" href={home(language)}><img src={asset("icon.png")} width="46" height="46" alt="" />GameLibrary</a>
        <nav aria-label={en ? "Section navigation" : "章节导航"}>{sections.map(([id, label], index) => <a href={`#${id}`} key={id} onClick={() => sidebar.current?.close()}><span aria-hidden="true">0{index + 1}</span>{label}</a>)}</nav>
        <p>Windows 10 / 11 · x64</p>
        <a className="text-link" href={repository}>GitHub ↗</a>
      </div>
    </dialog>
  </>;
}

export function LocalTooltip({ language }: { language: Language }) {
  const id = useId();
  const [visible, setVisible] = useState(false);
  const en = language === "en";
  return <span className="tooltip-wrap" onMouseEnter={() => setVisible(true)} onMouseLeave={event => { if (!event.currentTarget.contains(document.activeElement)) setVisible(false); }}>
    <button className="tooltip-trigger" aria-describedby={visible ? id : undefined} onFocus={() => setVisible(true)} onBlur={() => setVisible(false)} onClick={() => setVisible(true)} onKeyDown={event => { if (event.key === "Escape") { setVisible(false); event.stopPropagation(); } }}>{en ? "Local first" : "本地优先"}<Icon kind="info" /></button>
    <span id={id} role="tooltip" className={glass("tooltip")} hidden={!visible}>{en ? "Library data is saved on this PC. No account is required to manage your games." : "游戏库数据保存在本机，管理游戏无需注册账号。"}</span>
  </span>;
}

export function Screenshot({ name, alt, caption, language, priority = false }: { name: string; alt: string; caption: string; language: Language; priority?: boolean }) {
  const modal = useRef<HTMLDialogElement>(null);
  const id = useId();
  const en = language === "en";
  return <>
    <figure className={glass(priority ? "screenshot hero-image" : "screenshot secondary-image")}>
      <button className="screenshot-button" aria-label={en ? `Enlarge: ${caption}` : `放大查看：${caption}`} onClick={() => modal.current?.showModal()}>
        <img src={asset(name.replace(".png", "-1320.webp"))} srcSet={[440, 880, 1320].map(size => `${asset(name.replace(".png", `-${size}.webp`))} ${size}w`).join(", ")} sizes={priority ? "(max-width: 760px) calc(100vw - 52px), (max-width: 1000px) calc(100vw - 84px), (max-width: 1312px) calc(100vw - 132px), 1180px" : "(max-width: 760px) calc(100vw - 56px), (max-width: 1000px) 50vw, 720px"} width="1320" height="820" loading={priority ? "eager" : "lazy"} fetchPriority={priority ? "high" : undefined} decoding="async" alt={alt} />
        <span className={glass("expand-hint")} aria-hidden="true"><Icon kind="expand" /></span>
      </button>
      <figcaption><span className="caption-dot" aria-hidden="true" />{caption}<span className="caption-action" aria-hidden="true">{en ? "Click to enlarge" : "点击放大"}</span></figcaption>
    </figure>
    <dialog ref={modal} className={glass("screenshot-modal")} aria-labelledby={id} onClick={event => { if (event.target === event.currentTarget) modal.current?.close(); }}>
      <div className="modal-inner">
        <div className="overlay-heading"><span id={id}>{caption}</span><button className="icon-button" aria-label={en ? "Close screenshot" : "关闭截图"} autoFocus onClick={() => modal.current?.close()}><Icon kind="close" /></button></div>
        <img src={asset(name)} width="1320" height="820" loading="lazy" alt={alt} />
      </div>
    </dialog>
  </>;
}
