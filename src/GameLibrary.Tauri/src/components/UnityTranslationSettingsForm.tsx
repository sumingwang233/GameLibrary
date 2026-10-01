import { useEffect, useState, type FormEvent } from "react";
import { t } from "../lib/i18n";
import { describeFailure } from "../lib/api";
import { Button } from "./ui/button";
import { Input } from "./ui/input";

export interface UnityProviderSettings {
  provider: string;
  endpoint: string;
  model: string;
  hasKey: boolean;
}

export interface UnityProviderInput {
  provider: string;
  endpoint: string;
  model: string;
  apiKey?: string;
}

const DEEPSEEK_ENDPOINT = "https://api.deepseek.com/chat/completions";

export function UnityTranslationSettingsForm({ settings, demo = false, onSave, onImport, onCancel }: {
  settings: UnityProviderSettings | null;
  demo?: boolean;
  onSave: (input: UnityProviderInput) => Promise<void>;
  onImport: () => Promise<void>;
  onCancel: () => void;
}) {
  const [provider, setProvider] = useState("deepseek");
  const [endpoint, setEndpoint] = useState(DEEPSEEK_ENDPOINT);
  const [model, setModel] = useState("deepseek-flash");
  const [apiKey, setApiKey] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [demoSaved, setDemoSaved] = useState(false);

  useEffect(() => {
    setProvider(settings?.provider ?? "deepseek");
    setEndpoint(settings?.endpoint ?? DEEPSEEK_ENDPOINT);
    setModel(settings?.model ?? "deepseek-flash");
    setApiKey(""); setError(null); setDemoSaved(false);
  }, [settings, demo]);

  const submit = async (event: FormEvent) => {
    event.preventDefault(); setError(null);
    let address: URL;
    try { address = new URL(endpoint.trim()); }
    catch { setError(t("请输入有效的翻译服务地址")); return; }
    if (address.protocol !== "https:" && !(address.protocol === "http:" && ["localhost", "127.0.0.1", "[::1]"].includes(address.hostname))) {
      setError(t("翻译服务地址须使用 HTTPS，本机服务可以使用 HTTP")); return;
    }
    let sameOrigin = false;
    try { sameOrigin = settings !== null && new URL(settings.endpoint).origin === address.origin; } catch { /* 首次配置。 */ }
    const key = apiKey.trim();
    if (!/^[A-Za-z0-9][A-Za-z0-9._:/-]{0,127}$/.test(model.trim())) { setError(t("请输入有效的模型名称")); return; }
    if (!key && (demo || !settings?.hasKey || provider !== settings.provider || !sameOrigin)) {
      setError(t("请输入 API Key；更换供应商或服务地址需要重新填写")); return;
    }
    if (/[\r\n\0]/.test(key)) { setError(t("API Key 不能包含换行")); return; }
    setBusy(true);
    try {
      if (demo) setDemoSaved(true);
      else await onSave({ provider, endpoint: endpoint.trim(), model: model.trim(), ...(key ? { apiKey: key } : {}) });
      setApiKey("");
    } catch (cause) {
      const reason = describeFailure(cause);
      setError(key ? reason.split(key).join("[redacted]") : reason);
    } finally { setBusy(false); }
  };

  const importExisting = async () => {
    setBusy(true); setError(null);
    try { await onImport(); setApiKey(""); }
    catch (cause) { setError(describeFailure(cause)); }
    finally { setBusy(false); }
  };

  return <form onSubmit={event => void submit(event)} className="space-y-4">
    <p className="text-sm text-text-secondary">{demo
      ? t("体验模式：不会保存密钥或调用翻译服务")
      : t("配置一次即可复用。密钥由 Windows 加密保存，游戏插件所需的配置仅写入本机游戏目录。")}</p>
    {error && <p role="alert" className="rounded border border-danger/40 bg-danger/10 p-3 text-sm text-danger">{error}</p>}
    {demoSaved && <p role="status" className="text-sm text-steam">{t("体验已完成，未修改主配置")}</p>}
    <label className="block space-y-1 text-sm text-text-primary">
      <span>{t("翻译供应商")}</span>
      <select value={provider} disabled={busy} onChange={event => {
        const next = event.target.value; setProvider(next); setApiKey("");
        if (next === "deepseek") { setEndpoint(DEEPSEEK_ENDPOINT); setModel("deepseek-flash"); }
        else { setEndpoint(""); setModel(""); }
      }} className="h-10 w-full rounded-md border border-input bg-field px-3">
        <option value="deepseek">DeepSeek</option>
        <option value="openai">{t("自定义 OpenAI 兼容服务")}</option>
      </select>
    </label>
    <label className="block space-y-1 text-sm text-text-primary">
      <span>{t("翻译服务地址")}</span>
      <Input value={endpoint} maxLength={2048} disabled={busy} onChange={event => setEndpoint(event.target.value)} placeholder={DEEPSEEK_ENDPOINT} autoComplete="off" />
    </label>
    <label className="block space-y-1 text-sm text-text-primary">
      <span>{t("模型名称")}</span>
      <Input value={model} maxLength={128} disabled={busy} onChange={event => setModel(event.target.value)} autoComplete="off" />
    </label>
    <label className="block space-y-1 text-sm text-text-primary">
      <span>API Key</span>
      <Input type="password" value={apiKey} maxLength={4096} disabled={busy} onChange={event => setApiKey(event.target.value)} autoComplete="new-password" spellCheck={false}
        placeholder={!demo && settings?.hasKey ? t("已保存，留空保留原密钥") : t("请输入 API Key")} />
    </label>
    <div className="flex flex-wrap justify-end gap-2">
      {demo ? <Button type="button" variant="outline" disabled={busy} onClick={() => setApiKey("demo-local-key")}>{t("填入测试密钥")}</Button>
        : <Button type="button" variant="outline" disabled={busy} onClick={() => void importExisting()}>{t("导入已有插件配置")}</Button>}
      <Button type="button" variant="outline" disabled={busy} onClick={() => { setApiKey(""); onCancel(); }}>{t("取消")}</Button>
      <Button type="submit" disabled={busy}>{busy ? t("正在保存…") : t("保存并继续")}</Button>
    </div>
  </form>;
}
