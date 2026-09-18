import type { InputHTMLAttributes } from "react";
import { cn } from "../../lib/utils";

export function Input({ className, ...props }: InputHTMLAttributes<HTMLInputElement>) { return <input className={cn("h-10 w-full rounded-md border border-border bg-[#121821] px-3 text-sm text-text-primary outline-none placeholder:text-text-secondary focus:border-steam focus:ring-1 focus:ring-steam", className)} {...props} />; }
