import { useEffect, useState } from "react";
import { Dialog as DialogPrimitive } from "radix-ui";
import { Button } from "./button";
import { Input } from "./input";

/**
 * 替代 window.prompt：v1.1.5 用它新建标签，在 WebView2 下行为不可控且无法校验、
 * 无法与主题一致。这里用 Radix Dialog 实现同样的单输入交互。
 */
export function PromptDialog({
  open,
  title,
  label,
  placeholder,
  confirmLabel = "确定",
  initialValue = "",
  onCancel,
  onSubmit,
}: {
  open: boolean;
  title: string;
  label: string;
  placeholder?: string;
  confirmLabel?: string;
  initialValue?: string;
  onCancel: () => void;
  onSubmit: (value: string) => void;
}) {
  const [value, setValue] = useState(initialValue);

  useEffect(() => {
    if (open) setValue(initialValue);
  }, [open, initialValue]);

  if (!open) return null;
  const trimmed = value.trim();

  const submit = () => {
    if (!trimmed) return;
    onSubmit(trimmed);
  };

  return (
    <DialogPrimitive.Root open onOpenChange={(next) => { if (!next) onCancel(); }}>
      <DialogPrimitive.Portal>
        <DialogPrimitive.Overlay className="fixed inset-0 z-50 bg-black/60" />
        <DialogPrimitive.Content className="fixed top-1/2 left-1/2 z-60 w-[min(400px,92vw)] -translate-x-1/2 -translate-y-1/2 rounded-lg border border-border bg-panel p-6 shadow-2xl">
          <DialogPrimitive.Title className="text-base font-bold text-text-primary">
            {title}
          </DialogPrimitive.Title>
          <label className="mt-4 block">
            <span className="mb-2 block text-xs font-semibold text-text-secondary">{label}</span>
            <Input
              value={value}
              autoFocus
              placeholder={placeholder}
              onChange={(event) => setValue(event.currentTarget.value)}
              onKeyDown={(event) => {
                if (event.key === "Enter") submit();
              }}
            />
          </label>
          <div className="mt-6 flex justify-end gap-2">
            <DialogPrimitive.Close asChild>
              <Button variant="outline">取消</Button>
            </DialogPrimitive.Close>
            <Button disabled={!trimmed} onClick={submit}>
              {confirmLabel}
            </Button>
          </div>
        </DialogPrimitive.Content>
      </DialogPrimitive.Portal>
    </DialogPrimitive.Root>
  );
}
