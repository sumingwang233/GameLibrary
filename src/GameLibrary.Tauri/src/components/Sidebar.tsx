import {
  FolderPlus,
  FolderSearch,
  FolderTree,
  Gamepad2,
  Heart,
  Inbox,
  Plus,
  PlusCircle,
  Settings2,
  Tags as TagsIcon,
  Trash2,
} from "lucide-react";
import type { ReactNode } from "react";
import type { ViewItem } from "../lib/types";
import { cn } from "../lib/utils";
import { Button } from "./ui/button";
import { ScrollArea } from "./ui/scroll-area";
import { Separator } from "./ui/separator";

export type Section = "library" | "pending" | "tags" | "roots";

export interface SidebarProps {
  section: Section;
  onSectionChange: (section: Section) => void;
  /** 当前生效的收藏夹 ID；"" 表示未按收藏夹过滤。 */
  viewId: string;
  onViewChange: (viewId: string) => void;
  views: ViewItem[];
  favoriteOnly: boolean;
  onFavoriteChange: (value: boolean) => void;
  tagId: string;
  onTagChange: (tagId: string) => void;
  tags: Array<{ tagId: string; name: string; gameCount?: number }>;
  candidateTotal: number;
  gameTotal: number;
  notificationTotal: number;
  onCreateTag: () => void;
  onCreateView: () => void;
  onRemoveView: (view: ViewItem) => void;
  onAddRoot: () => void;
  onManualAdd: () => void;
  onScan: () => void;
  scanning: boolean;
  onSettings: () => void;
}

export function Sidebar(props: SidebarProps) {
  const {
    section,
    onSectionChange,
    viewId,
    onViewChange,
    views,
    favoriteOnly,
    onFavoriteChange,
    tagId,
    onTagChange,
    tags,
    candidateTotal,
    gameTotal,
    notificationTotal,
    onCreateTag,
    onCreateView,
    onRemoveView,
    onAddRoot,
    onManualAdd,
    onScan,
    scanning,
    onSettings,
  } = props;

  const pendingBadge = candidateTotal + notificationTotal;
  // 标签定义仍保留在“管理标签”中；侧栏只展示当前库内至少有一个有效游戏的标签。
  const visibleTags = tags.filter((tag) => (tag.gameCount ?? 0) > 0);

  return (
    <aside className="flex h-full w-[var(--sidebar-width)] shrink-0 flex-col border-r border-border bg-background p-4">
      <div className="mb-6 flex items-center gap-3 px-1">
        <div
          aria-hidden="true"
          className="flex h-9 w-9 items-center justify-center rounded-lg bg-primary text-lg font-black text-primary-foreground"
        >
          <img src="/app-icon.svg" alt="" className="h-9 w-9" />
        </div>
        <div className="leading-tight">
          <div className="text-sm font-bold tracking-[0.18em] text-text-primary">GAME</div>
          <div className="text-[10px] tracking-[0.28em] text-steam">LIBRARY</div>
        </div>
      </div>

      <SectionLabel>Library</SectionLabel>
      <nav className="space-y-1" aria-label="游戏库导航">
        <NavButton
          icon={<Gamepad2 size={16} />}
          label="全部游戏"
          count={gameTotal}
          active={section === "library" && !favoriteOnly && viewId === ""}
          onClick={() => {
            onSectionChange("library");
            onFavoriteChange(false);
            onViewChange("");
          }}
        />
        <NavButton
          icon={<Heart size={16} />}
          label="收藏"
          active={section === "library" && favoriteOnly}
          onClick={() => {
            onSectionChange("library");
            onFavoriteChange(true);
            onViewChange("");
          }}
        />
        <NavButton
          icon={<Inbox size={16} />}
          label="待确认"
          count={pendingBadge}
          active={section === "pending"}
          onClick={() => onSectionChange("pending")}
        />
      </nav>

      <Separator />

      <div className="mb-2 flex items-center justify-between px-2">
        <SectionLabel className="mb-0">收藏夹</SectionLabel>
        <button
          type="button"
          onClick={onCreateView}
          className="cursor-pointer rounded p-1 text-text-secondary hover:bg-surface hover:text-steam"
          aria-label="新建收藏夹"
        >
          <Plus size={14} />
        </button>
      </div>
      {views.length === 0 ? (
        <p className="px-2 pb-1 text-xs text-text-secondary">
          还没有收藏夹。先把列表筛成你想要的样子，再点上面的 + 保存。
        </p>
      ) : (
        <nav className="space-y-1" aria-label="收藏夹">
          {views.map((view) => (
            <div key={view.viewId} className="group flex items-center gap-1">
              <NavButton
                icon={<FolderTree size={15} />}
                label={view.name}
                active={section === "library" && viewId === view.viewId}
                className="flex-1"
                onClick={() => {
                  onSectionChange("library");
                  onViewChange(view.viewId);
                  onFavoriteChange(false);
                }}
              />
              <button
                type="button"
                onClick={() => onRemoveView(view)}
                className="cursor-pointer rounded p-1 text-text-secondary opacity-0 group-hover:opacity-100 hover:bg-danger-soft hover:text-danger focus-visible:opacity-100"
                aria-label={`删除收藏夹 ${view.name}`}
              >
                <Trash2 size={13} />
              </button>
            </div>
          ))}
        </nav>
      )}

      <Separator />

      <div className="mb-2 flex items-center justify-between px-2">
        <SectionLabel className="mb-0">Tags</SectionLabel>
        <button
          type="button"
          onClick={onCreateTag}
          className="cursor-pointer rounded p-1 text-text-secondary hover:bg-surface hover:text-steam"
          aria-label="新建标签"
        >
          <Plus size={14} />
        </button>
      </div>
      <ScrollArea className="min-h-0 flex-1">
        <div className="space-y-1 pr-2">
          {visibleTags.map((tag) => (
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
                onTagChange(tagId === tag.tagId ? "" : tag.tagId);
              }}
            >
              <span className="truncate">{tag.name}</span>
              <span className="shrink-0 text-xs text-text-secondary">
                {tag.gameCount ? tag.gameCount.toLocaleString() : ""}
              </span>
            </button>
          ))}
          {visibleTags.length === 0 && (
            <p className="px-2 text-xs text-text-secondary">没有正在使用的标签</p>
          )}
        </div>
      </ScrollArea>

      <div className="mt-4 space-y-2">
        <Button variant="outline" className="w-full" onClick={() => onSectionChange("tags")}>
          <TagsIcon size={16} />
          管理标签
        </Button>
        <Button variant="outline" className="w-full" onClick={() => onSectionChange("roots")}>
          <FolderTree size={16} />
          游戏库目录
        </Button>
        <Button variant="outline" className="w-full" onClick={onAddRoot}>
          <FolderPlus size={16} />
          添加目录
        </Button>
        <Button variant="outline" className="w-full" onClick={onManualAdd}>
          <PlusCircle size={16} />
          手动添加游戏
        </Button>
        <Button className="w-full" onClick={onScan} disabled={scanning}>
          <FolderSearch size={16} />
          {scanning ? "正在扫描…" : "扫描游戏库"}
        </Button>
        <Button variant="ghost" className="w-full justify-start" onClick={onSettings}>
          <Settings2 size={16} />
          设置
        </Button>
      </div>
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
