import type { TagItem } from "./types";

/**
 * feat-3 标签四分类：后端 tags.category CHECK（迁移 v22 / TagsHandler.cs:85）
 * 只允许 engine/gameplay/social/special，此处保持同一取值域。
 * 分组展示标签：social 组承载厂商/社团类标签（如 [ANIM]）。
 */
export const TAG_CATEGORIES = [
  { value: "engine", label: "引擎" },
  { value: "gameplay", label: "玩法" },
  { value: "social", label: "社团" },
  { value: "special", label: "特殊" },
] as const;

export type TagCategoryValue = (typeof TAG_CATEGORIES)[number]["value"];

/** 分类显示名；未知名原样回显（后端不可能产生，防御性兜底）。 */
export function categoryLabel(category?: string | null): string {
  const hit = TAG_CATEGORIES.find((item) => item.value === category);
  return hit?.label ?? category ?? "特殊";
}

/**
 * 标签所属分类：缺省按 kind 推断（engine→engine，其余→special），
 * 与后端迁移 v22 的存量回填口径一致（DatabaseMigrations.cs:401）。
 */
export function tagCategory(tag: Pick<TagItem, "kind" | "category">): TagCategoryValue {
  const category = tag.category?.trim();
  if (category && (TAG_CATEGORIES as readonly { value: string }[]).some((item) => item.value === category)) {
    return category as TagCategoryValue;
  }
  return tag.kind === "engine" ? "engine" : "special";
}

/**
 * UI 展示名优先 displayName：engine 标签 name 是身份键不可变（扫描识别、Suppress/Reset
 * 均按 (kind,name) 匹配），改名落 display_name（TagsHandler.cs:169）；user 标签两者相等。
 */
export function tagLabel(tag: Pick<TagItem, "name" | "displayName">): string {
  const display = tag.displayName?.trim();
  return display ? display : tag.name;
}

/**
 * 组内排序（TagsPanel/DetailSheet 共用）：星级降序置顶（0=无评分垫底）→ sortOrder
 * 升序 → 名称不区分大小写字典序（后端 ListTags 固定 kind+name NOCASE，sort_order
 * 不参与 SQL 排序，TagStore.cs:50，顺序完全由前端消费 sortOrder 决定）。
 */
export function compareTags(a: TagItem, b: TagItem): number {
  const aStarred = a.starred ?? 0;
  const bStarred = b.starred ?? 0;
  if (aStarred !== bStarred) return bStarred - aStarred;
  const order = (a.sortOrder ?? 0) - (b.sortOrder ?? 0);
  if (order !== 0) return order;
  return a.name.toLowerCase().localeCompare(b.name.toLowerCase());
}

/** tags.create/update 的 color 校验（#RRGGBB，TagsHandler.cs:72 同一正则）。 */
export function isHexColor(value: string): boolean {
  return /^#[0-9a-fA-F]{6}$/.test(value);
}

/** 预设色板（feat-3 颜色编辑）：深色主题下可辨识的中饱和度色。 */
export const TAG_PALETTE: readonly string[] = [
  "#f87171", "#fb923c", "#facc15", "#4ade80", "#2dd4bf",
  "#38bdf8", "#818cf8", "#c084fc", "#f472b6", "#94a3b8",
];

/** 按 TAG_CATEGORIES 固定顺序分组并组内排序；空分组保留（调用方决定是否渲染）。 */
export function groupTagsByCategory(tags: TagItem[]): Array<{ value: TagCategoryValue; label: string; items: TagItem[] }> {
  return TAG_CATEGORIES.map((category) => ({
    value: category.value,
    label: category.label,
    items: tags.filter((tag) => tagCategory(tag) === category.value).sort(compareTags),
  }));
}
