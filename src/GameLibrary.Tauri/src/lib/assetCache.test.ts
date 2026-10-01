import { describe, expect, it, vi } from "vitest";
import { DataUrlCache } from "./assetCache";

describe("data URL cache", () => {
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
});
