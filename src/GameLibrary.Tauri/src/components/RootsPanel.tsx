import { useEffect, useState } from "react";
import { open } from "@tauri-apps/plugin-dialog";
import { Ban, FolderPlus, FolderTree, Trash2 } from "lucide-react";
import { describeFailure, operation } from "../lib/api";
import type { IgnoreRuleItem, RootItem } from "../lib/types";
import { Button } from "./ui/button";
import { ConfirmDialog } from "./ui/confirm-dialog";
import { Input } from "./ui/input";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "./ui/select";

const SCOPE_LABELS: Record<string, string> = {
  Subtree: "整个子树",
  ExactPath: "仅该路径",
  ConfirmedIdentity: "按已确认身份",
};

/**
 * 游戏库目录 + 扫描过滤名单。
 * 过滤名单是用户在 2026-09-18 明确提出的需求（「可以选择对应文件夹或者对应程序文件
 * 不作为扫描对象」）；后端 ignores.list/create/remove 早已实现，v1.1.5 完全没有入口。
 */
export function RootsPanel({
  roots,
  onAddRoot,
  onRemoveRoot,
}: {
  roots: RootItem[];
  onAddRoot: (path: string) => Promise<void>;
  onRemoveRoot: (root: RootItem) => Promise<void>;
}) {
  const [ignores, setIgnores] = useState<IgnoreRuleItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [pendingPath, setPendingPath] = useState("");
  const [pendingScope, setPendingScope] = useState("Subtree");
  const [removingRoot, setRemovingRoot] = useState<RootItem | null>(null);
  const [busy, setBusy] = useState(false);

  const loadIgnores = async () => {
    setLoading(true);
    try {
      const result = await operation<{ items: IgnoreRuleItem[] }>("ignores.list");
      setIgnores(result.data.items);
      setError(null);
    } catch (cause) {
      setError(describeFailure(cause));
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    void loadIgnores();
  }, []);

  const pickDirectory = async () => {
    const selected = await open({ directory: true, multiple: false, title: "选择要排除的目录" });
    if (selected) setPendingPath(String(selected));
  };

  const addIgnore = async () => {
    const path = pendingPath.trim();
    if (!path) return;
    setBusy(true);
    setError(null);
    try {
      await operation(
        "ignores.create",
        { scope: pendingScope, path, reason: "用户在扫描过滤名单中手动添加" },
        `ignores.create:${pendingScope}:${path}`,
      );
      setPendingPath("");
      await loadIgnores();
    } catch (cause) {
      setError(describeFailure(cause));
    } finally {
      setBusy(false);
    }
  };

  const removeIgnore = async (rule: IgnoreRuleItem) => {
    setError(null);
    try {
      await operation(
        "ignores.remove",
        { ignoreId: rule.ignoreId, expectedRevision: rule.revision },
        `ignores.remove:${rule.ignoreId}:${rule.revision}`,
      );
      await loadIgnores();
    } catch (cause) {
      setError(describeFailure(cause));
    }
  };

  return (
    <section aria-labelledby="roots-heading" className="space-y-8">
      <header>
        <h1 id="roots-heading" className="text-2xl font-bold tracking-tight text-text-primary">
          游戏库目录
        </h1>
        <p className="mt-1 text-sm text-text-secondary">
          扫描会遍历这里列出的每一个目录；下面的过滤名单可以把不想入库的子目录排除掉。
        </p>
      </header>

      <div className="space-y-3">
        <h2 className="text-xs font-semibold tracking-[0.16em] text-text-secondary uppercase">
          已注册目录 · {roots.length}
        </h2>
        {roots.length === 0 ? (
          <p className="text-sm text-text-secondary">还没有注册任何游戏库目录。</p>
        ) : (
          <ul className="space-y-1">
            {roots.map((root) => (
              <li
                key={root.rootId}
                className="flex items-center gap-3 rounded-md border border-border bg-surface px-3 py-2"
              >
                <FolderTree size={15} aria-hidden="true" className="shrink-0 text-steam" />
                <span className="min-w-0 flex-1 truncate text-sm text-text-primary">
                  {root.path}
                </span>
                <Button
                  size="icon"
                  variant="ghost"
                  aria-label={`移除目录 ${root.path}`}
                  onClick={() => setRemovingRoot(root)}
                >
                  <Trash2 size={14} />
                </Button>
              </li>
            ))}
          </ul>
        )}
        <div>
          <Button
            variant="outline"
            onClick={() =>
              void open({ directory: true, multiple: false, title: "选择游戏库目录" }).then(
                (selected) => {
                  if (selected) void onAddRoot(String(selected));
                },
              )
            }
          >
            <FolderPlus size={16} />
            添加目录
          </Button>
        </div>
      </div>

      <div className="space-y-3">
        <h2 className="text-xs font-semibold tracking-[0.16em] text-text-secondary uppercase">
          扫描过滤名单 · {ignores.length}
        </h2>

        <div className="flex flex-wrap items-center gap-2">
          <Input
            value={pendingPath}
            onChange={(event) => setPendingPath(event.currentTarget.value)}
            placeholder="要排除的目录或程序文件路径"
            aria-label="要排除的路径"
            className="min-w-[280px] flex-1"
          />
          <Select value={pendingScope} onValueChange={setPendingScope}>
            <SelectTrigger className="w-[150px]" aria-label="排除范围">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {Object.entries(SCOPE_LABELS).map(([value, label]) => (
                <SelectItem key={value} value={value}>
                  {label}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
          <Button variant="outline" onClick={() => void pickDirectory()}>
            浏览…
          </Button>
          <Button onClick={() => void addIgnore()} disabled={!pendingPath.trim() || busy}>
            <Ban size={16} />
            加入名单
          </Button>
        </div>

        {error && (
          <p role="alert" className="rounded-md border border-danger/40 bg-danger/10 p-3 text-sm text-danger">
            {error}
          </p>
        )}

        {loading ? (
          <p className="text-sm text-text-secondary">正在读取过滤名单…</p>
        ) : ignores.length === 0 ? (
          <p className="text-sm text-text-secondary">
            名单为空。把不想入库的目录（例如攻略、素材、工具集）加进来，扫描会跳过它们。
          </p>
        ) : (
          <ul className="space-y-1">
            {ignores.map((rule) => (
              <li
                key={rule.ignoreId}
                className="flex items-center gap-3 rounded-md border border-border bg-surface px-3 py-2"
              >
                <Ban size={14} aria-hidden="true" className="shrink-0 text-text-secondary" />
                <span className="shrink-0 text-xs text-text-secondary">
                  {SCOPE_LABELS[rule.scope] ?? rule.scope}
                </span>
                <span className="min-w-0 flex-1 truncate text-sm text-text-primary">
                  {rule.path ?? rule.gameId ?? "—"}
                </span>
                <Button
                  size="icon"
                  variant="ghost"
                  aria-label="从名单移除"
                  onClick={() => void removeIgnore(rule)}
                >
                  <Trash2 size={14} />
                </Button>
              </li>
            ))}
          </ul>
        )}
      </div>

      <ConfirmDialog
        open={removingRoot !== null}
        title="移除游戏库目录"
        description={
          removingRoot
            ? `将不再扫描 ${removingRoot.path}。已入库的游戏不会被删除，但会失去与该目录的关联。`
            : ""
        }
        confirmLabel="移除目录"
        destructive
        onCancel={() => setRemovingRoot(null)}
        onConfirm={() => {
          const target = removingRoot;
          setRemovingRoot(null);
          if (target) void onRemoveRoot(target);
        }}
      />
    </section>
  );
}
