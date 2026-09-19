import * as React from "react";
import { Dialog as DialogPrimitive } from "radix-ui";
import { cn } from "../../lib/utils";
import { Button } from "./button";

/**
 * 居中确认框，用于破坏性操作（删除标签、移除库根、从游戏库移除）。
 * 同样基于 Radix Dialog：焦点陷阱 + Escape + aria 关联。
 */
export function ConfirmDialog({
  open,
  title,
  description,
  confirmLabel = "确认",
  cancelLabel = "取消",
  destructive = false,
  busy = false,
  onConfirm,
  onCancel,
}: {
  open: boolean;
  title: string;
  description?: string;
  confirmLabel?: string;
  cancelLabel?: string;
  destructive?: boolean;
  busy?: boolean;
  onConfirm: () => void;
  onCancel: () => void;
}) {
  const titleId = React.useId();
  const descriptionId = React.useId();

  return (
    <DialogPrimitive.Root open={open} onOpenChange={(next) => { if (!next) onCancel(); }}>
      <DialogPrimitive.Portal>
        <DialogPrimitive.Overlay className="fixed inset-0 z-50 bg-black/60" />
        <DialogPrimitive.Content
          aria-labelledby={titleId}
          aria-describedby={description ? descriptionId : undefined}
          className={cn(
            "fixed top-1/2 left-1/2 z-60 w-[min(420px,92vw)] -translate-x-1/2 -translate-y-1/2",
            "rounded-lg border border-border bg-panel p-6 shadow-2xl",
          )}
        >
          <DialogPrimitive.Title asChild>
            <h2 id={titleId} className="text-base font-bold text-text-primary">
              {title}
            </h2>
          </DialogPrimitive.Title>
          {description && (
            <DialogPrimitive.Description asChild>
              <p id={descriptionId} className="mt-2 text-sm break-words text-text-secondary">
                {description}
              </p>
            </DialogPrimitive.Description>
          )}
          <div className="mt-6 flex justify-end gap-2">
            <DialogPrimitive.Close asChild>
              <Button variant="outline" disabled={busy}>
                {cancelLabel}
              </Button>
            </DialogPrimitive.Close>
            <Button
              variant={destructive ? "danger" : "default"}
              disabled={busy}
              onClick={onConfirm}
            >
              {confirmLabel}
            </Button>
          </div>
        </DialogPrimitive.Content>
      </DialogPrimitive.Portal>
    </DialogPrimitive.Root>
  );
}
