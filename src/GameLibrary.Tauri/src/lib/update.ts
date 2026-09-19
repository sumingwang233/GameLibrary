import { getVersion } from "@tauri-apps/api/app";

const REPO = "sumingwang233/GameLibrary";
const LATEST_URL = `https://api.github.com/repos/${REPO}/releases/latest`;

export interface UpdateInfo {
  current: string;
  latest: string;
  available: boolean;
  url: string;
  publishedAt?: string;
}

function parse(version: string): number[] {
  return version
    .trim()
    .replace(/^v/i, "")
    .split(/[.+]/)[0]
    .split(".")
    .map((part) => Number.parseInt(part, 10) || 0);
}

/** 与 Desktop/GitHubReleaseChecker.cs 相同的语义比较：逐段数值比较，缺位按 0。 */
export function compareVersions(left: string, right: string): number {
  const a = parse(left);
  const b = parse(right);
  const length = Math.max(a.length, b.length);
  for (let index = 0; index < length; index += 1) {
    const diff = (a[index] ?? 0) - (b[index] ?? 0);
    if (diff !== 0) return diff < 0 ? -1 : 1;
  }
  return 0;
}

/**
 * 检查 GitHub 上的最新 Release。保持 WPF 版的「提醒式」行为：只提示并跳转浏览器，
 * 不自动下载、不自动替换文件——未签名的自动更新在 SmartScreen 下体验更差。
 */
export async function checkForUpdate(): Promise<UpdateInfo> {
  const current = await getVersion().catch(() => "0.0.0");
  const response = await fetch(LATEST_URL, {
    headers: { Accept: "application/vnd.github+json" },
  });
  if (!response.ok) {
    throw new Error(`GitHub 返回 ${response.status}，无法检查更新`);
  }

  const payload = (await response.json()) as {
    tag_name?: string;
    html_url?: string;
    published_at?: string;
  };
  const latest = (payload.tag_name ?? "").replace(/^v/i, "");
  if (!latest) throw new Error("GitHub 响应中没有 tag_name");

  return {
    current,
    latest,
    available: compareVersions(current, latest) < 0,
    url: payload.html_url ?? `https://github.com/${REPO}/releases/latest`,
    publishedAt: payload.published_at,
  };
}
