import { useCallback } from "react";
import { operation } from "../api";
import type { TagItem } from "../types";
import type { TagChanges } from "../state";

export function useTagActions(supports: (name: string) => boolean, refreshMeta: () => Promise<void>, bump: () => void) {
  const createTag = useCallback(
    async (name: string, category?: string) => {
      // feat-3：category 缺省由后端按 kind 推断（user→special）；显式传入时校验留给后端。
      await operation(
        "tags.create",
        { name, ...(category ? { category } : {}) },
        `tags.create:${name}:${category ?? "special"}`,
      );
      await refreshMeta();
    },
    [refreshMeta],
  );

  const renameTag = useCallback(
    async (tag: TagItem, name: string) => {
      // feat-3：engine 标签 name 是身份键不可变（TagsHandler.cs:169），改名走 displayName；
      // user 标签直接改 name。displayName 无显式清除入口，清除走 updateTag({ displayName: null })。
      const isEngine = tag.kind === "engine";
      await operation(
        "tags.update",
        isEngine
          ? { tagId: tag.tagId, expectedRevision: tag.revision, displayName: name }
          : { tagId: tag.tagId, expectedRevision: tag.revision, name },
        `tags.update:${tag.tagId}:${tag.revision}:${isEngine ? "displayName" : "name"}:${name}`,
      );
      await refreshMeta();
    },
    [refreshMeta],
  );

  const updateTag = useCallback(
    async (tag: TagItem, changes: TagChanges) => {
      const parameters: Record<string, unknown> = {
        tagId: tag.tagId,
        expectedRevision: tag.revision,
      };
      if (changes.color !== undefined) parameters.color = changes.color;
      if (changes.category !== undefined) parameters.category = changes.category;
      if (changes.sortOrder !== undefined) parameters.sortOrder = changes.sortOrder;
      if (changes.starred !== undefined) parameters.starred = changes.starred;
      // displayName: null 显式清除（后端按 JSON Null 走 clearDisplayName 分支，TagsHandler.cs:225）。
      if (changes.displayName !== undefined) parameters.displayName = changes.displayName;

      // 幂等键随变更内容稳定：同一次修改重试命中收据重放，不同变更互不串键。
      const changedKeys = Object.keys(changes).sort();
      await operation(
        "tags.update",
        parameters,
        `tags.update:${tag.tagId}:${tag.revision}:${changedKeys.join(",")}:${JSON.stringify(changes)}`,
      );
      await refreshMeta();
    },
    [refreshMeta],
  );

  const reorderTags = useCallback(
    async (updates: Array<{ tag: TagItem; sortOrder: number }>) => {
      try {
        if (supports("tags.reorder")) {
          const items = updates.map(({ tag, sortOrder }) => ({ tagId: tag.tagId, expectedRevision: tag.revision, sortOrder }));
          await operation("tags.reorder", { items }, `tags.reorder:${JSON.stringify(items)}`);
        } else {
          for (const { tag, sortOrder } of updates)
            await operation("tags.update", { tagId: tag.tagId, expectedRevision: tag.revision, sortOrder },
              `tags.update:${tag.tagId}:${tag.revision}:sortOrder:${sortOrder}`);
        }
      } finally { await refreshMeta(); }
    },
    [supports, refreshMeta],
  );

  const removeTag = useCallback(
    async (tag: TagItem) => {
      await operation(
        "tags.remove",
        { tagId: tag.tagId, expectedRevision: tag.revision },
        `tags.remove:${tag.tagId}:${tag.revision}`,
      );
      await refreshMeta();
      bump();
    },
    [refreshMeta, bump],
  );

  return { createTag, renameTag, updateTag, reorderTags, removeTag };
}
