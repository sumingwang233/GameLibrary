import { useEffect, useState } from "react";
import { open } from "@tauri-apps/plugin-dialog";
import { openPath } from "@tauri-apps/plugin-opener";
import { ExternalLink, ImagePlus, Play, Settings2, Star, Trash2 } from "lucide-react";
import { assetDataUrl, operation } from "../lib/api";
import type { GameItem, ProfileItem, TagItem, TranslationPolicy } from "../lib/types";
import { formatTime } from "../lib/utils";
import { Button } from "./ui/button";
import { Badge } from "./ui/badge";
import { Sheet } from "./ui/sheet";

export function DetailSheet({ game, tags, onClose, onPlay, onUpdated }: { game: GameItem | null; tags: TagItem[]; onClose: () => void; onPlay: (game: GameItem) => void; onUpdated: () => Promise<void> }) {
  const [current, setCurrent] = useState<GameItem | null>(game);
  const [profiles, setProfiles] = useState<ProfileItem[]>([]);
  const [translation, setTranslation] = useState<TranslationPolicy | null>(null);
  const [cover, setCover] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => { setCurrent(game); setCover(null); setError(null); }, [game]);
  useEffect(() => {
    if (!game) return;
    void Promise.all([
      operation<{ items: ProfileItem[] }>("profiles.list", { gameId: game.gameId }),
      operation<TranslationPolicy>("translation.get", { gameId: game.gameId }),
      operation<GameItem>("games.get", { gameId: game.gameId }),
    ]).then(([profileResult, translationResult, gameResult]) => {
      setProfiles(profileResult.data.items); setTranslation(translationResult.data); setCurrent(gameResult.data);
      if (gameResult.data.coverAssetId) void assetDataUrl(gameResult.data.coverAssetId).then(setCover).catch(() => undefined);
    }).catch(cause => setError(String(cause)));
  }, [game]);

  if (!current) return null;
  const initials = current.title.slice(0, 2).toUpperCase();
  const run = async (work: () => Promise<void>) => { setError(null); try { await work(); await onUpdated(); } catch (cause) { setError(cause instanceof Error ? cause.message : "操作失败"); } };

  const configure = () => run(async () => {
    const selected = await open({ multiple: false, directory: false, filters: [{ name: "游戏启动文件", extensions: ["exe", "swf"] }] });
    if (!selected) return;
    const path = String(selected);
    const result = await operation<ProfileItem>("profiles.create", { idempotencyKey: `tauri-profile-${Date.now()}`, gameId: current.gameId, executablePath: path, argv: [], cwd: path.replace(/[\\/][^\\/]+$/, ""), isDefault: !profiles.some(profile => profile.isDefault) });
    setProfiles(previous => [...previous, result.data]);
  });

  const importCover = () => run(async () => {
    const selected = await open({ multiple: false, directory: false, filters: [{ name: "封面图片", extensions: ["png", "jpg", "jpeg", "webp", "gif"] }] });
    if (!selected) return;
    const result = await operation<{ assetId: string }>("assets.import", { idempotencyKey: `tauri-cover-${Date.now()}`, gameId: current.gameId, sourcePath: String(selected) });
    setCover(await assetDataUrl(result.data.assetId));
  });

  const setPolicy = (value: string) => run(async () => {
    if (!translation) return;
    const result = await operation<TranslationPolicy>("translation.set", { idempotencyKey: `tauri-translation-${Date.now()}`, gameId: current.gameId, override: value, expectedRevision: translation.revision });
    setTranslation(result.data);
    setCurrent(previous => previous ? { ...previous, revision: result.data.revision } : previous);
  });

  const toggleFavorite = () => run(async () => {
    const result = await operation<GameItem>("games.update", { idempotencyKey: `tauri-favorite-${Date.now()}`, gameId: current.gameId, favorite: !current.favorite, expectedRevision: current.revision });
    setCurrent(result.data);
  });

  const toggleTag = (tag: TagItem) => run(async () => {
    const assigned = (current.tags ?? []).some(item => item.tagId === tag.tagId);
    const result = await operation<{ revision?: number }>(assigned ? "tags.unassign" : "tags.assign", { idempotencyKey: `tauri-tag-${Date.now()}`, gameId: current.gameId, tagId: tag.tagId, expectedRevision: current.revision });
    const refreshed = await operation<GameItem>("games.get", { gameId: current.gameId });
    setCurrent({ ...refreshed.data, revision: result.data.revision ?? refreshed.data.revision });
  });

  const remove = () => run(async () => {
    await operation("games.remove", { idempotencyKey: `tauri-remove-${Date.now()}`, gameId: current.gameId, expectedRevision: current.revision });
    onClose();
  });

  return <Sheet open onClose={onClose} title="游戏详情"><div className="space-y-6">
    <div className="relative aspect-[16/9] overflow-hidden rounded-lg bg-[#243447]">{cover ? <img src={cover} alt="" className="absolute inset-0 h-full w-full object-cover" /> : <div className="absolute inset-0 flex items-center justify-center text-7xl font-black text-white/15">{initials}</div>}<div className="absolute inset-0 bg-gradient-to-t from-black/85 via-black/10 to-transparent" /><div className="absolute inset-x-5 bottom-4"><h3 className="text-2xl font-bold text-white">{current.title}</h3><div className="mt-2 flex gap-2"><Badge>{current.engine ?? current.kind}</Badge>{current.favorite && <Badge className="border-[#f5c542]/50 text-[#f5c542]"><Star size={12} className="mr-1 fill-current" />收藏</Badge>}</div></div></div>
    {error && <div className="rounded-md border border-danger/40 bg-danger/10 p-3 text-sm text-danger">{error}</div>}
    <div className="grid grid-cols-2 gap-3"><Button className="col-span-2 h-11" onClick={() => onPlay(current)}><Play size={17} fill="currentColor" />开始游戏</Button><Button variant="outline" onClick={configure}><Settings2 size={15} />配置启动方式</Button><Button variant="outline" onClick={() => void openPath(current.rootPath)}><ExternalLink size={15} />打开目录</Button><Button variant="outline" onClick={importCover}><ImagePlus size={15} />导入封面</Button><Button variant="outline" onClick={toggleFavorite}><Star size={15} />{current.favorite ? "取消收藏" : "收藏"}</Button></div>
    <div><label className="mb-2 block text-[11px] uppercase tracking-[0.14em] text-text-secondary">翻译策略</label><select value={translation?.userOverride ?? "Auto"} onChange={event => void setPolicy(event.target.value)} className="h-10 w-full rounded-md border border-border bg-[#121821] px-3 text-sm text-text-primary"><option value="Auto">Auto · 继承并自动路由</option><option value="Required">Required · 强制翻译</option><option value="NotRequired">NotRequired · 原文直启</option></select>{translation && <p className="mt-2 text-xs text-text-secondary">当前有效策略：{translation.effective}{translation.inherited !== "Auto" ? ` · 继承 ${translation.inherited}` : ""}</p>}</div>
    <div><div className="mb-2 text-[11px] uppercase tracking-[0.14em] text-text-secondary">标签</div><div className="flex flex-wrap gap-2">{tags.map(tag => { const active = (current.tags ?? []).some(item => item.tagId === tag.tagId); return <button key={tag.tagId} className={active ? "rounded-full border border-steam bg-steam-soft px-3 py-1 text-xs text-steam" : "rounded-full border border-border px-3 py-1 text-xs text-text-secondary hover:border-steam"} onClick={() => void toggleTag(tag)}>{tag.name}</button>; })}</div></div>
    <div className="space-y-3 text-sm"><Info label="路径" value={current.rootPath} /><Info label="入库时间" value={formatTime(current.acceptedUtc)} /><Info label="启动方式" value={profiles.find(profile => profile.isDefault)?.executablePath ?? "尚未配置"} /></div>
    <Button variant="danger" className="w-full" onClick={remove}><Trash2 size={15} />从游戏库移除</Button>
  </div></Sheet>;
}

function Info({ label, value }: { label: string; value: string }) { return <div><div className="mb-1 text-[11px] uppercase tracking-[0.14em] text-text-secondary">{label}</div><div className="break-all rounded-md bg-[#121821] p-3 text-text-primary">{value}</div></div>; }
