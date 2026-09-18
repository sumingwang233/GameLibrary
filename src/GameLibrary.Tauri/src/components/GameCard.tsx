import { useEffect, useState } from "react";
import { Play, Star } from "lucide-react";
import { assetDataUrl } from "../lib/api";
import type { GameItem } from "../lib/types";
import { cn } from "../lib/utils";

export function GameCard({ game, selected, onSelect, onPlay }: { game: GameItem; selected: boolean; onSelect: () => void; onPlay: () => void }) {
  const initials = game.title.trim().slice(0, 2).toUpperCase() || "GL";
  const [cover, setCover] = useState<string | null>(null);
  useEffect(() => { let active = true; if (game.coverAssetId) void assetDataUrl(game.coverAssetId).then(value => { if (active) setCover(value); }).catch(() => undefined); return () => { active = false; }; }, [game.coverAssetId]);
  return <article className={cn("group cursor-pointer overflow-hidden rounded-lg border bg-surface transition duration-200 [content-visibility:auto] [contain-intrinsic-size:260px_360px] hover:-translate-y-1 hover:shadow-2xl hover:shadow-black/40", selected ? "border-steam ring-2 ring-steam/50" : "border-border hover:border-steam/60")} onClick={onSelect}>
    <div className="relative aspect-[3/4] overflow-hidden bg-gradient-to-br from-[#2a475e] via-[#243447] to-[#151b25]">
      {cover ? <img src={cover} alt="" className="absolute inset-0 h-full w-full object-cover transition duration-300 group-hover:scale-[1.02]" /> : <div className="absolute inset-0 flex items-center justify-center text-5xl font-black tracking-widest text-white/15">{initials}</div>}
      <div className="absolute inset-0 bg-gradient-to-t from-black/80 via-transparent to-transparent" />
      <button className="absolute right-3 top-3 flex h-9 w-9 translate-y-1 items-center justify-center rounded-full bg-steam text-[#0d1822] opacity-0 shadow-lg transition group-hover:translate-y-0 group-hover:opacity-100 focus-visible:opacity-100" onClick={(event) => { event.stopPropagation(); onPlay(); }} aria-label={`启动 ${game.title}`}><Play size={16} fill="currentColor" /></button>
      <div className="absolute inset-x-3 bottom-3"><h3 className="line-clamp-2 text-sm font-semibold leading-tight text-white">{game.title}</h3><p className="mt-1 truncate text-[11px] text-white/60">{game.engine ?? game.kind}</p></div>
    </div>
    <div className="flex items-center justify-between px-3 py-2 text-[11px] text-text-secondary"><span className="truncate">{game.availability ?? "已入库"}</span>{game.favorite && <Star size={13} className="fill-[#f5c542] text-[#f5c542]" />}</div>
  </article>;
}
