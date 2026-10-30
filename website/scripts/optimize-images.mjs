import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import sharp from "sharp";

// Keep original captures for enlargement and derive separate previews for the illustrations.
const images = [
  { name: "library", sizes: [440, 880, 1320], dimensions: [1320, 820] },
  { name: "library-dark", sizes: [440, 880, 1320], dimensions: [1320, 820] },
  { name: "tags", sizes: [440, 880, 1320], dimensions: [1320, 820] },
  { name: "tags-dark", sizes: [440, 880, 1320], dimensions: [1320, 820] },
  { name: "hero-paper-pixel", sizes: [640, 960, 1440], dimensions: [1536, 1024] },
  { name: "pixel-journey", sizes: [640, 960, 1440], dimensions: [1774, 887] },
];

for (const { name, sizes, dimensions } of images) {
  const input = new URL(`../public/assets/${name}.png`, import.meta.url);
  const bytes = await readFile(input);
  const { width, height } = await sharp(bytes).metadata();
  assert.equal(width, dimensions[0], `${name}: update dimensions when replacing the source`);
  assert.equal(height, dimensions[1], `${name}: update dimensions when replacing the source`);
  for (const size of sizes) {
    await sharp(bytes).resize({ width: size }).webp({ quality: 90, effort: 5 })
      .toFile(fileURLToPath(new URL(`../public/assets/${name}-${size}.webp`, import.meta.url)));
  }
}
console.log("Responsive screenshot and illustration previews generated.");
