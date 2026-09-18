import type { ComponentProps } from "react";
import { Check, ChevronRight, Circle } from "lucide-react";
import { DropdownMenu as Primitive } from "radix-ui";
import { cn } from "../../lib/utils";

export const DropdownMenu = Primitive.Root;
export const DropdownMenuTrigger = Primitive.Trigger;
export const DropdownMenuGroup = Primitive.Group;
export const DropdownMenuPortal = Primitive.Portal;
export const DropdownMenuSub = Primitive.Sub;
export const DropdownMenuRadioGroup = Primitive.RadioGroup;

export function DropdownMenuContent({ className, sideOffset = 4, ...props }: ComponentProps<typeof Primitive.Content>) {
  return <Primitive.Portal><Primitive.Content sideOffset={sideOffset} className={cn("z-50 min-w-40 overflow-hidden rounded-md border border-border bg-popover p-1 text-popover-foreground shadow-xl", className)} {...props} /></Primitive.Portal>;
}

export function DropdownMenuItem({ className, inset, ...props }: ComponentProps<typeof Primitive.Item> & { inset?: boolean }) {
  return <Primitive.Item className={cn("relative flex cursor-default items-center gap-2 rounded-sm px-2 py-1.5 text-sm outline-none focus:bg-accent focus:text-accent-foreground data-[disabled]:pointer-events-none data-[disabled]:opacity-50", inset && "pl-8", className)} {...props} />;
}

export function DropdownMenuCheckboxItem({ className, children, checked, ...props }: ComponentProps<typeof Primitive.CheckboxItem>) {
  return <Primitive.CheckboxItem checked={checked} className={cn("relative flex cursor-default items-center rounded-sm py-1.5 pr-2 pl-8 text-sm outline-none focus:bg-accent", className)} {...props}><span className="absolute left-2 flex size-3.5 items-center justify-center"><Primitive.ItemIndicator><Check className="size-4" /></Primitive.ItemIndicator></span>{children}</Primitive.CheckboxItem>;
}

export function DropdownMenuRadioItem({ className, children, ...props }: ComponentProps<typeof Primitive.RadioItem>) {
  return <Primitive.RadioItem className={cn("relative flex cursor-default items-center rounded-sm py-1.5 pr-2 pl-8 text-sm outline-none focus:bg-accent", className)} {...props}><span className="absolute left-2 flex size-3.5 items-center justify-center"><Primitive.ItemIndicator><Circle className="size-2 fill-current" /></Primitive.ItemIndicator></span>{children}</Primitive.RadioItem>;
}

export function DropdownMenuLabel({ className, inset, ...props }: ComponentProps<typeof Primitive.Label> & { inset?: boolean }) {
  return <Primitive.Label className={cn("px-2 py-1.5 text-sm font-medium", inset && "pl-8", className)} {...props} />;
}

export function DropdownMenuSeparator({ className, ...props }: ComponentProps<typeof Primitive.Separator>) {
  return <Primitive.Separator className={cn("-mx-1 my-1 h-px bg-border", className)} {...props} />;
}

export function DropdownMenuSubTrigger({ className, inset, children, ...props }: ComponentProps<typeof Primitive.SubTrigger> & { inset?: boolean }) {
  return <Primitive.SubTrigger className={cn("flex cursor-default items-center rounded-sm px-2 py-1.5 text-sm outline-none focus:bg-accent", inset && "pl-8", className)} {...props}>{children}<ChevronRight className="ml-auto size-4" /></Primitive.SubTrigger>;
}

export function DropdownMenuSubContent({ className, ...props }: ComponentProps<typeof Primitive.SubContent>) {
  return <Primitive.SubContent className={cn("z-50 min-w-40 rounded-md border border-border bg-popover p-1 shadow-xl", className)} {...props} />;
}

export function DropdownMenuShortcut({ className, ...props }: ComponentProps<"span">) {
  return <span className={cn("ml-auto text-xs tracking-widest opacity-60", className)} {...props} />;
}
