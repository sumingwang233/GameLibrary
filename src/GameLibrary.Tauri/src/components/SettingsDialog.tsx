import { useEffect, useState } from "react";
import { getVersion } from "@tauri-apps/api/app";
import { open } from "@tauri-apps/plugin-dialog";
import { openUrl } from "@tauri-apps/plugin-opener";
import { Download, FolderOpen, Power, RefreshCw, RotateCcw } from "lucide-react";
import { describeFailure, operation } from "../lib/api";
import { useSettings } from "../lib/settings";
import { checkForUpdate, type UpdateInfo } from "../lib/update";
import { Button } from "./ui/button";
import { ConfirmDialog } from "./ui/confirm-dialog";
import { Input } from "./ui/input";
import { Sheet } from "./ui/sheet";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "./ui/tabs";

const FONT_CHOICES = ["Microsoft YaHei UI", "Segoe UI", "Microsoft YaHei", "Inter", "system-ui"];

export function SettingsDialog({ open: isOpen, onClose }: { open: boolean; onClose: () => void }) {
  const { settings, error: settingsError, update, reload } = useSettings();
  const [cacheDraft, setCacheDraft] = useState<string | null>(null);
  const [fontDraft, setFontDraft] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [localError, setLocalError] = useState<string | null>(null);
  const [updateInfo, setUpdateInfo] = useState<UpdateInfo | null>(null);
  const [confirmStop, setConfirmStop] = useState(false);
  const [appVersion, setAppVersion] = useState("…");

  useEffect(() => {
    if (!isOpen) return;
    void getVersion().then(setAppVersion).catch(() => setAppVersion("未知"));
  }, [isOpen]);

  if (!isOpen) return null;
  const error = localError ?? settingsError;
  const cacheDirectory = cacheDraft ?? settings?.cacheParentDirectory ?? "";
  const fontFamily = fontDraft ?? settings?.uiFontFamily ?? "Microsoft YaHei UI";

  const patch = async (change: Record<string, unknown>) => {
    setBusy(true);
    setLocalError(null);
    const result = await update(change);
    setBusy(false);
    return result !== null;
  };

  const runRaw = async (work: () => Promise<void>) => {
    setBusy(true);
    setLocalError(null);
    try {
      await work();
    } catch (cause) {
      setLocalError(describeFailure(cause));
    } finally {
      setBusy(false);
    }
  };

  const stopHost = () =>
    runRaw(async () => {
      await operation("host.stop", {}, "host.stop");
      setConfirmStop(false);
      onClose();
    });

  const resetSettings = () =>
    runRaw(async () => {
      await operation("settings.reset", {}, "settings.reset");
      setCacheDraft(null);
      setFontDraft(null);
      await reload();
    });

  return (
    <>
      <Sheet open={isOpen} onClose={onClose} title="设置" description="外观、扫描、存储与关于">
        {error && (
          <p role="alert" className="mb-4 rounded-md border border-danger/40 bg-danger/10 p-3 text-sm text-danger">
            {error}
          </p>
        )}

        {!settings ? (
          <p className="text-sm text-text-secondary">正在读取设置…</p>
        ) : (
          <Tabs defaultValue="appearance">
            <TabsList aria-label="设置分区">
              <TabsTrigger value="appearance">外观</TabsTrigger>
              <TabsTrigger value="library">游戏库</TabsTrigger>
              <TabsTrigger value="storage">存储</TabsTrigger>
              <TabsTrigger value="guide">使用指南</TabsTrigger>
              <TabsTrigger value="about">关于</TabsTrigger>
            </TabsList>

            <TabsContent value="appearance" className="space-y-5">
              <Field label="主题" hint="深色 / 浅色 / 跟随系统。选择后立即生效。">
                <select
                  value={settings.theme}
                  disabled={busy}
                  onChange={(event) => void patch({ theme: event.target.value })}
                  aria-label="主题"
                  className="h-10 w-full rounded-md border border-input bg-field px-3 text-sm text-text-primary focus-visible:border-steam focus-visible:outline-none"
                >
                  <option value="dark">深色</option>
                  <option value="light">浅色</option>
                  <option value="system">跟随系统</option>
                </select>
              </Field>

              <Field
                label={`界面缩放 ${Math.round(settings.uiFontScale * 100)}%`}
                hint="整体文字与间距按比例缩放（85%–160%）。"
              >
                <input
                  type="range"
                  min={0.85}
                  max={1.6}
                  step={0.05}
                  value={settings.uiFontScale}
                  disabled={busy}
                  onChange={(event) => void patch({ uiFontScale: Number(event.target.value) })}
                  aria-label="界面缩放"
                  className="w-full accent-steam"
                />
              </Field>

              <Field label="字体" hint="界面使用的字体族。">
                <select
                  value={fontFamily}
                  disabled={busy}
                  onChange={(event) => {
                    setFontDraft(event.target.value);
                    void patch({ uiFontFamily: event.target.value });
                  }}
                  aria-label="字体"
                  className="h-10 w-full rounded-md border border-input bg-field px-3 text-sm text-text-primary focus-visible:border-steam focus-visible:outline-none"
                >
                  {FONT_CHOICES.map((choice) => (
                    <option key={choice} value={choice}>
                      {choice}
                    </option>
                  ))}
                </select>
              </Field>

              <Toggle
                label="关闭窗口时最小化到托盘"
                hint="关闭后程序继续在后台运行，可从托盘图标重新打开。"
                checked={settings.closeToTray}
                disabled={busy}
                onChange={(checked) => void patch({ closeToTray: checked })}
              />
            </TabsContent>

            <TabsContent value="library" className="space-y-5">
              <Field
                label="后台核对周期（分钟）"
                hint="定期检查游戏目录是否还在原位，并提示磁盘上新出现的游戏。"
              >
                <Input
                  type="number"
                  min={1}
                  max={10080}
                  value={settings.scanIntervalMinutes}
                  disabled={busy}
                  onChange={(event) => void patch({ scanIntervalMinutes: Number(event.target.value) })}
                  aria-label="后台核对周期（分钟）"
                />
              </Field>

              <Toggle
                label="开机启动"
                hint="在 Windows 启动文件夹放置快捷方式（不写注册表）。"
                checked={settings.autostartEnabled}
                disabled={busy}
                onChange={(checked) => void patch({ autostartEnabled: checked })}
              />
            </TabsContent>

            <TabsContent value="storage" className="space-y-5">
              <Field
                label="封面与预览缓存目录"
                hint="留空则使用数据目录下的 cache。修改后新的缓存会写到新位置。"
              >
                <div className="flex gap-2">
                  <Input
                    value={cacheDirectory}
                    onChange={(event) => setCacheDraft(event.currentTarget.value)}
                    placeholder="默认：数据目录下的 cache"
                    aria-label="缓存目录"
                  />
                  <Button
                    variant="outline"
                    disabled={busy}
                    onClick={() =>
                      void open({ directory: true, multiple: false, title: "选择缓存目录" }).then(
                        (selected) => {
                          if (selected) setCacheDraft(String(selected));
                        },
                      )
                    }
                    aria-label="浏览缓存目录"
                  >
                    <FolderOpen size={15} />
                  </Button>
                </div>
                <div className="mt-2 flex gap-2">
                  <Button
                    size="sm"
                    variant="outline"
                    disabled={busy || cacheDirectory === (settings.cacheParentDirectory ?? "")}
                    onClick={() => void patch({ cacheParentDirectory: cacheDirectory || null })}
                  >
                    保存缓存目录
                  </Button>
                  <Button
                    size="sm"
                    variant="ghost"
                    disabled={busy}
                    onClick={() =>
                      runRaw(async () => {
                        await operation("diagnostics.cache_rebuild", {}, "diagnostics.cache_rebuild");
                      })
                    }
                  >
                    <RotateCcw size={14} />
                    重建缓存
                  </Button>
                </div>
              </Field>
            </TabsContent>

            <TabsContent value="guide" className="space-y-4 text-sm text-text-secondary">
              <GuideBlock title="怎么把游戏加进来">
                先在「游戏库目录」里注册你的游戏盘或游戏文件夹，再点侧栏的「扫描游戏库」。
                扫描出的候选会进入「待确认」，你可以逐个或批量加入、暂不处理、忽略。
                不想入库的目录可以加进「扫描过滤名单」。
              </GuideBlock>
              <GuideBlock title="怎么启动游戏">
                打开游戏详情，在「启动」分区里选中游戏的原始主程序（.exe 或 .swf）即可。
                之后点封面上的播放键或详情页的「开始游戏」。
              </GuideBlock>
              <GuideBlock title="三种翻译策略分别是什么意思">
                <ul className="mt-1 list-disc space-y-1 pl-5">
                  <li>
                    <strong className="text-text-primary">Auto</strong>
                    ：按目录约定自动判断。放在需要翻译的目录里的游戏，启动时会自动先调用翻译工具，
                    你只需要配置游戏的原始程序。
                  </li>
                  <li>
                    <strong className="text-text-primary">Required</strong>
                    ：强制必须经翻译工具启动。找不到可用的翻译配方时会明确拦住并告诉你下一步做什么，
                    不会偷偷直接启动原文。
                  </li>
                  <li>
                    <strong className="text-text-primary">NotRequired</strong>
                    ：明确不需要翻译，直接启动原文程序。
                  </li>
                </ul>
              </GuideBlock>
              <GuideBlock title="封面显示不全怎么办">
                详情页的封面按原始比例完整显示，不会被裁切。换一张图重新导入即可。
              </GuideBlock>
            </TabsContent>

            <TabsContent value="about" className="space-y-4">
              <div className="rounded-md border border-border bg-surface p-4 text-sm">
                <div className="font-semibold text-text-primary">GameLibrary</div>
                <div className="mt-1 text-text-secondary">当前版本 {appVersion}</div>
                <UpdateRow
                  info={updateInfo}
                  busy={busy}
                  onCheck={() =>
                    runRaw(async () => {
                      setUpdateInfo(await checkForUpdate());
                    })
                  }
                />
              </div>

              <div className="rounded-md border border-border bg-surface p-4">
                <div className="text-sm font-semibold text-text-primary">恢复默认设置</div>
                <p className="mt-1 text-xs text-text-secondary">
                  把外观、扫描周期、缓存目录等全部恢复为默认值。游戏与标签不受影响。
                </p>
                <Button variant="outline" className="mt-3" size="sm" disabled={busy} onClick={() => void resetSettings()}>
                  <RotateCcw size={14} />
                  恢复默认
                </Button>
              </div>

              <div className="rounded-md border border-danger/40 bg-danger/10 p-4">
                <div className="text-sm font-semibold text-danger">高级</div>
                <p className="mt-1 text-xs text-text-secondary">
                  停止后台服务。命令行与 MCP 下次调用时会自动把它重新拉起。
                </p>
                <Button variant="danger" className="mt-3" size="sm" disabled={busy} onClick={() => setConfirmStop(true)}>
                  <Power size={14} />
                  停止后台服务
                </Button>
              </div>
            </TabsContent>
          </Tabs>
        )}
      </Sheet>

      <ConfirmDialog
        open={confirmStop}
        title="停止后台服务"
        description="游戏库数据不会丢失。下次打开程序或调用命令行时会自动重新启动后台服务。"
        confirmLabel="停止"
        destructive
        onCancel={() => setConfirmStop(false)}
        onConfirm={() => void stopHost()}
      />
    </>
  );
}

/** settings.reset 后需要重新拉取并重新应用外观，由 SettingsProvider 的 reload 完成。 */

function UpdateRow({
  info,
  busy,
  onCheck,
}: {
  info: UpdateInfo | null;
  busy: boolean;
  onCheck: () => void;
}) {
  return (
    <div className="mt-3 space-y-2">
      <Button size="sm" variant="outline" disabled={busy} onClick={onCheck}>
        <RefreshCw size={14} />
        检查更新
      </Button>
      {info && (
        <p className="text-xs text-text-secondary">
          当前 {info.current} · 最新 {info.latest}
          {info.available ? (
            <>
              {" "}
              ·{" "}
              <button
                type="button"
                className="inline-flex cursor-pointer items-center gap-1 text-steam underline"
                onClick={() => void openUrl(info.url)}
              >
                <Download size={12} />
                前往下载
              </button>
            </>
          ) : (
            " · 已是最新版本"
          )}
        </p>
      )}
    </div>
  );
}

function GuideBlock({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div className="rounded-md border border-border bg-surface p-4">
      <h3 className="text-sm font-semibold text-text-primary">{title}</h3>
      <div className="mt-1 leading-relaxed">{children}</div>
    </div>
  );
}

function Field({ label, hint, children }: { label: string; hint?: string; children: React.ReactNode }) {
  return (
    <div>
      <span className="mb-2 block text-xs font-semibold text-text-primary">{label}</span>
      {children}
      {hint && <p className="mt-2 text-xs text-text-secondary">{hint}</p>}
    </div>
  );
}

function Toggle({
  label,
  hint,
  checked,
  disabled,
  onChange,
}: {
  label: string;
  hint?: string;
  checked: boolean;
  disabled?: boolean;
  onChange: (checked: boolean) => void;
}) {
  return (
    <label className="flex items-start justify-between gap-4 rounded-lg border border-border bg-surface p-4">
      <span>
        <span className="block text-sm text-text-primary">{label}</span>
        {hint && <span className="mt-1 block text-xs text-text-secondary">{hint}</span>}
      </span>
      <input
        type="checkbox"
        checked={checked}
        disabled={disabled}
        onChange={(event) => onChange(event.target.checked)}
        className="mt-1 h-5 w-5 shrink-0 accent-steam"
      />
    </label>
  );
}
