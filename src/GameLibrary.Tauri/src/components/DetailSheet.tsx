import { useCallback, useEffect, useState } from "react";
import { dirname, join, pictureDir } from "@tauri-apps/api/path";
import { open } from "@tauri-apps/plugin-dialog";
import { invoke } from "@tauri-apps/api/core";
import { Check, ExternalLink, ImagePlus, Play, Star, Trash2 } from "lucide-react";
import { assetDataUrl, describeFailure, operation } from "../lib/api";
import type {
  GameItem,
  ProfileItem,
  SimilarGameSuggestion,
  TagItem,
  TranslationPolicy,
} from "../lib/types";
import { cn, formatSimilarity, formatTime } from "../lib/utils";
import { Badge } from "./ui/badge";
import { Button } from "./ui/button";
import { ConfirmDialog } from "./ui/confirm-dialog";
import { Input } from "./ui/input";
import { Sheet } from "./ui/sheet";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "./ui/tabs";
import { Textarea } from "./ui/textarea";

/**
 * GameDto.tags 只回传 {kind,name}，不含 tagId（OperationDispatcher.Cataloging.cs 的 GameDto）。
 * v1.1.5 按 tagId 比较，运行时恒为 undefined === string → 标签永远显示未选中、
 * 点击只会 assign 不会 unassign。这里改为按 (kind,name) 匹配。
 */
function hasTag(game: GameItem, tag: TagItem) {
  return (game.tags ?? []).some((item) => item.name === tag.name && (item.kind ?? null) === tag.kind);
}

function isFileBackedGame(game: GameItem) {
  return game.kind === "manualFile" || game.kind === "manualShortcut" || game.kind === "fileGame";
}

async function gameDirectory(game: GameItem) {
  return isFileBackedGame(game) ? dirname(game.rootPath) : game.rootPath;
}

async function screenshotsDirectory() {
  return join(await pictureDir(), "Screenshots");
}

export function DetailSheet({
  game,
  tags,
  onClose,
  onPlay,
  onChanged,
  onNavigate,
}: {
  game: GameItem | null;
  tags: TagItem[];
  onClose: () => void;
  onPlay: (gameId: string) => Promise<void>;
  onChanged: () => Promise<void> | void;
  /** 点击「疑似重复」条目跳到目标游戏详情；Sheet 不关闭不重建，由 App 侧换 selected 实现。 */
  onNavigate?: (gameId: string) => void;
}) {
  const [current, setCurrent] = useState<GameItem | null>(game);
  const [profiles, setProfiles] = useState<ProfileItem[]>([]);
  const [translation, setTranslation] = useState<TranslationPolicy | null>(null);
  const [cover, setCover] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [titleDraft, setTitleDraft] = useState("");
  const [summaryDraft, setSummaryDraft] = useState("");
  const [confirmRemove, setConfirmRemove] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [busy, setBusy] = useState(false);
  const [tagDraft, setTagDraft] = useState("");

  const gameId = game?.gameId ?? null;

  const load = useCallback(async () => {
    if (!gameId) return;
    setError(null);
    try {
      const [detail, profileResult, translationResult] = await Promise.all([
        operation<GameItem>("games.get", { gameId }),
        operation<{ items: ProfileItem[] }>("profiles.list", { gameId }),
        operation<TranslationPolicy>("translation.get", { gameId }),
      ]);
      setCurrent(detail.data);
      setTitleDraft(detail.data.title);
      setSummaryDraft(detail.data.summary ?? "");
      setProfiles(profileResult.data.items);
      setTranslation(translationResult.data);
      if (detail.data.coverAssetId) {
        const url = await assetDataUrl(detail.data.coverAssetId).catch(() => null);
        setCover(url);
      } else {
        setCover(null);
      }
    } catch (cause) {
      setError(describeFailure(cause));
    }
  }, [gameId]);

  useEffect(() => {
    setCurrent(game);
    setCover(null);
    setProfiles([]);
    setTranslation(null);
    setTitleDraft(game?.title ?? "");
    setSummaryDraft(game?.summary ?? "");
    setTagDraft("");
    if (gameId) void load();
  }, [game, gameId, load]);

  if (!current) return null;

  const run = async (work: () => Promise<void>) => {
    setBusy(true);
    setError(null);
    try {
      await work();
    } catch (cause) {
      setError(describeFailure(cause));
    } finally {
      setConfirmDelete(false);
      setBusy(false);
    }
  };

  const saveField = (field: "title" | "summary", value: string) =>
    run(async () => {
      await operation(
        "fields.set",
        { gameId: current.gameId, field, value, expectedRevision: current.revision },
        `fields.set:${current.gameId}:${field}:${current.revision}`,
      );
      // fields.set 只返回字段修订回执，不是完整 GameItem；重新读取后再渲染，
      // 避免把缺少 title/rootPath 的回执写入 current 导致整页白屏。
      await load();
      await onChanged();
    });

  const toggleFavorite = () =>
    run(async () => {
      await operation(
        "games.update",
        { gameId: current.gameId, favorite: !current.favorite, expectedRevision: current.revision },
        `games.update:${current.gameId}:${current.revision}`,
      );
      await load();
      await onChanged();
    });

  const toggleTag = (tag: TagItem) =>
    run(async () => {
      const assigned = hasTag(current, tag);
      await operation(
        assigned ? "tags.unassign" : "tags.assign",
        { gameId: current.gameId, tagId: tag.tagId, expectedRevision: current.revision },
        `tags.${assigned ? "unassign" : "assign"}:${current.gameId}:${tag.tagId}:${current.revision}`,
      );
      const refreshed = await operation<GameItem>("games.get", { gameId: current.gameId });
      setCurrent(refreshed.data);
      await onChanged();
    });

  const setPolicy = (value: string) =>
    run(async () => {
      if (!translation) return;
      const result = await operation<TranslationPolicy>(
        "translation.set",
        { gameId: current.gameId, override: value, expectedRevision: translation.revision },
        `translation.set:${current.gameId}:${translation.revision}`,
      );
      setTranslation(result.data);
      await load();
    });

  const importCover = () =>
    run(async () => {
      const defaultPath = await screenshotsDirectory().catch(() => undefined);
      const selected = await open({
        multiple: false,
        directory: false,
        title: "选择封面图片",
        defaultPath,
        filters: [{ name: "封面图片", extensions: ["png", "jpg", "jpeg", "webp", "gif"] }],
      });
      if (!selected) return;
      const result = await operation<{ assetId: string; warning?: string }>(
        "assets.import",
        { gameId: current.gameId, sourcePath: String(selected) },
        `assets.import:${current.gameId}:${String(selected)}`,
      );
      setCover(await assetDataUrl(result.data.assetId));
      await load();
      await onChanged();
      if (result.data.warning) setError(result.data.warning);
    });

  const addProfile = () =>
    run(async () => {
      const selected = await open({
        multiple: false,
        directory: false,
        title: "选择游戏主程序",
        defaultPath: await gameDirectory(current),
        filters: [{ name: "游戏程序", extensions: ["exe", "swf"] }],
      });
      if (!selected) return;
      const executablePath = String(selected);
      await operation(
        "profiles.create",
        {
          gameId: current.gameId,
          executablePath,
          argv: [],
          cwd: executablePath.replace(/[\\/][^\\/]+$/, ""),
          isDefault: profiles.length === 0,
        },
        `profiles.create:${current.gameId}:${executablePath}`,
      );
      await load();
    });

  const setDefaultProfile = (profile: ProfileItem) =>
    run(async () => {
      await operation(
        "profiles.set_default",
        { gameId: current.gameId, profileId: profile.profileId, expectedRevision: profile.revision },
        `profiles.set_default:${profile.profileId}:${profile.revision}`,
      );
      await load();
    });

  const removeProfile = (profile: ProfileItem) =>
    run(async () => {
      await operation(
        "profiles.remove",
        { profileId: profile.profileId, expectedRevision: profile.revision },
        `profiles.remove:${profile.profileId}:${profile.revision}`,
      );
      await load();
    });

  const removeGame = (deleteFiles = false) =>
    run(async () => {
      await operation(
        "games.remove",
        { gameId: current.gameId, expectedRevision: current.revision, ...(deleteFiles ? { deleteFiles: true, confirmedPath: current.rootPath } : {}) },
        `games.remove:${current.gameId}:${current.revision}:${deleteFiles}`,
      );
      setConfirmRemove(false);
      setConfirmDelete(false);
      onClose();
      await onChanged();
    });

  const openGameDirectory = () =>
    run(async () => {
      await invoke("open_game_directory", { path: await gameDirectory(current) });
    });

  const initials = current.title.slice(0, 2).toUpperCase();
  const defaultProfile = profiles.find((profile) => profile.isDefault) ?? profiles[0];

  return (
    <>
      <Sheet
        open
        onClose={onClose}
        title={current.title}
        description={current.rootPath}
      >
        <div className="space-y-5">
          <div className="relative aspect-16/9 overflow-hidden rounded-lg bg-surface-elevated">
            {cover ? (
              // object-contain：完整显示图片，不再像 v1.1.5 的 object-cover 那样固定裁掉一部分。
              <img src={cover} alt="" className="absolute inset-0 h-full w-full object-contain" />
            ) : (
              <div
                aria-hidden="true"
                className="absolute inset-0 flex items-center justify-center text-7xl font-black text-white/15"
              >
                {initials}
              </div>
            )}
          </div>

          {error && (
            <p role="alert" className="rounded-md border border-danger/40 bg-danger/10 p-3 text-sm break-words text-danger">
              {error}
            </p>
          )}

          <div className="grid grid-cols-2 gap-2">
            <Button className="col-span-2" size="lg" disabled={busy} onClick={() => void run(() => onPlay(current.gameId))}>
              <Play size={17} fill="currentColor" />
              开始游戏
            </Button>
            <Button variant="outline" disabled={busy} onClick={() => void toggleFavorite()}>
              <Star size={15} className={current.favorite ? "fill-favorite text-favorite" : undefined} />
              {current.favorite ? "取消收藏" : "收藏"}
            </Button>
            <Button variant="outline" disabled={busy} onClick={() => void openGameDirectory()}>
              <ExternalLink size={15} />
              打开目录
            </Button>
          </div>

          <Tabs defaultValue="overview">
            <TabsList aria-label="游戏详情分区">
              <TabsTrigger value="overview">概览</TabsTrigger>
              <TabsTrigger value="launch">启动</TabsTrigger>
              <TabsTrigger value="tags">标签</TabsTrigger>
              <TabsTrigger value="cover">封面</TabsTrigger>
            </TabsList>

            <TabsContent value="overview" className="space-y-4">
              <LabeledField label="标题">
                <div className="flex gap-2">
                  <Input
                    value={titleDraft}
                    onChange={(event) => setTitleDraft(event.currentTarget.value)}
                    aria-label="游戏标题"
                  />
                  <Button
                    variant="outline"
                    disabled={busy || titleDraft.trim() === current.title}
                    onClick={() => void saveField("title", titleDraft.trim())}
                  >
                    保存
                  </Button>
                </div>
              </LabeledField>

              <LabeledField label="简介">
                <Textarea
                  rows={4}
                  value={summaryDraft}
                  onChange={(event) => setSummaryDraft(event.currentTarget.value)}
                  onBlur={() => {
                    if (summaryDraft !== (current.summary ?? "")) void saveField("summary", summaryDraft);
                  }}
                  placeholder="写点什么，或留空使用自动生成的内容"
                  aria-label="游戏简介"
                />
              </LabeledField>

              <LabeledField label="翻译策略">
                <select
                  value={translation?.userOverride ?? "Auto"}
                  onChange={(event) => void setPolicy(event.target.value)}
                  aria-label="翻译策略"
                  className="h-10 w-full rounded-md border border-input bg-field px-3 text-sm text-text-primary focus-visible:border-steam focus-visible:outline-none"
                >
                  <option value="Auto">Auto · 继承目录约定并自动调用翻译工具</option>
                  <option value="Required">Required · 需要翻译插件或翻译工具</option>
                  <option value="NotRequired">NotRequired · 直接启动原文程序</option>
                </select>
                {translation && (
                  <p className="mt-2 text-xs text-text-secondary">
                    当前有效策略：{translation.effective}
                    {translation.inherited ? ` · 继承自 ${translation.inherited}` : ""}
                  </p>
                )}
              </LabeledField>

              <dl className="space-y-2 text-sm">
                <InfoRow label="路径" value={current.rootPath} />
                <InfoRow label="入库时间" value={formatTime(current.acceptedUtc)} />
                <InfoRow label="最近修改" value={formatTime(current.updatedUtc)} />
                <InfoRow label="可用状态" value={current.availability ?? "unknown"} />
                <InfoRow label="默认启动程序" value={defaultProfile?.executablePath ?? "尚未配置"} />
              </dl>

              <SimilarGamesSection similarTo={current.similarTo} onNavigate={onNavigate} />

              <Button variant="danger" className="w-full" disabled={busy} onClick={() => setConfirmRemove(true)}>
                <Trash2 size={15} />
                从游戏库移除
              </Button>
              <Button variant="danger" className="w-full border border-danger/40" disabled={busy} onClick={() => setConfirmDelete(true)}>
                <Trash2 size={15} />删除游戏及原文件
              </Button>
            </TabsContent>

            <TabsContent value="launch" className="space-y-3">
              <p className="text-xs text-text-secondary">
                配置游戏的原始主程序即可。内置 BepInEx + XUnity.AutoTranslator 的游戏直接启动原程序；
                外部翻译工具需要受支持的启动配方。
              </p>
              {profiles.length === 0 ? (
                <p className="rounded-md border border-dashed border-border p-4 text-sm text-text-secondary">
                  还没有配置启动方式。
                </p>
              ) : (
                <ul className="space-y-2">
                  {profiles.map((profile) => (
                    <li
                      key={profile.profileId}
                      className="rounded-md border border-border bg-surface p-3 text-sm"
                    >
                      <div className="flex items-start justify-between gap-2">
                        <span className="min-w-0 flex-1 truncate text-text-primary">
                          {profile.executablePath}
                        </span>
                        {profile.isDefault && <Badge variant="steam">默认</Badge>}
                      </div>
                      <div className="mt-1 truncate text-xs text-text-secondary">
                        工作目录：{profile.cwd ?? "—"}
                        {profile.argv && profile.argv.length > 0 ? ` · 参数：${profile.argv.join(" ")}` : ""}
                      </div>
                      <div className="mt-2 flex gap-2">
                        {!profile.isDefault && (
                          <Button size="sm" variant="outline" disabled={busy} onClick={() => void setDefaultProfile(profile)}>
                            <Check size={13} />
                            设为默认
                          </Button>
                        )}
                        <Button size="sm" variant="ghost" disabled={busy} onClick={() => void removeProfile(profile)}>
                          <Trash2 size={13} />
                          删除
                        </Button>
                      </div>
                    </li>
                  ))}
                </ul>
              )}
              <Button variant="outline" className="w-full" disabled={busy} onClick={() => void addProfile()}>
                <Play size={15} />
                添加启动方式
              </Button>
            </TabsContent>

            <TabsContent value="tags" className="space-y-2">
              <form className="flex gap-2" onSubmit={(event) => {
                event.preventDefault();
                if (!tagDraft.trim() || busy) return;
                void run(async () => {
                  const name = tagDraft.trim();
                  const existing = tags.find(tag => tag.kind === "user" && tag.name === name);
                  const tag = existing ?? (await operation<TagItem>("tags.create", { name }, `tags.create:${name}`)).data;
                  try {
                    if (!hasTag(current, tag)) {
                      await operation("tags.assign", { gameId: current.gameId, tagId: tag.tagId, expectedRevision: current.revision }, `tags.assign:${current.gameId}:${tag.tagId}:${current.revision}`);
                    }
                  } finally {
                    await onChanged();
                  }
                  setTagDraft("");
                  await load();
                });
              }}>
                <Input aria-label="新标签名称" placeholder="新标签名称" value={tagDraft} onChange={(event) => setTagDraft(event.target.value)} />
                <Button type="submit" disabled={busy || !tagDraft.trim()}>新建并添加</Button>
              </form>
              {tags.length === 0 ? (
                <p className="text-sm text-text-secondary">还没有标签，可以直接在上方新建。</p>
              ) : (
                <div className="flex flex-wrap gap-2">
                  {tags.map((tag) => {
                    const active = hasTag(current, tag);
                    return (
                      <button
                        key={tag.tagId}
                        type="button"
                        disabled={busy}
                        aria-pressed={active}
                        onClick={() => void toggleTag(tag)}
                        className={cn(
                          "cursor-pointer rounded-full border px-3 py-1 text-xs transition disabled:opacity-50",
                          active
                            ? "border-steam bg-steam-soft text-steam"
                            : "border-border text-text-secondary hover:border-steam",
                        )}
                      >
                        {tag.name}
                      </button>
                    );
                  })}
                </div>
              )}
            </TabsContent>

            <TabsContent value="cover" className="space-y-3">
              <p className="text-xs text-text-secondary">
                支持最大 5 MiB 图片，按原比例完整显示。游戏目录没有 cover 时，会保存一份 cover.原扩展名；已有 cover 不覆盖。
              </p>
              <Button variant="outline" className="w-full" disabled={busy} onClick={() => void importCover()}>
                <ImagePlus size={15} />
                导入封面图片
              </Button>
            </TabsContent>
          </Tabs>
        </div>
      </Sheet>

      <ConfirmDialog
        open={confirmDelete}
        title={`删除「${current.title}」及原文件`}
        description={`将以下位置移入回收站并移除游戏记录：${current.rootPath}。目录型游戏包含其中的存档、补丁和全部子文件；单文件游戏只删除该文件。请确认路径。`}
        confirmLabel="确认移入回收站"
        destructive
        busy={busy}
        onCancel={() => { if (!busy) setConfirmDelete(false); }}
        onConfirm={() => void removeGame(true)}
      />
      <ConfirmDialog
        open={confirmRemove}
        title={`从游戏库移除「${current.title}」`}
        description="只会解除游戏库中的记录，不会删除磁盘上的任何文件。该目录会被加入过滤名单，避免下次扫描又把它加回来。"
        confirmLabel="移除"
        destructive
        onCancel={() => setConfirmRemove(false)}
        onConfirm={() => void removeGame()}
      />
    </>
  );
}

function LabeledField({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div>
      <span className="mb-2 block text-[11px] tracking-[0.14em] text-text-secondary uppercase">
        {label}
      </span>
      {children}
    </div>
  );
}

/**
 * 「疑似重复」区块：similarTo 随 games.get 自然到达，零新增请求。
 * undefined（旧后端无字段）与 []（无指纹/无命中）都连标题一起完全隐藏。
 * 条目是原生 button，点击后 App 换 selected 实现「详情翻页」，Sheet 不关闭不重建。
 */
function SimilarGamesSection({
  similarTo,
  onNavigate,
}: {
  similarTo: SimilarGameSuggestion[] | undefined;
  onNavigate?: (gameId: string) => void;
}) {
  if (!similarTo || similarTo.length === 0) return null;

  return (
    <LabeledField label="疑似重复">
      <ul className="space-y-2">
        {similarTo.map((item) => {
          const percent = formatSimilarity(item.similarity);
          return (
            <li key={item.gameId}>
              <button
                type="button"
                aria-label={`查看《${item.title}》详情，相似度 ${percent}`}
                onClick={() => onNavigate?.(item.gameId)}
                className="flex w-full cursor-pointer items-center justify-between gap-2 rounded-md border border-border bg-surface px-3 py-2 text-left text-sm transition hover:border-steam focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
              >
                <span className="min-w-0 flex-1 truncate text-text-primary">《{item.title}》</span>
                <Badge variant="steam" aria-hidden="true" className="shrink-0">
                  相似度 {percent}
                </Badge>
              </button>
            </li>
          );
        })}
      </ul>
    </LabeledField>
  );
}

function InfoRow({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <dt className="text-[11px] tracking-[0.14em] text-text-secondary uppercase">{label}</dt>
      <dd className="mt-1 rounded-md bg-field p-2.5 break-all text-text-primary">{value}</dd>
    </div>
  );
}
