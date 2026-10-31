import { describe, expect, it, vi } from "vitest";
import { DataUrlCache } from "./assetCache";

describe("data URL cache", () => {
  it("bounds fetches and skips a queued cover after its last consumer leaves", async () => {
    const cache = new DataUrlCache(1024, 1);
    let finish!: (value: string) => void;
    const active = cache.load("active", () => new Promise(resolve => { finish = resolve; }));
    const signal = new AbortController();
    const skipped = vi.fn(async () => "old cover");
    const queued = cache.load("offscreen", skipped, signal.signal);
    const rejected = expect(queued).rejects.toMatchObject({ name: "AbortError" });
    const nextFetch = vi.fn(async () => "visible cover");
    const next = cache.load("visible", nextFetch);
    expect(nextFetch).not.toHaveBeenCalled();
    signal.abort();
    await rejected;
    finish("first");
    await active;
    await expect(next).resolves.toBe("visible cover");
    expect(skipped).not.toHaveBeenCalled();
    expect(nextFetch).toHaveBeenCalledOnce();
  });

  it("does not cancel another consumer of the same queued asset", async () => {
    const cache = new DataUrlCache(1024, 1);
    let finish!: (value: string) => void;
    const active = cache.load("active", () => new Promise(resolve => { finish = resolve; }));
    const signal = new AbortController();
    const fetch = vi.fn(async () => "shared");
    const a = cache.load("same", fetch, signal.signal);
    const b = cache.load("same", fetch);
    const rejected = expect(a).rejects.toMatchObject({ name: "AbortError" });
    signal.abort(); await rejected;
    finish("first"); await active;
    await expect(b).resolves.toBe("shared");
    expect(fetch).toHaveBeenCalledOnce();
  });

  it("invalidates queued work while preserving the original error of running work", async () => {
    const cache = new DataUrlCache(1024, 1);
    let fail!: (reason: Error) => void;
    const active = cache.load("active", () => new Promise((_resolve, reject) => { fail = reject; }));
    const oldFetch = vi.fn(async () => "old");
    const old = cache.load("queued", oldFetch);
    const activeError = expect(active).rejects.toThrow("库已切换");
    const queuedError = expect(old).rejects.toThrow("封面已变化");
    cache.clear();
    const fresh = cache.load("queued", async () => "new");
    fail(new Error("库已切换"));
    await activeError; await queuedError;
    await expect(fresh).resolves.toBe("new");
    expect(oldFetch).not.toHaveBeenCalled();
  });

  it("uses string bytes and evicts the least recently read value", async () => {
    const cache = new DataUrlCache(24);
    await cache.load("a", async () => "12345");
    await cache.load("b", async () => "12345");
    const unused = vi.fn(async () => "wrong");
    await cache.load("a", unused);
    await cache.load("c", async () => "12345");
    expect(unused).not.toHaveBeenCalled();
    const reloaded = vi.fn(async () => "12345");
    await cache.load("b", reloaded);
    expect(reloaded).toHaveBeenCalledOnce();
    expect(cache.bytes).toBeLessThanOrEqual(24);
  });
  it("merges in-flight requests and rejects late responses after invalidation", async () => {
    const cache = new DataUrlCache();
    let resolve!: (value: string) => void;
    const fetch = vi.fn(() => new Promise<string>(done => { resolve = done; }));
    const first = cache.load("cover", fetch);
    const second = cache.load("cover", fetch);
    expect(fetch).toHaveBeenCalledOnce();
    cache.clear();
    const fresh = cache.load("cover", async () => "new");
    resolve("old");
    await expect(first).rejects.toThrow("封面已变化");
    await expect(second).rejects.toThrow("封面已变化");
    await expect(fresh).resolves.toBe("new");
  });
  it("returns oversized values without retaining them", async () => {
    const cache = new DataUrlCache(10);
    const fetch = vi.fn(async () => "oversized");
    await cache.load("x", fetch);
    await cache.load("x", fetch);
    expect(fetch).toHaveBeenCalledTimes(2);
    expect(cache.bytes).toBe(0);
  });
  it("invalidates A without rejecting an unrelated in-flight B", async () => {
    const cache = new DataUrlCache();
    let resolveA!: (value: string) => void, resolveB!: (value: string) => void;
    const a = cache.load("a", () => new Promise<string>(done => { resolveA = done; }));
    const b = cache.load("b", () => new Promise<string>(done => { resolveB = done; }));
    const rejected = expect(a).rejects.toThrow("封面已变化");
    cache.clear("a");
    resolveA("old A"); resolveB("B");
    await rejected;
    await expect(b).resolves.toBe("B");
    const fetch = vi.fn(async () => "wrong");
    await expect(cache.load("b", fetch)).resolves.toBe("B");
    expect(fetch).not.toHaveBeenCalled();
    await expect(cache.load("a", async () => "new A")).resolves.toBe("new A");
    cache.clear("a");
    expect(cache.bytes).toBe(4); // UTF-16 key and value: "b" + "B".
  });
});
