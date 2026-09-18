import type { ButtonHTMLAttributes } from "react";
import { cn } from "../../lib/utils";

type Variant = "default" | "ghost" | "outline" | "success" | "danger";
const variants: Record<Variant, string> = {
  default: "bg-steam text-[#0d1822] hover:bg-[#8ed5f7]",
  ghost: "bg-transparent text-text-secondary hover:bg-steam-soft hover:text-text-primary",
  outline: "border border-border bg-transparent text-text-primary hover:border-steam hover:text-steam",
  success: "bg-success text-[#101a0b] hover:bg-[#c4ed2d]",
  danger: "bg-transparent text-danger hover:bg-[#522b36]",
};

export function Button({ className, variant = "default", ...props }: ButtonHTMLAttributes<HTMLButtonElement> & { variant?: Variant }) {
  return <button className={cn("inline-flex min-h-9 items-center justify-center gap-2 rounded-md px-3 text-sm font-semibold transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-steam disabled:cursor-not-allowed disabled:opacity-45", variants[variant], className)} {...props} />;
}
