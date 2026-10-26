import { t } from "../../lib/i18n";
import * as React from "react";
import { X } from "lucide-react";
import { Dialog as DialogPrimitive } from "radix-ui";
import { cn } from "../../lib/utils";
import { Button } from "./button";

/**
 * 右侧滑出面板。基于 Radix Dialog，焦点陷阱、Escape 关闭与背景滚动锁都由它提供。
 * 标题/描述关联同样交给 Radix：Dialog.Title / Dialog.Description 会把自己的 id
 * 写进 context，Content 只在 count > 0 时才挂 aria-labelledby / aria-describedby。
 * 这里不能再自造 id 或手写 aria-*，否则标题上缺失的 id 会让可访问名落空。
 * 进出场动画由 data-state 驱动（keyframes 在 index.css），reduced-motion 下被关掉。
 */
export function Sheet({
  open,
  onClose,
  title,
  description,
  children,
  className,
}: {
  open: boolean;
  onClose: () => void;
  title: string;
  description?: string;
  children: React.ReactNode;
  className?: string;
}) {
  return (
    <DialogPrimitive.Root open={open} onOpenChange={(next) => { if (!next) onClose(); }}>
      <DialogPrimitive.Portal>
        <DialogPrimitive.Overlay
          data-slot="sheet-overlay"
          className="fixed inset-0 z-40 bg-black/60 data-[state=closed]:animate-[sheet-overlay-out_140ms_ease-in] data-[state=open]:animate-[sheet-overlay-in_180ms_ease-out]"
        />
        <DialogPrimitive.Content
          data-slot="sheet-content"
          className={cn(
            "fixed inset-y-0 right-0 z-50 flex w-[min(var(--sheet-width),92vw)] flex-col border-l border-border bg-panel shadow-2xl",
            "data-[state=closed]:animate-[sheet-panel-out_180ms_cubic-bezier(0.32,0.72,0,1)]",
            "data-[state=open]:animate-[sheet-panel-in_240ms_cubic-bezier(0.32,0.72,0,1)]",
            className,
          )}
        >
          <header className="flex shrink-0 items-center justify-between gap-3 border-b border-border px-6 py-4">
            <div className="min-w-0">
              <DialogPrimitive.Title asChild>
                <h2 className="truncate text-lg font-bold text-text-primary">{title}</h2>
              </DialogPrimitive.Title>
              {description && (
                <DialogPrimitive.Description asChild>
                  <p className="mt-1 truncate text-xs text-text-secondary">{description}</p>
                </DialogPrimitive.Description>
              )}
            </div>
            <DialogPrimitive.Close asChild>
              <Button variant="ghost" size="icon" aria-label={t("关闭")}>
                <X size={18} />
              </Button>
            </DialogPrimitive.Close>
          </header>
          <div className="min-h-0 flex-1 overflow-y-auto px-6 py-5">{children}</div>
        </DialogPrimitive.Content>
      </DialogPrimitive.Portal>
    </DialogPrimitive.Root>
  );
}
