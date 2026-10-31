import { t } from "../lib/i18n";
import { useCallback, useEffect, useRef, useState, type ClipboardEvent, type DragEvent } from "react";
import { dirname, join, pictureDir } from "@tauri-apps/api/path";
import { open } from "@tauri-apps/plugin-dialog";
import { invoke } from "@tauri-apps/api/core";
import { Check, ExternalLink, ImagePlus, Play, Star, Trash2 } from "lucide-react";
import { assetDataUrl, describeFailure, operation, OperationError } from "../lib/api";
import type { TitleTranslationController } from "../lib/hooks/useTitleTranslation";
import { TitleTranslationStatus } from "./TitleTranslationStatus";
import { MToolGameTranslation } from "./MToolTranslation";
import type { UnityTranslationItem } from "./UnityTranslationDialog";
import { discoverLaunchProfiles, LaunchProfileSelectionRequired } from "../lib/launchProfiles";
import { TAG_CATEGORIES, groupTagsByCategory, tagCategory, tagLabel, tagStyle, type TagCategoryValue } from "../lib/tags";
import type {
  GameItem,
  ProfileItem,
  SimilarGameSuggestion,
  TagItem,
  TranslationPolicy,
} from "../lib/types";
import { cn, formatPlaytime, formatSimilarity, formatTime } from "../lib/utils";
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

interface CoverAsset { assetId: string; isCurrent: boolean; importedUtc?: string }

export function DetailSheet(props: Parameters<typeof DetailSheetContent>[0]) {
  return <DetailSheetContent key={props.game?.gameId ?? ""} {...props} />;
}

function DetailSheetContent({
  game,
  tags,
  onClose,
  onPlay,
  onChanged,
  onNavigate,
  refreshToken,
  supportsSuggestions = false,
  titleTranslation,
  onUnityTranslation,
  unityTranslationState,
  onUpdateTag,
  initialTab = "overview",
  ratingPromptToken,
}: {
  game: GameItem | null;
  tags: TagItem[];
  onClose: () => void;
  onPlay: (gameId: string, profileId?: string) => Promise<void>;
  refreshToken?: number;
  supportsSuggestions?: boolean;
  titleTranslation?: TitleTranslationController;
  onUnityTranslation?: (gameId: string, action: "configure" | "restore") => void;
  unityTranslationState?: UnityTranslationItem;
  onUpdateTag?: (tag: TagItem, changes: { category: TagCategoryValue }) => Promise<void>;
  initialTab?: "overview" | "launch";
  ratingPromptToken?: number;
  onChanged: () => Promise<void> | void;
  /** 点击「疑似重复」条目跳到目标游戏详情。 */
  onNavigate?: (gameId: string) => void;
}) {
  const [current, setCurrent] = useState<GameItem | null>(game);
  const [profiles, setProfiles] = useState<ProfileItem[]>([]);
  const [tab, setTab] = useState<string>(initialTab);
  const [translation, setTranslation] = useState<TranslationPolicy | null>(null);
  const [cover, setCover] = useState<string | null>(null);
  const [coverAssets, setCoverAssets] = useState<CoverAsset[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [titleDraft, setTitleDraft] = useState("");
  const [titleTarget, setTitleTarget] = useState<"original" | "translated">("original");
  const titleEdit = useRef({ dirty: false, revision: 0, version: 0 });
  const summaryEdit = useRef({ dirty: false, revision: 0, version: 0 });
  const currentRef = useRef(game);
  const alive = useRef(true);
  const session = useRef(0);
  const loadSerial = useRef(0);
  const profileSerial = useRef(0);
  const actionSerial = useRef(0);
  const coverRequest = useRef<AbortController | null>(null);
  const [summaryDraft, setSummaryDraft] = useState("");
  const [confirmRemove, setConfirmRemove] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [deletePlan, setDeletePlan] = useState<(NonNullable<GameItem["removalPlan"]> & { revision: number }) | null>(null);
  const [busy, setBusy] = useState(false);
  const [tagDraft, setTagDraft] = useState("");
  const [dragOverCategory, setDragOverCategory] = useState<TagCategoryValue | null>(null);
  const [categoryTagId, setCategoryTagId] = useState("");
  const [ratingPrompt, setRatingPrompt] = useState(false);

  const gameId = game?.gameId ?? null;
  useEffect(() => {
    alive.current = true;
    session.current++;
    return () => { alive.current = false; session.current++; coverRequest.current?.abort(); };
  }, []);
  useEffect(() => { setTab(initialTab); }, [gameId, initialTab]);
  useEffect(() => { setConfirmDelete(false); setDeletePlan(null); }, [gameId]);
  useEffect(() => {
    if (ratingPromptToken === undefined) { setRatingPrompt(false); return; }
    setTab("overview");
    setRatingPrompt(true);
    const timer = window.setTimeout(() => setRatingPrompt(false), 12000);
    return () => window.clearTimeout(timer);
  }, [gameId, ratingPromptToken]);

  const mergeDetail = useCallback((detail: GameItem) => {
    if (!alive.current || (currentRef.current && currentRef.current.revision > detail.revision)) return;
    currentRef.current = detail;
    setCurrent(detail);
    if (!titleEdit.current.dirty) {
      setTitleDraft(detail.title);
      setTitleTarget(detail.titleDisplayMode ?? "original");
      titleEdit.current.revision = detail.revision;
    }
    if (!summaryEdit.current.dirty) {
      setSummaryDraft(detail.summary ?? "");
      summaryEdit.current.revision = detail.revision;
    }
  }, []);

  const readCover = useCallback((assetId: string) => {
    coverRequest.current?.abort();
    const request = new AbortController();
    coverRequest.current = request;
    return assetDataUrl(assetId, request.signal);
  }, []);

  const load = useCallback(async () => {
    if (!gameId || !alive.current) return;
    coverRequest.current?.abort();
    const epoch = session.current;
    const request = ++loadSerial.current;
    const profileRequest = ++profileSerial.current;
    const valid = () => alive.current && session.current === epoch && loadSerial.current === request;
    setError(null);
    try {
      const [detail, profileResult, translationResult] = await Promise.all([
        operation<GameItem>("games.get", { gameId }),
        operation<{ items: ProfileItem[] }>("profiles.list", { gameId }),
        operation<TranslationPolicy>("translation.get", { gameId }),
      ]);
      if (!valid()) return;
      mergeDetail(detail.data);
      if (profileSerial.current === profileRequest) setProfiles(profileResult.data.items);
      setTranslation(translationResult.data);
      const coverAssetId = currentRef.current?.coverAssetId;
      if (coverAssetId) {
        const url = await readCover(coverAssetId).catch(() => null);
        if (!valid() || currentRef.current?.coverAssetId !== coverAssetId) return;
        setCover(url);
      } else {
        setCover(null);
      }
      return currentRef.current ?? undefined;
    } catch (cause) {
      if (valid()) setError(describeFailure(cause));
    }
  }, [gameId, mergeDetail, readCover]);

  useEffect(() => {
    setCurrent(game);
    setCover(null);
    setProfiles([]);
    setTranslation(null);
    setTitleDraft(game?.title ?? "");
    setTitleTarget(game?.titleDisplayMode ?? "original");
    titleEdit.current = { revision: game?.revision ?? 0, dirty: false, version: 0 };
    summaryEdit.current = { revision: game?.revision ?? 0, dirty: false, version: 0 };
    setSummaryDraft(game?.summary ?? "");
    setTagDraft("");
    if (gameId) void load();
  // Reset drafts only when navigating to another game.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [gameId, load]);

  useEffect(() => {
    if (game) mergeDetail(game);
  }, [game, mergeDetail]);

  const previousRefresh = useRef(refreshToken);
  useEffect(() => {
    if (previousRefresh.current === refreshToken) return;
    previousRefresh.current = refreshToken;
    if (!gameId) return;
    const request = ++profileSerial.current;
    let cancelled = false;
    void operation<{ items: ProfileItem[] }>("profiles.list", { gameId }).then(result => {
      if (!cancelled && alive.current && profileSerial.current === request) setProfiles(result.data.items);
    }).catch(() => undefined);
    return () => { cancelled = true; };
  }, [gameId, refreshToken]);

  useEffect(() => {
    setCoverAssets([]);
    if (!gameId || tab !== "cover") return;
    let cancelled = false;
    void operation<{ items: CoverAsset[] }>("assets.list", { gameId }).then(result => {
      if (!cancelled) setCoverAssets(result.data.items ?? []);
    }).catch(cause => { if (!cancelled) setError(describeFailure(cause)); });
    return () => { cancelled = true; };
  }, [gameId, tab, current?.coverAssetId]);

  if (!current) return null;

  const run = async (work: (valid: () => boolean) => Promise<void>, dismissDeleteDialog = true) => {
    const epoch = session.current;
    const request = ++actionSerial.current;
    const valid = () => alive.current && session.current === epoch && actionSerial.current === request;
    setBusy(true);
    setError(null);
    try {
      await work(valid);
    } catch (cause) {
      if (!valid()) return;
      if (cause instanceof LaunchProfileSelectionRequired) setTab("launch");
      setError(describeFailure(cause));
    } finally {
      if (valid()) { if (dismissDeleteDialog) setConfirmDelete(false); setBusy(false); }
    }
  };

  const saveField = (field: "title" | "summary", value: string) =>
    run(async valid => {
      const translated = field === "title" && titleTarget === "translated";
      const operationId = translated ? "titles.set_translated" : "fields.set";
      const edit = field === "title" ? titleEdit.current : summaryEdit.current;
      const { revision, version } = edit;
      try {
        await operation(operationId,
          { gameId: current.gameId, ...(translated ? { title: value } : { field, value }), expectedRevision: revision },
          `${operationId}:${current.gameId}:${field}:${revision}:${value}`);
      } catch (cause) {
        if (cause instanceof OperationError && cause.code === "RevisionConflict") {
          const refreshed = await load();
          if (refreshed && valid()) edit.revision = refreshed.revision;
        }
        throw cause;
      }
      if (!valid()) return;
      if (edit.version === version) edit.dirty = false;
      // fields.set 只返回字段修订回执，不是完整 GameItem；重新读取后再渲染，
      // 避免把缺少 title/rootPath 的回执写入 current 导致整页白屏。
      const refreshed = await load();
      if (refreshed && valid()) edit.revision = refreshed.revision;
      await onChanged();
    });

  const updatePreference = (patch: { played?: boolean; rating?: number }) =>
    run(async () => {
      await operation(
        "games.update",
        { gameId: current.gameId, ...patch, expectedRevision: current.revision },
        `games.update:${current.gameId}:${current.revision}:${JSON.stringify(patch)}`,
      );
      setRatingPrompt(false);
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
      await load();
      await onChanged();
    });

  const moveTag = (tag: TagItem, category: TagCategoryValue) => {
    if (busy || !onUpdateTag || tagCategory(tag) === category) return;
    void run(() => onUpdateTag(tag, { category }));
  };
  const dropTag = (category: TagCategoryValue, event: DragEvent) => {
    event.preventDefault();
    setDragOverCategory(null);
    const tag = tags.find(item => item.tagId === event.dataTransfer.getData("text/plain"));
    if (tag) moveTag(tag, category);
  };

  const setPolicy = (value: string) =>
    run(async valid => {
      if (!translation) return;
      const result = await operation<TranslationPolicy>(
        "translation.set",
        { gameId: current.gameId, override: value, expectedRevision: translation.revision },
        `translation.set:${current.gameId}:${translation.revision}`,
      );
      if (!valid()) return;
      setTranslation(result.data);
      await load();
    });

  const updateCover = async (result: { assetId: string; warning?: string }) => {
    if (!alive.current) return;
    const pendingCover = readCover(result.assetId);
    const request = coverRequest.current;
    let url: string;
    try { url = await pendingCover; }
    catch (error) {
      if (error instanceof DOMException && error.name === "AbortError") return;
      throw error;
    }
    if (!alive.current || coverRequest.current !== request) return;
    setCover(url);
    await load();
    await onChanged();
    if (alive.current && result.warning) setError(result.warning);
  };

  const pasteCover = (event: ClipboardEvent<HTMLButtonElement>) => {
    const file = Array.from(event.clipboardData.items).find(item => item.kind === "file" && item.type.startsWith("image/"))?.getAsFile();
    if (!file || busy) return;
    event.preventDefault();
    void run(async () => {
      if (file.size > 5 * 1024 * 1024) throw new Error(t("图片超过 5 MiB 上限"));
      const imageBase64 = await new Promise<string>((resolve, reject) => {
        const reader = new FileReader();
        reader.onload = () => resolve(String(reader.result).split(",")[1]);
        reader.onerror = () => reject(new Error(t("无法读取粘贴图片")));
        reader.readAsDataURL(file);
      });
      if (!alive.current) return;
      const result = await operation<{ assetId: string; warning?: string }>("assets.import",
        { gameId: current.gameId, imageBase64, mimeType: file.type },
        `assets.import:${current.gameId}:paste:${imageBase64}`);
      await updateCover(result.data);
    });
  };

  const importCover = () =>
    run(async () => {
      const defaultPath = await screenshotsDirectory().catch(() => undefined);
      const selected = await open({
        multiple: false,
        directory: false,
        title: t("选择封面图片"),
        defaultPath,
        filters: [{ name: t("封面图片"), extensions: ["png", "jpg", "jpeg", "webp", "gif"] }],
      });
      if (!selected) return;
      if (!alive.current) return;
      const result = await operation<{ assetId: string; warning?: string }>(
        "assets.import",
        { gameId: current.gameId, sourcePath: String(selected) },
        `assets.import:${current.gameId}:${String(selected)}`,
      );
      await updateCover(result.data);
    });

  const addProfile = () =>
    run(async () => {
      const selected = await open({
        multiple: false,
        directory: false,
        title: t("选择游戏主程序"),
        defaultPath: await gameDirectory(current),
        filters: [{ name: t("游戏程序"), extensions: ["exe", "swf", "bat"] }],
      });
      if (!selected || !alive.current) return;
      const executablePath = String(selected);
      await operation(
        "profiles.create",
        {
          gameId: current.gameId,
          executablePath,
          argv: [],
          cwd: executablePath.replace(/[\\/][^\\/]+$/, ""),
          isDefault: !profiles.some(profile => profile.isDefault),
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
        { gameId: current.gameId, expectedRevision: deleteFiles ? deletePlan!.revision : current.revision,
          ...(deleteFiles ? { deleteFiles: true, confirmedPath: deletePlan!.targetPath } : {}) },
        `games.remove:${current.gameId}:${deleteFiles ? deletePlan!.revision : current.revision}:${deleteFiles}`,
      );
      if (!alive.current) return;
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
  const showRatingPrompt = ratingPrompt && !(current.rating && current.rating > 0);
  const defaultProfile = profiles.find((profile) => profile.isDefault);

  return (
    <>
      <Sheet
        open
        onClose={onClose}
        title={current.title}
        description={current.rootPath}
      >
        <div className="space-y-5">
          <button type="button" aria-label={t("封面：点击后按 Ctrl+V 粘贴图片")} disabled={busy}
            onClick={event => { event.currentTarget.focus(); setTab("cover"); }} onPaste={pasteCover}
            className="relative block aspect-16/9 w-full cursor-pointer overflow-hidden rounded-lg bg-surface-elevated focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring">
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
          </button>

          {error && (
            <p role="alert" className="rounded-md border border-danger/40 bg-danger/10 p-3 text-sm break-words text-danger">
              {error}
            </p>
          )}

          <div className="grid grid-cols-2 gap-2">
            <Button className="col-span-2" size="lg" disabled={busy} onClick={() => void run(() => onPlay(current.gameId))}>
              <Play size={17} fill="currentColor" />{t("开始游戏")}</Button>
            <Button variant="outline" disabled={busy} aria-pressed={!!current.played} onClick={() => void updatePreference({ played: !current.played })}>
              <Check size={15} className={current.played ? "text-steam" : "text-text-secondary"} />
              {current.played ? t("已游玩") : t("标记为已游玩")}
            </Button>
            <Button variant="outline" disabled={busy} onClick={() => void openGameDirectory()}>
              <ExternalLink size={15} />{t("打开目录")}</Button>
          </div>

          <div key={ratingPromptToken ?? "rating"} className={cn("rounded-lg border p-3 transition-colors", showRatingPrompt ? "border-favorite bg-favorite/10 ring-2 ring-favorite/30 motion-safe:animate-pulse" : "border-border")}
            role="group" aria-label={t("游戏评分")}>
            <div className="flex items-center justify-between gap-2">
              <span className="text-sm font-medium">{t("游戏评分")}</span>
              <span className="text-xs text-text-secondary">{current.rating ? t("{0} / 5 星", current.rating) : t("尚未评分")}</span>
            </div>
            <div className="mt-2 flex items-center gap-1">
              {[1, 2, 3, 4, 5].map(stars => <button key={stars} type="button" disabled={busy}
                aria-label={t("评为 {0} 星", stars)} aria-pressed={current.rating === stars}
                onClick={() => void updatePreference({ rating: stars })}
                className="cursor-pointer rounded p-2 text-text-secondary transition hover:text-favorite focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:cursor-wait">
                <Star size={23} className={stars <= (current.rating ?? 0) ? "fill-favorite text-favorite" : undefined} />
              </button>)}
              {!!current.rating && <Button variant="ghost" size="sm" disabled={busy} onClick={() => void updatePreference({ rating: 0 })}>{t("清除评分")}</Button>}
            </div>
            {showRatingPrompt && <p role="status" className="mt-2 text-xs text-favorite">{t("这次游玩体验如何？点选星星为游戏评分。")}</p>}
          </div>

          <Tabs value={tab} onValueChange={setTab}>
            <TabsList aria-label={t("游戏详情分区")}>
              <TabsTrigger value="overview">{t("概览")}</TabsTrigger>
              <TabsTrigger value="launch">{t("启动")}</TabsTrigger>
              <TabsTrigger value="tags">{t("标签")}</TabsTrigger>
              <TabsTrigger value="cover">{t("封面")}</TabsTrigger>
            </TabsList>

            <TabsContent value="overview" className="space-y-4">
              {titleTranslation && <>
                <div className="flex flex-wrap gap-2">
                  <Button variant="outline" disabled={busy || titleTranslation.busy} onClick={() => void titleTranslation.start([current.gameId], !!current.translatedTitle)}>{current.translatedTitle ? t("重新翻译名称") : t("翻译为简体中文")}</Button>
                  {current.translatedTitle && <Button variant="outline" disabled={busy || titleTranslation.busy} onClick={() => void run(async () => {
                    await operation("titles.set_display", { gameId: current.gameId, mode: current.titleDisplayMode === "translated" ? "original" : "translated", expectedRevision: current.revision }, `titles.set_display:${current.gameId}:${current.revision}`);
                    await load(); await onChanged();
                  })}>{current.titleDisplayMode === "translated" ? t("显示原文") : t("显示中文译名")}</Button>}
                </div>
                <TitleTranslationStatus controller={titleTranslation} />
              </>}
              <LabeledField label={titleTarget === "translated" ? t("标题（中文译名）") : t("标题（原文）")}>
                <div className="flex gap-2">
                  <Input
                    value={titleDraft}
                    onChange={(event) => { titleEdit.current.dirty = true; titleEdit.current.version++; setTitleDraft(event.currentTarget.value); }}
                    aria-label={t("游戏标题")}
                  />
                  <Button
                    variant="outline"
                    disabled={busy || !titleDraft.trim() || titleDraft.trim() === (titleTarget === "translated" ? current.translatedTitle : current.originalTitle ?? current.title)}
                    onClick={() => void saveField("title", titleDraft.trim())}
                  >{t("保存")}</Button>
                </div>
              </LabeledField>

              <LabeledField label={t("简介")}>
                <Textarea
                  rows={4}
                  value={summaryDraft}
                  onChange={(event) => { summaryEdit.current.dirty = true; summaryEdit.current.version++; setSummaryDraft(event.currentTarget.value); }}
                  onBlur={() => {
                    if (summaryDraft !== (current.summary ?? "")) void saveField("summary", summaryDraft);
                  }}
                  placeholder={t("写点什么")}
                  aria-label={t("游戏简介")}
                />
              </LabeledField>

              <LabeledField label={t("翻译策略")}>
                <select
                  value={translation?.userOverride ?? "Auto"}
                  onChange={(event) => void setPolicy(event.target.value)}
                  aria-label={t("翻译策略")}
                  className="h-10 w-full rounded-md border border-input bg-field px-3 text-sm text-text-primary focus-visible:border-steam focus-visible:outline-none"
                >
                  <option value="Auto">{t("Auto·自动翻译")}</option>
                  <option value="Required">{t("Required·需要翻译")}</option>
                  <option value="NotRequired">{t("NotRequired·原生启动")}</option>
                </select>
              </LabeledField>

              <dl className="space-y-2 text-sm">
                <InfoRow label={t("路径")} value={current.rootPath} />
                {/* feat-1：游玩统计（launch_attempts 聚合，v1.5 起 GameDto 携带）。 */}
                <InfoRow label={t("游玩时长")} value={formatPlaytime(current.playtimeMinutes) || t("尚未游玩")} />
                <InfoRow label={t("最近游玩")} value={formatTime(current.lastPlayedUtc)} />
                <InfoRow label={t("入库时间")} value={formatTime(current.acceptedUtc)} />
                <InfoRow label={t("最近修改")} value={formatTime(current.updatedUtc)} />
                <InfoRow label={t("可用状态")} value={current.availability ?? "unknown"} />
                <InfoRow label={t("默认启动程序")} value={defaultProfile?.executablePath ?? t("尚未配置")} />
              </dl>

              <SimilarGamesSection similarTo={current.similarTo} onNavigate={onNavigate} />

              <Button variant="danger" className="w-full" disabled={busy} onClick={() => setConfirmRemove(true)}>
                <Trash2 size={15} />{t("从游戏库移除")}</Button>
              <Button variant="danger" className="w-full border border-danger/40" disabled={busy} onClick={() => void run(async valid => {
                const result = await operation<GameItem>("games.get", { gameId: current.gameId, includeRemovalPlan: true });
                if (!valid()) return;
                if (!result.data.removalPlan) throw new Error(t("无法读取删除范围，请更新程序后重试"));
                if (result.data.removalPlan.error) throw new Error(t(result.data.removalPlan.error));
                setDeletePlan({ ...result.data.removalPlan, revision: result.data.revision });
                setConfirmDelete(true);
              }, false)}>
                <Trash2 size={15} />{t("删除游戏及原文件")}</Button>
            </TabsContent>

            <TabsContent value="launch" className="space-y-3">
              {current.engine?.toLowerCase() !== "unity" && <MToolGameTranslation key={current.gameId} gameId={current.gameId} />}
              {current.engine?.toLowerCase() === "unity" && onUnityTranslation && <div className="space-y-2 rounded-md border border-border p-3">
                <h3 className="text-sm font-semibold">{t("Unity 游戏内翻译")}</h3>
                {unityTranslationState && <p role={['failed', 'blocked'].includes(unityTranslationState.state) ? "alert" : "status"}
                  className="break-words text-xs text-text-secondary">
                  {t(unityTranslationState.reason || ({ queued: "等待配置翻译插件", inspecting: "正在检查翻译插件", installing: "正在安装翻译插件",
                    configured: "翻译插件已配置，等待运行确认", confirmed: "已确认翻译插件运行成功", failed: "翻译插件配置失败",
                    blocked: "翻译插件配置受阻", needs_settings: "请先配置翻译服务商", restored: "已恢复安装前状态" } as Record<string, string>)[unityTranslationState.state] || unityTranslationState.state)}
                </p>}
                <div className="flex flex-wrap gap-2">
                  <Button variant="outline" disabled={busy || ['queued', 'inspecting', 'installing'].includes(unityTranslationState?.state ?? '')} onClick={() => onUnityTranslation(current.gameId, "configure")}>{t("配置或重试翻译插件")}</Button>
                  <Button variant="outline" disabled={busy || ['queued', 'inspecting', 'installing'].includes(unityTranslationState?.state ?? '')} onClick={() => onUnityTranslation(current.gameId, "restore")}>{t("恢复安装前状态")}</Button>
                </div>
              </div>}
              {supportsSuggestions && <>
                <Button variant="outline" disabled={busy} onClick={() => void run(async () => { await discoverLaunchProfiles(current.gameId); await load(); })}>{t("自动识别启动方式")}</Button>
              </>}
              {profiles.length === 0 ? (
                <p className="rounded-md border border-dashed border-border p-4 text-sm text-text-secondary">{t("还没有配置启动方式。")}</p>
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
                        {profile.isDefault && <Badge variant="steam">{t("默认")}</Badge>}
                      </div>
                      <div className="mt-1 truncate text-xs text-text-secondary">{t("工作目录：")}{profile.cwd ?? "—"}
                        {profile.argv && profile.argv.length > 0 ? t(" · 参数：{0}", profile.argv.join(" ")) : ""}
                      </div>
                      <div className="mt-2 flex gap-2">
                        {profile.source === "automatic" && <Badge>{t(profile.validationStatus === "verifying" ? "试运行验证中" : profile.validationStatus === "verified" ? "已验证" : profile.validationStatus === "discarded" ? "已废弃" : profile.validationStatus === "inconclusive" ? "待确认" : "建议入口")}</Badge>}
                        {profile.validationStatus === "discarded" ? <Button size="sm" variant="outline" disabled={busy} onClick={() => void run(async () => {
                          await operation("profiles.restore", { profileId: profile.profileId, expectedRevision: profile.revision }, `profiles.restore:${profile.profileId}:${profile.revision}`); await load();
                        })}>{t("恢复入口")}</Button> : <Button size="sm" variant="outline" disabled={busy || profile.validationStatus === "verifying"} onClick={() => void run(() => onPlay(current.gameId, profile.profileId))}><Play size={13} />{t("开始游戏")}</Button>}
                        {!profile.isDefault && profile.validationStatus !== "discarded" && (
                          <Button size="sm" variant="outline" disabled={busy} onClick={() => void setDefaultProfile(profile)}>
                            <Check size={13} />{t("设为默认")}</Button>
                        )}
                        <Button size="sm" variant="ghost" disabled={busy} onClick={() => void removeProfile(profile)}>
                          <Trash2 size={13} />{t("删除")}</Button>
                      </div>
                      {profile.suggestionReasons?.some(reason => ["chinese-name", "chinese-original-pair", "multiple-launch-stages"].includes(reason)) ? <p className="mt-2 text-xs text-text-secondary">{profile.suggestionReasons.filter(reason => ["chinese-name", "chinese-original-pair", "multiple-launch-stages"].includes(reason)).map(reason => t(reason === "chinese-name" ? "中文入口名称" : reason === "chinese-original-pair" ? "与原版入口配对" : "存在多个启动入口")).filter((value, index, values) => values.indexOf(value) === index).join(" · ")}</p> : null}
                    </li>
                  ))}
                </ul>
              )}
              <Button variant="outline" className="w-full" disabled={busy} onClick={() => void addProfile()}>
                <Play size={15} />{t("添加启动方式")}</Button>
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
                   if (alive.current) setTagDraft("");
                  await load();
                });
              }}>
                <Input aria-label={t("新标签名称")} placeholder={t("新标签名称")} value={tagDraft} onChange={(event) => setTagDraft(event.target.value)} />
                <Button type="submit" disabled={busy || !tagDraft.trim()}>{t("新建并添加")}</Button>
              </form>
              {tags.length === 0 ? (
                <p className="text-sm text-text-secondary">{t("还没有标签，可以直接在上方新建。")}</p>
              ) : (
                <div className="space-y-4">
                  {onUpdateTag && <>
                    <p className="text-xs text-text-secondary">{t("拖动标签到其他分类；分类调整对整个游戏库生效。")}</p>
                    <details className="text-xs text-text-secondary">
                      <summary className="cursor-pointer">{t("调整标签分类")}</summary>
                      <div className="mt-2 flex gap-2">
                        <select aria-label={t("选择标签")} disabled={busy}
                          value={tags.some(tag => tag.tagId === categoryTagId) ? categoryTagId : tags[0].tagId}
                          onChange={event => setCategoryTagId(event.target.value)} className="min-w-0 flex-1 rounded border border-border bg-surface p-1">
                          {tags.map(tag => <option key={tag.tagId} value={tag.tagId}>{tagLabel(tag)}</option>)}
                        </select>
                        <select aria-label={t("标签分类")} disabled={busy}
                          value={tagCategory(tags.find(tag => tag.tagId === categoryTagId) ?? tags[0])}
                          onChange={event => moveTag(tags.find(tag => tag.tagId === categoryTagId) ?? tags[0], event.target.value as TagCategoryValue)}
                          className="rounded border border-border bg-surface p-1">
                          {TAG_CATEGORIES.map(category => <option key={category.value} value={category.value}>{t(category.label)}</option>)}
                        </select>
                      </div>
                    </details>
                  </>}
                  {groupTagsByCategory(tags)
                    .filter((group) => onUpdateTag || group.items.length > 0)
                    .map((group) => (
                      <div key={group.value} role="group" aria-label={t(group.label)}
                        onDragOver={event => {
                          if (busy || !onUpdateTag) return;
                          event.preventDefault(); event.dataTransfer.dropEffect = "move"; setDragOverCategory(group.value);
                        }}
                        onDragLeave={event => {
                          if (!event.currentTarget.contains(event.relatedTarget as Node)) setDragOverCategory(null);
                        }}
                        onDrop={event => dropTag(group.value, event)}
                        className={cn("rounded-md p-2 transition-colors", dragOverCategory === group.value && "ring-2 ring-steam bg-steam/10")}>
                        <h4 className="mb-2 text-[11px] font-semibold tracking-[0.14em] text-text-secondary uppercase">
                          {t(group.label)} · {group.items.length}
                        </h4>
                        <div className="flex flex-wrap gap-2">
                          {group.items.length === 0 && <p className="text-xs text-text-secondary">{t("无（可把标签拖到这里）")}</p>}
                          {group.items.map((tag) => {
                            const active = hasTag(current, tag);
                            return (
                              <button
                                key={tag.tagId}
                                type="button"
                                disabled={busy}
                                aria-pressed={active}
                                draggable={!!onUpdateTag && !busy}
                                onDragStart={event => { event.dataTransfer.setData("text/plain", tag.tagId); event.dataTransfer.effectAllowed = "move"; }}
                                onDragEnd={() => setDragOverCategory(null)}
                                onClick={() => void toggleTag(tag)}
                                className={cn(
                                  "inline-flex cursor-pointer items-center gap-1.5 rounded-full border px-3 py-1 text-xs transition disabled:opacity-50",
                                  active
                                    ? "border-steam bg-steam-soft text-steam"
                                    : "border-border text-text-secondary hover:border-steam",
                                )}
                                style={tagStyle(tag)}
                              >
                                <span aria-hidden="true" className="size-2 shrink-0 rounded-full bg-current" />
                                {tagLabel(tag)}
                              </button>
                            );
                          })}
                        </div>
                      </div>
                    ))}
                </div>
              )}
            </TabsContent>

            <TabsContent value="cover" className="space-y-3">
              <p className="text-xs text-text-secondary">{t("最大支持 5 MB 的图片文件")}</p>
              <p className="text-xs text-text-secondary">{t("点击上方封面后按 Ctrl+V 粘贴本地图片。")}</p>
              <Button variant="outline" className="w-full" disabled={busy} onClick={() => void importCover()}>
                <ImagePlus size={15} />{t("导入封面图片")}</Button>
              {coverAssets.some(asset => !asset.isCurrent) && <div className="space-y-2">
                <p className="text-xs text-text-secondary">{t("历史封面")}</p>
                {coverAssets.filter(asset => !asset.isCurrent).map((asset, index) => (
                  <Button key={asset.assetId} variant="outline" className="w-full" disabled={busy} onClick={() => void run(async () => {
                    const result = await operation<{ assetId: string; warning?: string }>("assets.choose",
                      { gameId: current.gameId, assetId: asset.assetId, expectedRevision: current.revision },
                      `assets.choose:${current.gameId}:${asset.assetId}:${current.revision}`);
                    await updateCover(result.data);
                  })}>{t("恢复封面 {0}", index + 1)}{asset.importedUtc ? ` · ${formatTime(asset.importedUtc)}` : ""}</Button>
                ))}
              </div>}
            </TabsContent>
          </Tabs>
        </div>
      </Sheet>

      <ConfirmDialog
        open={confirmDelete}
        title={t("删除「{0}」及原文件", current.title)}
        description={t(deletePlan?.scope === "directory"
          ? "将整个游戏文件夹移入回收站，包含资源、存档、补丁及全部子文件：{0}。请确认路径。"
          : "将这个独立游戏文件移入回收站，不删除同目录的其他游戏：{0}。请确认路径。", deletePlan?.targetPath ?? "")}
        confirmLabel={t("确认移入回收站")}
        destructive
        busy={busy}
        onCancel={() => { if (!busy) setConfirmDelete(false); }}
        onConfirm={() => void removeGame(true)}
      />
      <ConfirmDialog
        open={confirmRemove}
        title={t("从游戏库移除「{0}」", current.title)}
        description={t("只会解除游戏库中的记录，不会删除磁盘上的任何文件。该目录会被加入过滤名单，避免下次扫描又把它加回来。")}
        confirmLabel={t("移除")}
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
    <LabeledField label={t("疑似重复")}>
      <ul className="space-y-2">
        {similarTo.map((item) => {
          const percent = formatSimilarity(item.similarity);
          return (
            <li key={item.gameId}>
              <button
                type="button"
                aria-label={t("查看《{0}》详情，相似度 {1}", item.title, percent)}
                onClick={() => onNavigate?.(item.gameId)}
                className="flex w-full cursor-pointer items-center justify-between gap-2 rounded-md border border-border bg-surface px-3 py-2 text-left text-sm transition hover:border-steam focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
              >
                <span className="min-w-0 flex-1 truncate text-text-primary">《{item.title}》</span>
                <Badge variant="steam" aria-hidden="true" className="shrink-0">{t("相似度")}{percent}
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
