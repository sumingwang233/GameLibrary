import { useState } from "react";
import { Pencil, Plus, Trash2 } from "lucide-react";
import type { TagItem } from "../lib/types";
import { Button } from "./ui/button";
import { ConfirmDialog } from "./ui/confirm-dialog";
import { Input } from "./ui/input";

/**
 * 标签管理。v1.1.5 只有 tags.create（且用 window.prompt），
 * tags.update / tags.remove 后端早已实现却完全没有入口——这正是
 * 用户在 2026-09-14 提过、WPF 已经做过的能力在重写中丢失的部分。
 */
export function TagsPanel({
  tags,
  onCreate,
  onRename,
  onRemove,
}: {
  tags: TagItem[];
  onCreate: (name: string) => Promise<void>;
  onRename: (tag: TagItem, name: string) => Promise<void>;
  onRemove: (tag: TagItem) => Promise<void>;
}) {
  const [newName, setNewName] = useState("");
  const [editing, setEditing] = useState<TagItem | null>(null);
  const [editName, setEditName] = useState("");
  const [removing, setRemoving] = useState<TagItem | null>(null);
  const [error, setError] = useState<string | null>(null);

  const create = async () => {
    const name = newName.trim();
    if (!name) return;
    setError(null);
    try {
      await onCreate(name);
      setNewName("");
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "创建标签失败");
    }
  };

  const commitRename = async () => {
    if (!editing) return;
    const name = editName.trim();
    if (!name) return;
    setError(null);
    try {
      await onRename(editing, name);
      setEditing(null);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "重命名标签失败");
    }
  };

  const engineTags = tags.filter((tag) => tag.kind === "engine");
  const userTags = tags.filter((tag) => tag.kind !== "engine");

  return (
    <section aria-labelledby="tags-heading" className="space-y-6">
      <header>
        <h1 id="tags-heading" className="text-2xl font-bold tracking-tight text-text-primary">
          管理标签
        </h1>
        <p className="mt-1 text-sm text-text-secondary">
          引擎标签由扫描自动识别，重扫时会按同源规则恢复；用户标签完全由你维护。
        </p>
      </header>

      <div className="flex items-center gap-2">
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
        <Button onClick={() => void create()} disabled={!newName.trim()}>
          <Plus size={16} />
          新建标签
        </Button>
      </div>

      {error && (
        <p role="alert" className="rounded-md border border-danger/40 bg-danger/10 p-3 text-sm text-danger">
          {error}
        </p>
      )}

      <TagGroup title="用户标签" tags={userTags} editing={editing} editName={editName} onEditName={setEditName} onEdit={setEditing} onEditCancel={() => setEditing(null)} onEditCommit={commitRename} onRemove={setRemoving} />
      <TagGroup title="引擎标签（自动识别）" tags={engineTags} editing={editing} editName={editName} onEditName={setEditName} onEdit={setEditing} onEditCancel={() => setEditing(null)} onEditCommit={commitRename} onRemove={setRemoving} />

      <ConfirmDialog
        open={removing !== null}
        title={`删除标签「${removing?.name ?? ""}」`}
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
          if (target) void onRemove(target);
        }}
      />
    </section>
  );
}

function TagGroup({
  title,
  tags,
  editing,
  editName,
  onEditName,
  onEdit,
  onEditCancel,
  onEditCommit,
  onRemove,
}: {
  title: string;
  tags: TagItem[];
  editing: TagItem | null;
  editName: string;
  onEditName: (value: string) => void;
  onEdit: (tag: TagItem) => void;
  onEditCancel: () => void;
  onEditCommit: () => void;
  onRemove: (tag: TagItem) => void;
}) {
  return (
    <div>
      <h2 className="mb-2 text-xs font-semibold tracking-[0.16em] text-text-secondary uppercase">
        {title} · {tags.length}
      </h2>
      {tags.length === 0 ? (
        <p className="text-sm text-text-secondary">无</p>
      ) : (
        <ul className="space-y-1">
          {tags.map((tag) => (
            <li
              key={tag.tagId}
              className="flex items-center gap-3 rounded-md border border-border bg-surface px-3 py-2"
            >
              {editing?.tagId === tag.tagId ? (
                <>
                  <Input
                    value={editName}
                    onChange={(event) => onEditName(event.currentTarget.value)}
                    onKeyDown={(event) => {
                      if (event.key === "Enter") onEditCommit();
                      if (event.key === "Escape") onEditCancel();
                    }}
                    aria-label={`重命名标签 ${tag.name}`}
                    className="h-8 max-w-xs"
                    autoFocus
                  />
                  <Button size="sm" onClick={onEditCommit}>
                    保存
                  </Button>
                  <Button size="sm" variant="ghost" onClick={onEditCancel}>
                    取消
                  </Button>
                </>
              ) : (
                <>
                  <span className="min-w-0 flex-1 truncate text-sm text-text-primary">
                    {tag.name}
                  </span>
                  <span className="shrink-0 text-xs text-text-secondary">
                    {tag.gameCount ? `${tag.gameCount.toLocaleString()} 个游戏` : ""}
                  </span>
                  <Button
                    size="icon"
                    variant="ghost"
                    aria-label={`重命名 ${tag.name}`}
                    onClick={() => {
                      onEdit(tag);
                      onEditName(tag.name);
                    }}
                  >
                    <Pencil size={14} />
                  </Button>
                  <Button
                    size="icon"
                    variant="ghost"
                    aria-label={`删除 ${tag.name}`}
                    onClick={() => onRemove(tag)}
                  >
                    <Trash2 size={14} />
                  </Button>
                </>
              )}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
