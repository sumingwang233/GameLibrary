export function EmptyState({ symbol, title, message }: { symbol: string; title: string; message: string }) {
  return <div className="flex min-h-[420px] items-center justify-center rounded-lg border border-dashed border-border bg-surface/50 text-center"><div><div className="text-5xl text-steam/40">{symbol}</div><h2 className="mt-4 text-xl font-semibold text-text-primary">{title}</h2><p className="mt-2 text-sm text-text-secondary">{message}</p></div></div>;
}
