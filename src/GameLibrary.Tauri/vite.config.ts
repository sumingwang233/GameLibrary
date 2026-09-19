import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";
// @ts-expect-error type error without @types/node package
import process from "node:process";
const host = process.env.TAURI_DEV_HOST;

// Windows 下 new URL(...).pathname 形如 "/D:/a/b"，去掉前导斜杠才是可用的绝对路径。
const srcDir = new URL("./src", import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, "$1");

// https://vite.dev/config/
export default defineConfig(() => ({
  plugins: [react(), tailwindcss()],

  // tsconfig.json 与 components.json 都声明了 @/ 别名；Vite 侧必须同步配置，
  // 否则 `npx shadcn add <component>` 生成的 @/lib/utils 导入能通过 tsc 却在打包时解析失败。
  resolve: {
    alias: {
      "@": srcDir,
    },
  },

  // Vite options tailored for Tauri development and only applied in `tauri dev` or `tauri build`
  //
  // 1. prevent Vite from obscuring rust errors
  clearScreen: false,
  // 2. tauri expects a fixed port, fail if that port is not available
  server: {
    port: 1420,
    strictPort: true,
    host: host || false,
    hmr: host
      ? {
          protocol: "ws",
          host,
          port: 1421,
        }
      : undefined,
    watch: {
      // 3. tell Vite to ignore watching `src-tauri`
      ignored: ["**/src-tauri/**"],
    },
  },
}));
