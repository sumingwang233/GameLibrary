import type { ButtonHTMLAttributes } from "react";
import { cva, type VariantProps } from "class-variance-authority";
import { cn } from "../../lib/utils";

const buttonVariants = cva(
  "inline-flex shrink-0 cursor-pointer items-center justify-center gap-2 rounded-md font-semibold whitespace-nowrap transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-1 focus-visible:ring-offset-background disabled:pointer-events-none disabled:opacity-45",
  {
    variants: {
      variant: {
        default: "bg-primary text-primary-foreground hover:bg-primary-hover",
        ghost: "bg-transparent text-text-secondary hover:bg-accent hover:text-text-primary",
        outline:
          "border border-border bg-transparent text-text-primary hover:border-steam hover:text-steam",
        success: "bg-success text-success-foreground hover:bg-success-hover",
        danger: "bg-transparent text-danger hover:bg-danger-soft",
      },
      size: {
        sm: "min-h-8 px-2 text-xs",
        default: "min-h-9 px-3 text-sm",
        lg: "min-h-11 px-4 text-sm",
        icon: "h-8 w-8",
      },
    },
    defaultVariants: { variant: "default", size: "default" },
  },
);

export interface ButtonProps
  extends ButtonHTMLAttributes<HTMLButtonElement>,
    VariantProps<typeof buttonVariants> {}

export function Button({ className, variant, size, ...props }: ButtonProps) {
  return <button className={cn(buttonVariants({ variant, size }), className)} {...props} />;
}

export { buttonVariants };
