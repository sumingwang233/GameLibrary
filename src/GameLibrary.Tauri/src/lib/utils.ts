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

/**
 * 累计游玩时长（feat-1，GameDto.playtimeMinutes，分钟整数）：
 * ≥60 分钟显示"x.x 小时"（一位小数），1–59 分钟显示"n 分钟"，0/缺省返回空串（卡面不显示）。
 */
export function formatPlaytime(minutes?: number | null) {
  const value = Number.isFinite(minutes) ? Math.floor(minutes ?? 0) : 0;
  if (value <= 0) return "";
  if (value < 60) return `${value} 分钟`;
  return `${(value / 60).toFixed(1)} 小时`;
}
