import { useMemo, useState } from "react";
import { ChevronDown, ChevronUp, Palette, Pencil, Plus, RotateCcw, Star, Trash2 } from "lucide-react";
import {
  TAG_CATEGORIES,
  TAG_PALETTE,
  groupTagsByCategory,
  isHexColor,
  tagCategory,
  tagLabel,
  type TagCategoryValue,
} from "../lib/tags";
import type { TagItem } from "../lib/types";
import { cn } from "../lib/utils";
import type { TagChanges } from "../lib/state";
import { Button } from "./ui/button";
import { ConfirmDialog } from "./ui/confirm-dialog";
import { Input } from "./ui/input";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "./ui/select";

/**
 * ui-5 分组标题文案：engine 组显示“自动识别标签”（原“引擎标签（自动识别）”，
 * feat-3 重构后分组标题统一走 TAG_CATEGORIES.label，故在此处覆盖，不动 lib/tags.ts）。
 */
const groupHeadingLabel = (group: { value: TagCategoryValue; label: string }) =>
  group.value === "engine" ? "自动识别标签" : group.label;

/**
 * 标签管理（feat-3）：按四分类（引擎/玩法/社团/特殊）分组展示与筛选；
 * 每个标签支持颜色（预设色板 + 自定义 #RRGGBB）、组内上移/下移（写 sortOrder，
 * 星标置顶）、星级（starred）与重命名（engine 标签走 displayName，state.ts 分发）。
 * v1.1.5 只有 tags.create（且用 window.prompt），tags.update / tags.remove 后端早已
 * 实现却完全没有入口——正是用户在 2026-09-14 提过、WPF 已做过的能力在重写中丢失。
 */
export function TagsPanel({
  tags,
  onCreate,
  onRename,
  onUpdate,
  onReorder,
  onRemove,
}: {
  tags: TagItem[];
  onCreate: (name: string, category?: string) => Promise<void>;
  /** engine 标签的改名参数（displayName）由 state.ts 按 kind 分发。 */
  onRename: (tag: TagItem, name: string) => Promise<void>;
  onUpdate: (tag: TagItem, changes: TagChanges) => Promise<void>;
  onReorder: (updates: Array<{ tag: TagItem; sortOrder: number }>) => Promise<void>;
  onRemove: (tag: TagItem) => Promise<void>;
}) {
  const [newName, setNewName] = useState("");
  const [newCategory, setNewCategory] = useState<TagCategoryValue>("special");
  const [filter, setFilter] = useState<TagCategoryValue | "all">("all");
  const [editing, setEditing] = useState<TagItem | null>(null);
  const [editName, setEditName] = useState("");
  const [removing, setRemoving] = useState<TagItem | null>(null);
  const [colorEditing, setColorEditing] = useState<TagItem | null>(null);
  const [busyTagId, setBusyTagId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const groups = useMemo(() => groupTagsByCategory(tags), [tags]);
  const visibleGroups = filter === "all" ? groups : groups.filter((group) => group.value === filter);

  const run = async (tagId: string | null, work: () => Promise<void>) => {
    setBusyTagId(tagId);
    setError(null);
    try {
      await work();
      return true;
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "操作失败");
      return false;
    } finally {
      setBusyTagId(null);
    }
  };

  const create = async () => {
    const name = newName.trim();
    if (!name) return;
    if (await run(null, () => onCreate(name, newCategory))) setNewName("");
  };

  const commitRename = async (tag: TagItem) => {
    const name = editName.trim();
    if (!name || name === tagLabel(tag)) {
      setEditing(null);
      return;
    }
    if (await run(tag.tagId, () => onRename(tag, name))) setEditing(null);
  };

  /** 恢复 engine 标签的本名：清除 displayName（TagsHandler 对 JSON null 走 clearDisplayName）。 */
  const clearDisplayName = (tag: TagItem) =>
    void run(tag.tagId, async () => {
      await onUpdate(tag, { displayName: null });
      setEditing(null);
    });

  const toggleStarred = (tag: TagItem) =>
    void run(tag.tagId, () => onUpdate(tag, { starred: !(tag.starred ?? false) }));

  const applyColor = (tag: TagItem, color: string) =>
    void run(tag.tagId, async () => {
      await onUpdate(tag, { color });
      setColorEditing(null);
    });

  /**
   * 组内上移/下移：在与该标签同一星标分区（星标恒置顶，跨分区交换会被置顶规则吃掉）
   * 内交换相邻位次，然后把分区按新视觉顺序整体重排为 0..n-1——只写 sortOrder 发生
   * 变化的条目（首若干次移动后进入稳态，此后每次只写两条）。
   */
  const moveTag = (tag: TagItem, direction: -1 | 1) => {
    const group = groups.find((item) => item.value === tagCategory(tag))?.items ?? [];
    const partition = group.filter((item) => (item.starred ?? false) === (tag.starred ?? false));
    const index = partition.findIndex((item) => item.tagId === tag.tagId);
    const target = index + direction;
    if (index < 0 || target < 0 || target >= partition.length) return;

    const reordered = [...partition];
    const [moved] = reordered.splice(index, 1);
    reordered.splice(target, 0, moved);
    const updates = reordered
      .map((item, order) => ({ tag: item, sortOrder: order }))
      .filter((update) => (update.tag.sortOrder ?? 0) !== update.sortOrder);
    if (updates.length === 0) return;

    void run(tag.tagId, () => onReorder(updates));
  };

  return (
    <section className="space-y-6">
      <div className="flex flex-wrap items-center gap-2">
        <Input
          value={newName}
          onChange={(event) => setNewName(event.currentTarget.value)}
          onKeyDown={(event) => {
            if (event.key === "Enter") void create();
          }}
          placeholder="新标签名称"
          aria-label="新标签名称"
          className="max-w-xs"
        />
        <Select value={newCategory} onValueChange={(value) => setNewCategory(value as TagCategoryValue)}>
          <SelectTrigger className="w-[130px]" aria-label="新标签分类">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {TAG_CATEGORIES.map((category) => (
              <SelectItem key={category.value} value={category.value}>
                {category.label}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        <Button onClick={() => void create()} disabled={!newName.trim()}>
          <Plus size={16} />
          新建标签
        </Button>
      </div>

      {/* 分类筛选：默认全部；点某一分类只看该组。 */}
      <div role="group" aria-label="分类筛选" className="flex flex-wrap gap-2">
        <Button
          size="sm"
          variant={filter === "all" ? "default" : "outline"}
          aria-pressed={filter === "all"}
          onClick={() => setFilter("all")}
        >
          全部 · {tags.length}
        </Button>
        {groups.map((group) => (
          <Button
            key={group.value}
            size="sm"
            variant={filter === group.value ? "default" : "outline"}
            aria-pressed={filter === group.value}
            onClick={() => setFilter(filter === group.value ? "all" : group.value)}
          >
            {group.label} · {group.items.length}
          </Button>
        ))}
      </div>

      {error && (
        <p role="alert" className="rounded-md border border-danger/40 bg-danger/10 p-3 text-sm text-danger">
          {error}
        </p>
      )}

      {visibleGroups.map((group) => (
        <div key={group.value}>
          <h2 className="mb-2 text-xs font-semibold tracking-[0.16em] text-text-secondary uppercase">
            {groupHeadingLabel(group)} · {group.items.length}
          </h2>
          {group.items.length === 0 ? (
            <p className="text-sm text-text-secondary">无</p>
          ) : (
            <ul className="space-y-1">
              {group.items.map((tag) => {
                const busy = busyTagId === tag.tagId;
                const isEditing = editing?.tagId === tag.tagId;
                return (
                  <li
                    key={tag.tagId}
                    className="relative flex items-center gap-2 rounded-md border border-border bg-surface px-3 py-2"
                  >
                    {isEditing ? (
                      <>
                        <Input
                          value={editName}
                          onChange={(event) => setEditName(event.currentTarget.value)}
                          onKeyDown={(event) => {
                            if (event.key === "Enter") void commitRename(tag);
                            if (event.key === "Escape") setEditing(null);
                          }}
                          aria-label={`重命名标签 ${tagLabel(tag)}`}
                          className="h-8 max-w-xs"
                          autoFocus
                        />
                        <Button size="sm" disabled={busy || !editName.trim()} onClick={() => void commitRename(tag)}>
                          保存
                        </Button>
                        <Button size="sm" variant="ghost" onClick={() => setEditing(null)}>
                          取消
                        </Button>
                        {tag.kind === "engine" && tag.displayName && (
                          <Button
                            size="sm"
                            variant="ghost"
                            disabled={busy}
                            aria-label={`恢复本名 ${tag.name}`}
                            title={`恢复本名 ${tag.name}`}
                            onClick={() => clearDisplayName(tag)}
                          >
                            <RotateCcw size={14} />
                            恢复本名
                          </Button>
                        )}
                      </>
                    ) : (
                      <>
                        <span
                          aria-hidden="true"
                          className="size-3 shrink-0 rounded-full border border-border"
                          style={{ backgroundColor: tag.color ?? "transparent" }}
                        />
                        <span className="min-w-0 flex-1 truncate text-sm text-text-primary" title={tag.name === tagLabel(tag) ? undefined : `身份键：${tag.name}`}>
                          {tagLabel(tag)}
                        </span>
                        <span className="shrink-0 text-xs text-text-secondary">
                          {tag.gameCount ? `${tag.gameCount.toLocaleString()} 个游戏` : ""}
                        </span>
                        <Button
                          size="icon"
                          variant="ghost"
                          disabled={busy}
                          aria-label={`${tag.starred ? "取消星标" : "星标"} ${tagLabel(tag)}`}
                          aria-pressed={tag.starred ?? false}
                          title={tag.starred ? "取消星标" : "星标（组内置顶）"}
                          onClick={() => toggleStarred(tag)}
                        >
                          <Star size={14} className={tag.starred ? "fill-favorite text-favorite" : undefined} />
                        </Button>
                        <Button
                          size="icon"
                          variant="ghost"
                          disabled={busy}
                          aria-label={`上移 ${tagLabel(tag)}`}
                          title="组内上移"
                          onClick={() => moveTag(tag, -1)}
                        >
                          <ChevronUp size={14} />
                        </Button>
                        <Button
                          size="icon"
                          variant="ghost"
                          disabled={busy}
                          aria-label={`下移 ${tagLabel(tag)}`}
                          title="组内下移"
                          onClick={() => moveTag(tag, 1)}
                        >
                          <ChevronDown size={14} />
                        </Button>
                        <Button
                          size="icon"
                          variant="ghost"
                          disabled={busy}
                          aria-label={`编辑颜色 ${tagLabel(tag)}`}
                          title="编辑颜色"
                          onClick={() => setColorEditing(colorEditing?.tagId === tag.tagId ? null : tag)}
                        >
                          <Palette size={14} />
                        </Button>
                        <Button
                          size="icon"
                          variant="ghost"
                          disabled={busy}
                          aria-label={`重命名 ${tagLabel(tag)}`}
                          title="重命名"
                          onClick={() => {
                            setEditing(tag);
                            setEditName(tagLabel(tag));
                          }}
                        >
                          <Pencil size={14} />
                        </Button>
                        <Button
                          size="icon"
                          variant="ghost"
                          disabled={busy}
                          aria-label={`删除 ${tagLabel(tag)}`}
                          title="删除"
                          onClick={() => setRemoving(tag)}
                        >
                          <Trash2 size={14} />
                        </Button>
                      </>
                    )}
                    {colorEditing?.tagId === tag.tagId && !isEditing && (
                      <TagColorPopover
                        tag={tag}
                        busy={busy}
                        onClose={() => setColorEditing(null)}
                        onApply={(color) => applyColor(tag, color)}
                      />
                    )}
                  </li>
                );
              })}
            </ul>
          )}
        </div>
      ))}

      <ConfirmDialog
        open={removing !== null}
        title={`删除标签「${removing ? tagLabel(removing) : ""}」`}
        description={
          removing?.kind === "engine"
            ? "引擎标签删除后会登记 suppress 覆盖，避免下次扫描又把它加回来。"
            : "该标签会从所有游戏上解除关联。此操作不可撤销。"
        }
        confirmLabel="删除"
        destructive
        onCancel={() => setRemoving(null)}
        onConfirm={() => {
          const target = removing;
          setRemoving(null);
          if (target) void run(target.tagId, () => onRemove(target));
        }}
      />
    </section>
  );
}

/** 颜色编辑浮层：预设色板一击即用；自定义走原生取色器或 #RRGGBB 文本（TagsHandler 校验同一格式）。 */
function TagColorPopover({
  tag,
  busy,
  onClose,
  onApply,
}: {
  tag: TagItem;
  busy: boolean;
  onClose: () => void;
  onApply: (color: string) => void;
}) {
  const [draft, setDraft] = useState(tag.color ?? "#38bdf8");
  const valid = isHexColor(draft);
  return (
    <>
      <button
        type="button"
        aria-label="关闭颜色选择"
        className={cn("fixed inset-0 z-10 cursor-default", busy && "hidden")}
        onClick={onClose}
      />
      <div className="absolute top-full right-2 z-20 mt-1 w-64 rounded-md border border-border bg-panel p-3 shadow-2xl">
        <div className="grid grid-cols-5 gap-2">
          {TAG_PALETTE.map((color) => (
            <button
              key={color}
              type="button"
              disabled={busy}
              aria-label={`使用颜色 ${color}`}
              title={color}
              className="size-6 cursor-pointer rounded-md border border-border transition hover:scale-110 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
              style={{ backgroundColor: color }}
              onClick={() => onApply(color)}
            />
          ))}
        </div>
        <div className="mt-3 flex items-center gap-2">
          <input
            type="color"
            aria-label="自定义颜色"
            value={valid ? draft : "#38bdf8"}
            disabled={busy}
            onChange={(event) => setDraft(event.currentTarget.value)}
            className="size-8 shrink-0 cursor-pointer rounded border border-border bg-transparent"
          />
          <Input
            value={draft}
            disabled={busy}
            onChange={(event) => setDraft(event.currentTarget.value)}
            aria-label="自定义颜色（#RRGGBB）"
            className="h-8 flex-1"
          />
          <Button size="sm" disabled={busy || !valid} onClick={() => onApply(draft.toLowerCase())}>
            应用
          </Button>
        </div>
        {!valid && <p className="mt-2 text-xs text-danger">颜色须为 #RRGGBB 格式</p>}
      </div>
    </>
  );
}
