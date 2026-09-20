import { clsx, type ClassValue } from "clsx";
import { twMerge } from "tailwind-merge";

export function cn(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs));
}

/** 用户要求的时间格式：年-月-日 时:分:秒（本地时区）。 */
export function formatTime(value?: string | null) {
  if (!value) return "暂无记录";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "暂无记录";
  const pad = (part: number) => String(part).padStart(2, "0");
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ` +
    `${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`;
}

/** 相似度（0..1）转整数百分比文本：四舍五入并夹到 [0,100]，如 0.873 → "87%"。 */
export function formatSimilarity(value: number) {
  const percent = Math.min(100, Math.max(0, Math.round(value * 100)));
  return `${percent}%`;
}
