import { t } from "./i18n";

interface Consumer {
  resolve: (value: string) => void;
  reject: (reason: unknown) => void;
  signal?: AbortSignal;
  abort: () => void;
}
interface FetchEntry {
  key: string;
  generation: number;
  fetch: () => Promise<string>;
  running: boolean;
  consumers: Set<Consumer>;
}

/** Bounded strings and fetches; abort only removes this consumer, not a shared IPC request. */
export class DataUrlCache {
  private values = new Map<string, string>();
  private pending = new Map<string, FetchEntry>();
  private queue: FetchEntry[] = [];
  private active = 0;
  private generation = 0;
  bytes = 0;

  constructor(readonly budget = 64 * 1024 * 1024, readonly concurrency = 2) {
    if (!Number.isInteger(concurrency) || concurrency < 1) throw new RangeError("Invalid fetch concurrency");
  }

  private changed() { return new Error(t("封面已变化，请重新加载")); }

  clear(key?: string) {
    if (key === undefined) {
      this.generation++;
      this.values.clear(); this.bytes = 0;
      for (const entry of [...this.pending.values()]) this.invalidate(entry);
      return;
    }
    const value = this.values.get(key);
    if (value !== undefined) this.bytes -= 2 * (key.length + value.length);
    this.values.delete(key);
    const entry = this.pending.get(key);
    if (entry) this.invalidate(entry);
  }

  private invalidate(entry: FetchEntry) {
    this.pending.delete(entry.key);
    // Running IPC cannot be cancelled safely. Preserve its original transport/epoch error.
    if (!entry.running) {
      this.queue = this.queue.filter(item => item !== entry);
      this.finish(entry, { error: this.changed() });
    }
  }

  load(key: string, fetch: () => Promise<string>, signal?: AbortSignal): Promise<string> {
    if (signal?.aborted) return Promise.reject(new DOMException("Aborted", "AbortError"));
    const cached = this.values.get(key);
    if (cached !== undefined) {
      this.values.delete(key); this.values.set(key, cached);
      return Promise.resolve(cached);
    }
    let entry = this.pending.get(key);
    if (!entry) {
      entry = { key, fetch, generation: this.generation, running: false, consumers: new Set() };
      this.pending.set(key, entry);
      this.queue.push(entry);
    }
    const current = entry;
    return new Promise((resolve, reject) => {
      const consumer: Consumer = { resolve, reject, signal, abort: () => {
        if (!current.consumers.delete(consumer)) return;
        signal?.removeEventListener("abort", consumer.abort);
        reject(new DOMException("Aborted", "AbortError"));
        if (!current.running && current.consumers.size === 0) {
          if (this.pending.get(key) === current) this.pending.delete(key);
          this.queue = this.queue.filter(item => item !== current);
        }
      } };
      current.consumers.add(consumer);
      signal?.addEventListener("abort", consumer.abort, { once: true });
      this.pump();
    });
  }

  private finish(entry: FetchEntry, result: { value: string } | { error: unknown }) {
    for (const consumer of entry.consumers) {
      consumer.signal?.removeEventListener("abort", consumer.abort);
      if ("error" in result) consumer.reject(result.error); else consumer.resolve(result.value);
    }
    entry.consumers.clear();
  }

  private pump() {
    while (this.active < this.concurrency && this.queue.length) {
      const entry = this.queue.shift()!;
      if (!entry.consumers.size || this.pending.get(entry.key) !== entry) continue;
      entry.running = true;
      this.active++;
      void this.run(entry);
    }
  }

  private async run(entry: FetchEntry) {
    try {
      const value = await entry.fetch();
      if (entry.generation !== this.generation || this.pending.get(entry.key) !== entry) {
        this.finish(entry, { error: this.changed() });
        return;
      }
      const cost = 2 * (entry.key.length + value.length);
      if (cost <= this.budget) {
        while (this.bytes + cost > this.budget && this.values.size) {
          const oldest = this.values.keys().next().value!;
          this.bytes -= 2 * (oldest.length + this.values.get(oldest)!.length);
          this.values.delete(oldest);
        }
        this.values.set(entry.key, value); this.bytes += cost;
      }
      this.finish(entry, { value });
    } catch (error) { this.finish(entry, { error }); }
    finally {
      if (this.pending.get(entry.key) === entry) this.pending.delete(entry.key);
      this.active--;
      this.pump();
    }
  }
}
