import type { HTMLAttributes } from "react";
import { cva, type VariantProps } from "class-variance-authority";
import { cn } from "../../lib/utils";

const badgeVariants = cva(
  "inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-[11px] font-medium",
  {
    variants: {
      variant: {
        default: "border-border bg-steam-soft text-text-secondary",
        steam: "border-steam/50 bg-steam-soft text-steam",
        favorite: "border-favorite/50 bg-transparent text-favorite",
        danger: "border-danger/50 bg-danger/10 text-danger",
        success: "border-success/50 bg-success/10 text-success",
      },
    },
    defaultVariants: { variant: "default" },
  },
);

export interface BadgeProps
  extends HTMLAttributes<HTMLSpanElement>,
    VariantProps<typeof badgeVariants> {}

export function Badge({ className, variant, ...props }: BadgeProps) {
  return <span className={cn(badgeVariants({ variant }), className)} {...props} />;
}
