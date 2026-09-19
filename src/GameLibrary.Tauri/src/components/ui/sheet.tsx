import * as React from "react";
import { X } from "lucide-react";
import { Dialog as DialogPrimitive } from "radix-ui";
import { cn } from "../../lib/utils";
import { Button } from "./button";

/**
 * 右侧滑出面板。基于 Radix Dialog，因此自带焦点陷阱、Escape 关闭、
 * aria-labelledby 关联与背景滚动锁——v1.1.5 的手写版本这三项全缺。
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
  const titleId = React.useId();
  const descriptionId = React.useId();

  return (
    <DialogPrimitive.Root open={open} onOpenChange={(next) => { if (!next) onClose(); }}>
      <DialogPrimitive.Portal>
        <DialogPrimitive.Overlay className="fixed inset-0 z-40 bg-black/60" />
        <DialogPrimitive.Content
          aria-labelledby={titleId}
          aria-describedby={description ? descriptionId : undefined}
          className={cn(
            "fixed inset-y-0 right-0 z-50 flex w-[min(var(--sheet-width),92vw)] flex-col border-l border-border bg-panel shadow-2xl",
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
                  <p id={descriptionId} className="mt-1 truncate text-xs text-text-secondary">
                    {description}
                  </p>
                </DialogPrimitive.Description>
              )}
            </div>
            <DialogPrimitive.Close asChild>
              <Button variant="ghost" size="icon" aria-label="关闭">
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
