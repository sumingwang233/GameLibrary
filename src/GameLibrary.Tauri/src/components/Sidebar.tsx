import { FolderPlus, FolderSearch, Gamepad2, Heart, Inbox, Plus, PlusCircle, Settings2 } from "lucide-react";
import type { ReactNode } from "react";
import type { TagItem } from "../lib/types";
import { cn } from "../lib/utils";
import { Button } from "./ui/button";
import { Separator } from "./ui/separator";
import { ScrollArea } from "./ui/scroll-area";

export function Sidebar({ view, onViewChange, tagId, tags, onTagChange, candidateTotal, gameTotal, onCreateTag, onAddRoot, onManualAdd, onScan, onSettings }: { view: string; onViewChange: (view: string) => void; tagId: string; tags: TagItem[]; onTagChange: (tagId: string) => void; candidateTotal: number; gameTotal: number; onCreateTag: () => void; onAddRoot: () => void; onManualAdd: () => void; onScan: () => void; onSettings: () => void }) {
  return <aside className="flex h-full w-[232px] shrink-0 flex-col border-r border-border bg-[#171a21] p-4">
    <div className="mb-8 flex items-center gap-3"><div className="flex h-9 w-9 items-center justify-center rounded-lg bg-steam text-lg font-black text-[#10202c]">G</div><div><div className="text-sm font-bold tracking-[0.18em] text-text-primary">GAME</div><div className="text-[10px] tracking-[0.28em] text-steam">LIBRARY</div></div></div>
    <div className="mb-3 px-2 text-[10px] font-semibold uppercase tracking-[0.2em] text-text-secondary">Library</div>
    <nav className="space-y-1">
      <NavButton icon={<Gamepad2 size={16} />} label="全部游戏" count={gameTotal} active={view === "all"} onClick={() => onViewChange("all")} />
      <NavButton icon={<Heart size={16} />} label="收藏" active={view === "favorites"} onClick={() => onViewChange("favorites")} />
      <NavButton icon={<Inbox size={16} />} label="待确认" count={candidateTotal} active={view === "pending"} onClick={() => onViewChange("pending")} />
    </nav>
    <Separator />
    <div className="mb-3 flex items-center justify-between px-2 text-[10px] font-semibold uppercase tracking-[0.2em] text-text-secondary"><span>Tags</span><button onClick={onCreateTag} className="rounded p-1 hover:bg-surface hover:text-steam" aria-label="新建标签"><Plus size={14} /></button></div>
    <ScrollArea className="min-h-0 flex-1"><div className="space-y-1 pr-2">{tags.map(tag => <button key={tag.tagId} className={cn("flex w-full items-center justify-between rounded-md px-2 py-2 text-left text-sm transition", tagId === tag.tagId ? "bg-steam-soft text-text-primary" : "text-text-secondary hover:bg-surface hover:text-text-primary")} onClick={() => onTagChange(tagId === tag.tagId ? "" : tag.tagId)}><span className="truncate">{tag.name}</span><span className="text-xs text-text-secondary">{tag.gameCount ?? ""}</span></button>)}{!tags.length && <p className="px-2 text-xs text-text-secondary">还没有标签</p>}</div></ScrollArea>
    <div className="mt-4 space-y-2"><Button variant="outline" className="w-full" onClick={onAddRoot}><FolderPlus size={16} />添加游戏库</Button><Button variant="outline" className="w-full" onClick={onManualAdd}><PlusCircle size={16} />手动添加</Button><Button className="w-full" onClick={onScan}><FolderSearch size={16} />扫描游戏库</Button><Button variant="ghost" className="w-full justify-start" onClick={onSettings}><Settings2 size={16} />设置</Button></div>
  </aside>;
}

function NavButton({ icon, label, count, active, onClick }: { icon: ReactNode; label: string; count?: number; active: boolean; onClick: () => void }) {
  return <button className={cn("flex w-full items-center gap-3 rounded-md px-3 py-2.5 text-sm transition", active ? "bg-steam-soft text-text-primary shadow-inner" : "text-text-secondary hover:bg-surface hover:text-text-primary")} onClick={onClick}>{icon}<span className="flex-1 text-left">{label}</span>{count !== undefined && <span className="text-xs text-text-secondary">{count.toLocaleString()}</span>}</button>;
}
