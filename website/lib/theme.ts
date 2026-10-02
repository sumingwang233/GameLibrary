export type Theme = "dark" | "light" | "system";
export const themeKey = "gamelibrary-website-theme";

// Self-contained so the same function runs before paint and from the selector.
export function applyTheme(value: unknown, systemDark: boolean): Theme {
  const theme = value === "dark" || value === "system" ? value : "light";
  const dark = theme === "dark" || (theme === "system" && systemDark);
  const root = document.documentElement;
  root.classList.toggle("dark", dark);
  root.dataset.theme = theme;
  root.style.colorScheme = dark ? "dark" : "light";
  document.querySelector('meta[name="theme-color"]')?.setAttribute("content", dark ? "#151c21" : "#f4faff");
  return theme;
}

export const themeBootstrap = `(()=>{let saved;try{saved=localStorage.getItem(${JSON.stringify(themeKey)})}catch{}(${applyTheme.toString()})(saved,matchMedia('(prefers-color-scheme: dark)').matches)})()`;
