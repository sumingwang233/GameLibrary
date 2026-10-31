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
      setVisible(!!entry?.isIntersecting);
    }, { rootMargin: "600px" });
    observer.observe(node);
    return () => observer.disconnect();
  }, []);

  useEffect(() => {
    let active = true;
    const request = new AbortController();
    setCover(null);
    if (!visible || !game.coverAssetId) {
      return () => {
        active = false;
        request.abort();
      };
    }
    void assetDataUrl(game.coverAssetId, request.signal)
      .then((value) => {
        if (active) setCover(value);
      })
      .catch(() => undefined);
    return () => {
      active = false;
      request.abort();
    };
  }, [game.coverAssetId, visible]);

  return (
    <article
      ref={cardRef}
      className={cn(
        "group relative cursor-pointer overflow-hidden rounded-lg border bg-surface transition duration-200",
        "hover:-translate-y-1 hover:shadow-2xl hover:shadow-black/40",
        selected || checked ? "border-steam ring-2 ring-steam/50" : "border-border hover:border-steam/60",
      )}
    >
      {/*
        拉伸详情按钮：整卡唯一的「查看详情」入口。
        原实现让 article 自己 role="button"、再往里嵌 checkbox 与启动按钮，属于嵌套交互控件，
        Enter/Space 还得用 event.target === currentTarget 排除子元素。现在 article 退回纯静态语义
        （无 role/tabIndex/onClick），这个真实 button 用 after 伪元素铺满整张卡的热区；checkbox
        与启动按钮仍是 z-10 且 DOM 在其后，因此盖在按钮上方、各自独立聚焦，点击它们不会触发
        onSelect。焦点环画成 inset（卡片 overflow-hidden 会裁掉向外扩的 ring）。
      */}
      <button
        type="button"
        aria-label={t("{0}，查看详情", game.title)}
        aria-haspopup="dialog"
        onClick={() => onSelect(game.gameId)}
        className="absolute inset-0 z-10 cursor-pointer rounded-lg after:absolute after:inset-0 after:content-[''] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-inset"
      />
      <div className="relative aspect-3/4 overflow-hidden bg-linear-to-br from-steam-soft via-surface-elevated to-gradient-deep">
        <input type="checkbox" aria-label={t("选择 {0}", game.title)} checked={checked} disabled={selectionDisabled}
          className="absolute top-3 left-3 z-10 size-5 cursor-pointer accent-steam"
          onChange={() => onToggle(game.gameId)} />
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
          className="absolute top-3 right-3 z-10 flex h-9 w-9 translate-y-1 cursor-pointer items-center justify-center rounded-full bg-primary text-primary-foreground opacity-0 shadow-lg transition group-hover:translate-y-0 group-hover:opacity-100 focus-visible:translate-y-0 focus-visible:opacity-100"
          onClick={() => onPlay(game.gameId)}
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
