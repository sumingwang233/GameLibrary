import type { HTMLAttributes } from "react";
import { cva, type VariantProps } from "class-variance-authority";
import { cn } from "../../lib/utils";

/**
 * 11px 小字按 WCAG AA 正文标准验收（≥4.5:1），颜色取 index.css 的真实令牌，
 * 括号内为「暗色主题 / 亮色主题」的实测对比度（相对亮度公式，/10 底色按
 * 叠在 panel 上的 sRGB 合成值计算）：
 * - default：text-primary / steam-soft（6.49 / 11.74），原 text-secondary 只有 3.32 / 4.31；
 * - steam：#66c0f4、#1a73b8 在 steam-soft 上分别为 4.82 / 3.94，改用 bg-background 后为 8.63 / 4.63；
 * - favorite / danger / success：亮色主题的 #a67c00、#4c8c0a 即使在白底上也只有 3.82 / 3.69，
 *   再叠加 10% 底色只会更低，所以这三个语义变体的文字改用 text-primary，彩色语义保留在
 *   边框与 10% 底色上（7.99~8.78 / 12.83~13.33）。
 */
const badgeVariants = cva(
  "inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-[11px] font-medium",
  {
    variants: {
      variant: {
        default: "border-border bg-steam-soft text-text-primary",
        steam: "border-steam/50 bg-background text-steam",
        favorite: "border-favorite/50 bg-favorite/10 text-text-primary",
        danger: "border-danger/50 bg-danger/10 text-text-primary",
        success: "border-success/50 bg-success/10 text-text-primary",
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
