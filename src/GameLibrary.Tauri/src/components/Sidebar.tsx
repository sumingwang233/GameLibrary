import { t } from "../lib/i18n";
import {
  CheckCircle2,
  Gamepad2,
  Heart,
  Inbox,
  Plus,
} from "lucide-react";
import type { ReactNode } from "react";
import type { TagItem } from "../lib/types";
import { groupTagsByCategory, tagLabel, tagStyle } from "../lib/tags";
import { cn } from "../lib/utils";
import { ScrollArea } from "./ui/scroll-area";
import { Separator } from "./ui/separator";

export type Section = "library" | "pending" | "tags" | "roots";

export interface SidebarProps {
  section: Section;
  onSectionChange: (section: Section) => void;
  /** 当前生效的收藏夹 ID；"" 表示未按收藏夹过滤。 */
  viewId: string;
  onViewChange: (viewId: string) => void;
  favoriteOnly: boolean;
  onFavoriteChange: (value: boolean) => void;
  playedOnly: boolean;
  onPlayedChange: (value: boolean) => void;
  tagId: string;
  onTagChange: (tagId: string, clickedTagId?: string) => void;
  tags: TagItem[];
  candidateTotal: number;
  gameTotal: number;
  onCreateTag: () => void;
}

export function Sidebar(props: SidebarProps) {
  const {
    section,
    onSectionChange,
    viewId,
    onViewChange,
    favoriteOnly,
    onFavoriteChange,
    playedOnly,
    onPlayedChange,
    tagId,
    onTagChange,
    tags,
    candidateTotal,
    gameTotal,
    onCreateTag,
  } = props;

  // 标签定义仍保留在“管理标签”中；侧栏只展示当前库内至少有一个有效游戏的标签，
  // 并与管理页同口径按四分类分组（v1.5.5），空分组不占位。
  const visibleTags = tags.filter((tag) => (tag.gameCount ?? 0) > 0);
  const tagGroups = groupTagsByCategory(visibleTags).filter((group) => group.items.length > 0);

  return (
    <aside className="flex h-full w-[var(--sidebar-width)] shrink-0 flex-col border-r border-border bg-background p-4">
      <SectionLabel>{t("游戏库")}</SectionLabel>
      <nav className="space-y-1" aria-label={t("游戏库导航")}>
        <NavButton
          icon={<Gamepad2 size={16} />}
          label={t("全部游戏")}
          count={gameTotal}
          active={section === "library" && !playedOnly && !favoriteOnly && viewId === ""}
          onClick={() => {
            onSectionChange("library");
            onPlayedChange(false);
            onViewChange("");
            onFavoriteChange(false);
          }}
        />
        <NavButton
          icon={<CheckCircle2 size={16} />}
          label={t("已游玩")}
          active={section === "library" && playedOnly}
          onClick={() => {
            onSectionChange("library");
            onPlayedChange(true);
          }}
        />
        <NavButton
          icon={<Heart size={16} />}
          label={t("收藏")}
          active={section === "library" && !playedOnly && favoriteOnly}
          onClick={() => {
            onSectionChange("library");
            onPlayedChange(false);
            onViewChange("");
            onFavoriteChange(true);
          }}
        />
        <NavButton
          icon={<Inbox size={16} />}
          label={t("待确认")}
          count={candidateTotal}
          active={section === "pending"}
          onClick={() => onSectionChange("pending")}
        />
      </nav>

      <Separator />

      <div className="mb-2 flex items-center justify-between px-2">
        <SectionLabel className="mb-0">Tags</SectionLabel>
        <button
          type="button"
          onClick={onCreateTag}
          className="cursor-pointer rounded p-1 text-text-secondary hover:bg-surface hover:text-steam"
          aria-label={t("新建标签")}
        >
          <Plus size={14} />
        </button>
      </div>
      <ScrollArea className="min-h-0 flex-1">
        <div className="space-y-1 pr-2">
          {tagGroups.map((group) => (
            <div key={group.value}>
              <p className="px-2 pb-1 pt-2 text-[10px] font-semibold uppercase tracking-[0.14em] text-text-secondary/80">
                {t(group.label)}
              </p>
              {group.items.map((tag) => (
                <button
                  key={tag.tagId}
                  type="button"
                  className={cn(
                    "flex w-full cursor-pointer items-center justify-between gap-2 rounded-md px-2 py-2 text-left text-sm transition",
                    tagId === tag.tagId
                      ? "bg-steam-soft text-text-primary"
                      : "text-text-secondary hover:bg-surface hover:text-text-primary",
                  )}
                  onClick={() => {
                    onSectionChange("library");
                    onTagChange(tagId === tag.tagId ? "" : tag.tagId, tag.tagId);
                  }}
                >
                  <span className="flex min-w-0 items-center gap-2 rounded px-1" style={tagStyle(tag)}>
                    <span
                      aria-hidden="true"
                      className="size-2 shrink-0 rounded-full border border-border"
                      style={{ backgroundColor: "currentColor" }}
                    />
                    <span className="truncate">{tagLabel(tag)}</span>
                  </span>
                  <span className="shrink-0 text-xs text-text-secondary">
                    {tag.gameCount ? tag.gameCount.toLocaleString() : ""}
                  </span>
                </button>
              ))}
            </div>
          ))}
          {visibleTags.length === 0 && (
            <p className="px-2 text-xs text-text-secondary">{t("没有正在使用的标签")}</p>
          )}
        </div>
      </ScrollArea>

    </aside>
  );
}

function SectionLabel({ children, className }: { children: ReactNode; className?: string }) {
  return (
    <div
      className={cn(
        "mb-2 px-2 text-[10px] font-semibold tracking-[0.2em] text-text-secondary uppercase",
        className,
      )}
    >
      {children}
    </div>
  );
}

function NavButton({
  icon,
  label,
  count,
  active,
  onClick,
  className,
}: {
  icon: ReactNode;
  label: string;
  count?: number;
  active: boolean;
  onClick: () => void;
  className?: string;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      aria-current={active ? "page" : undefined}
      className={cn(
        "flex w-full cursor-pointer items-center gap-3 rounded-md px-3 py-2.5 text-sm transition",
        active
          ? "bg-steam-soft text-text-primary shadow-inner"
          : "text-text-secondary hover:bg-surface hover:text-text-primary",
        className,
      )}
    >
      <span className="shrink-0">{icon}</span>
      <span className="flex-1 truncate text-left">{label}</span>
      {count !== undefined && count > 0 && (
        <span className="shrink-0 text-xs text-text-secondary">{count.toLocaleString()}</span>
      )}
    </button>
  );
}
