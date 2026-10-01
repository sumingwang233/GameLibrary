import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import sharp from "sharp";

// Derive responsive previews from the real captures; keep the originals for enlargement.
for (const name of ["library", "tags"]) {
  const input = new URL(`../public/assets/${name}.png`, import.meta.url);
  const bytes = await readFile(input);
  const { width, height } = await sharp(bytes).metadata();
  assert.equal(width, 1320, `${name}: update the HTML dimensions when replacing the capture`);
  assert.equal(height, 820, `${name}: update the HTML dimensions when replacing the capture`);
  for (const size of [440, 880, 1320]) {
    await sharp(bytes).resize({ width: size }).webp({ quality: 90, effort: 5 })
      .toFile(fileURLToPath(new URL(`../public/assets/${name}-${size}.webp`, import.meta.url)));
  }
}
console.log("Responsive screenshot previews generated.");
