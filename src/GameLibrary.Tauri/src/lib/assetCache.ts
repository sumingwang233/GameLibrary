import { t } from "./i18n";
/** Conservative UTF-16 string storage estimate; cached data URLs retain the public API. */
export class DataUrlCache {
  private values = new Map<string, string>();
  private pending = new Map<string, Promise<string>>();
  private generation = 0;
  bytes = 0;

  constructor(readonly budget = 64 * 1024 * 1024) {}

  clear() {
    this.generation++;
    this.values.clear();
    this.pending.clear();
    this.bytes = 0;
  }

  async load(key: string, fetch: () => Promise<string>): Promise<string> {
    const cached = this.values.get(key);
    if (cached !== undefined) {
      this.values.delete(key);
      this.values.set(key, cached);
      return cached;
    }
    const pending = this.pending.get(key);
    if (pending) return pending;
    const generation = this.generation;
    const request = fetch().then(value => {
      if (generation !== this.generation) throw new Error(t("封面已变化，请重新加载"));
      const cost = 2 * (key.length + value.length);
      if (cost <= this.budget) {
        while (this.bytes + cost > this.budget && this.values.size > 0) {
          const oldest = this.values.keys().next().value!;
          this.bytes -= 2 * (oldest.length + this.values.get(oldest)!.length);
          this.values.delete(oldest);
        }
        this.values.set(key, value);
        this.bytes += cost;
      }
      return value;
    }).finally(() => {
      if (this.pending.get(key) === request) this.pending.delete(key);
    });
    this.pending.set(key, request);
    return request;
  }
}
