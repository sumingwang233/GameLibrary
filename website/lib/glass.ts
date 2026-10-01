/** Shared liquid glass treatment; CSS supplies the contrast and motion fallbacks. */
export function glass(className = "") {
  return `glass bg-white/10 dark:bg-black/10 backdrop-blur-2xl border border-white/20 shadow-lg ${className}`.trim();
}
