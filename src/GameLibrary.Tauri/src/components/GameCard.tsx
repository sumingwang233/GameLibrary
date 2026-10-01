import { t } from "../lib/i18n";
import { memo, useEffect, useRef, useState } from "react";
import { Clock3, Play, Star } from "lucide-react";
import { assetDataUrl } from "../lib/api";
import type { GameItem } from "../lib/types";
import { cn, formatPlaytime } from "../lib/utils";

export interface GameCardProps {
  game: GameItem;
  selected: boolean;
  checked: boolean;
  selectionDisabled: boolean;
  onToggle: (gameId: string) => void;
  /** 传 gameId 而非闭包，配合 memo 让搜索输入不再重渲染整片网格。 */
  onSelect: (gameId: string) => void;
  onPlay: (gameId: string) => void;
}

function GameCardImpl({ game, selected, checked, selectionDisabled, onToggle, onSelect, onPlay }: GameCardProps) {
  const initials = game.title.trim().slice(0, 2).toUpperCase() || "GL";
  const playtime = formatPlaytime(game.playtimeMinutes) || t("{0} 分钟", 0);
  const [cover, setCover] = useState<string | null>(null);
  const [visible, setVisible] = useState(false);
  const cardRef = useRef<HTMLElement | null>(null);

  useEffect(() => {
    const node = cardRef.current;
    if (!node) return;
    const observer = new IntersectionObserver(([entry]) => {
      if (entry?.isIntersecting) {
        setVisible(true);
        observer.disconnect();
      }
    }, { rootMargin: "600px" });
    observer.observe(node);
    return () => observer.disconnect();
  }, []);

  useEffect(() => {
    let active = true;
    if (!visible || !game.coverAssetId) {
      setCover(null);
      return () => {
        active = false;
      };
    }
    void assetDataUrl(game.coverAssetId)
      .then((value) => {
        if (active) setCover(value);
      })
      .catch(() => undefined);
    return () => {
      active = false;
    };
  }, [game.coverAssetId, visible]);

  return (
    <article
      ref={cardRef}
      role="button"
      tabIndex={0}
      aria-label={t("{0}，查看详情", game.title)}
      aria-pressed={selected}
      onClick={() => onSelect(game.gameId)}
      onKeyDown={(event) => {
        if (event.target !== event.currentTarget) return;
        if (event.key === "Enter" || event.key === " ") {
          event.preventDefault();
          onSelect(game.gameId);
        }
      }}
      className={cn(
        "group cursor-pointer overflow-hidden rounded-lg border bg-surface transition duration-200",
        "[content-visibility:auto] [contain-intrinsic-size:auto_275px]",
        "hover:-translate-y-1 hover:shadow-2xl hover:shadow-black/40",
        "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background",
        selected || checked ? "border-steam ring-2 ring-steam/50" : "border-border hover:border-steam/60",
      )}
    >
      <div className="relative aspect-3/4 overflow-hidden bg-linear-to-br from-steam-soft via-surface-elevated to-gradient-deep">
        <input type="checkbox" aria-label={t("选择 {0}", game.title)} checked={checked} disabled={selectionDisabled}
          className="absolute top-3 left-3 z-10 size-5 cursor-pointer accent-steam"
          onClick={event => event.stopPropagation()} onChange={() => onToggle(game.gameId)} />
        {cover ? (
          <img
            src={cover}
            alt=""
            loading="lazy"
            decoding="async"
            className="absolute inset-0 h-full w-full object-cover transition duration-300 group-hover:scale-[1.02]"
          />
        ) : (
          <div
            aria-hidden="true"
            className="absolute inset-0 flex items-center justify-center text-5xl font-black tracking-widest text-white/15"
          >
            {initials}
          </div>
        )}
        <div
          aria-hidden="true"
          className="absolute inset-0 bg-linear-to-t from-black/80 via-transparent to-transparent"
        />
        <button
          type="button"
          className="absolute top-3 right-3 flex h-9 w-9 translate-y-1 cursor-pointer items-center justify-center rounded-full bg-primary text-primary-foreground opacity-0 shadow-lg transition group-hover:translate-y-0 group-hover:opacity-100 focus-visible:translate-y-0 focus-visible:opacity-100"
          onClick={(event) => {
            event.stopPropagation();
            onPlay(game.gameId);
          }}
          aria-label={t("启动 {0}", game.title)}
        >
          <Play size={16} fill="currentColor" />
        </button>
        <div className="absolute inset-x-3 bottom-3">
          <h3 className="line-clamp-2 text-sm leading-tight font-semibold text-white">
            {game.title}
          </h3>
          <p title={t("累计游玩 {0}", playtime)} className="mt-1 flex items-center gap-1 text-[11px] text-white/60">
            <Clock3 size={12} aria-hidden="true" className="shrink-0" />
            {playtime}
          </p>
        </div>
      </div>
      <div className="flex items-center justify-between gap-2 px-3 py-2 text-[11px] text-text-secondary">
        <span className="truncate">{availabilityLabel(game.availability)}</span>
        {game.favorite && (
          <Star size={13} aria-label={t("已收藏")} className="shrink-0 fill-favorite text-favorite" />
        )}
      </div>
    </article>
  );
}

function availabilityLabel(value?: string) {
  switch (value) {
    case "available":
      return t("可启动");
    case "suspectedMissing":
      return t("疑似缺失");
    case "missing":
      return t("已缺失");
    case "offline":
      return t("磁盘离线");
    case "accessError":
      return t("无法访问");
    case "rootUnbound":
      return t("未绑定目录");
    default:
      return t("已入库");
  }
}

export const GameCard = memo(GameCardImpl);
