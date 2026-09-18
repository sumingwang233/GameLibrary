import { useEffect, useState } from "react";
import { Save, Settings2, X } from "lucide-react";
import { operation } from "../lib/api";
import { Button } from "./ui/button";

interface SettingsData {
  revision: number;
  autostartEnabled: boolean;
  scanIntervalMinutes: number;
  theme: string;
  closeToTray: boolean;
  uiFontScale: number;
}

export function SettingsDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const [settings, setSettings] = useState<SettingsData | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!open) return;
    void operation<SettingsData>("settings.get").then(result => setSettings(result.data)).catch(cause => setError(String(cause)));
  }, [open]);

  if (!open) return null;
  const save = async () => {
    if (!settings) return;
    setError(null);
    try {
      const { revision, ...patch } = settings;
      const result = await operation<SettingsData>("settings.update", { ...patch, idempotencyKey: `tauri-settings-${Date.now()}`, expectedRevision: revision });
      setSettings(result.data);
      onClose();
    } catch (cause) { setError(cause instanceof Error ? cause.message : "保存设置失败"); }
  };

  return <div className="fixed inset-0 z-40 flex justify-end" role="dialog" aria-modal="true" aria-label="设置">
    <button className="absolute inset-0 bg-black/60" onClick={onClose} aria-label="关闭设置" />
    <section className="relative h-full w-[min(420px,92vw)] overflow-y-auto border-l border-border bg-[#1b2838] p-6">
      <div className="mb-8 flex items-center justify-between"><div className="flex items-center gap-2"><Settings2 size={19} className="text-steam" /><h2 className="text-xl font-bold">设置</h2></div><Button variant="ghost" className="px-2" onClick={onClose} aria-label="关闭设置"><X size={18} /></Button></div>
      {!settings ? <div className="h-32 animate-pulse rounded-lg bg-surface" /> : <div className="space-y-6">
        <Field label="主题"><select value={settings.theme} onChange={event => setSettings({ ...settings, theme: event.target.value })} className="control"><option value="dark">深色</option><option value="light">浅色</option><option value="system">跟随系统</option></select></Field>
        <Field label={`界面缩放 ${Math.round(settings.uiFontScale * 100)}%`}><input type="range" min="0.85" max="1.6" step="0.05" value={settings.uiFontScale} onChange={event => setSettings({ ...settings, uiFontScale: Number(event.target.value) })} className="w-full accent-[#66c0f4]" /></Field>
        <Field label="后台核对周期"><div className="flex items-center gap-3"><input type="number" min="1" max="10080" value={settings.scanIntervalMinutes} onChange={event => setSettings({ ...settings, scanIntervalMinutes: Number(event.target.value) })} className="control" /><span className="text-sm text-text-secondary">分钟</span></div></Field>
        <Toggle label="开机启动" checked={settings.autostartEnabled} onChange={checked => setSettings({ ...settings, autostartEnabled: checked })} />
        <Toggle label="关闭窗口时保留托盘" checked={settings.closeToTray} onChange={checked => setSettings({ ...settings, closeToTray: checked })} />
        {error && <div className="rounded-md border border-danger/40 bg-danger/10 p-3 text-sm text-danger">{error}</div>}
        <Button className="w-full" onClick={() => void save()}><Save size={16} />保存设置</Button>
      </div>}
    </section>
  </div>;
}

function Field({ label, children }: { label: string; children: React.ReactNode }) { return <label className="block"><span className="mb-2 block text-xs font-semibold text-text-secondary">{label}</span>{children}</label>; }
function Toggle({ label, checked, onChange }: { label: string; checked: boolean; onChange: (checked: boolean) => void }) { return <label className="flex items-center justify-between rounded-lg border border-border bg-surface p-4 text-sm"><span>{label}</span><input type="checkbox" checked={checked} onChange={event => onChange(event.target.checked)} className="h-5 w-5 accent-[#66c0f4]" /></label>; }
