import fs from "node:fs";
import path from "node:path";

const ROOT = path.resolve(import.meta.dirname, "..");
const file = path.join(ROOT, "render", "render-film.mjs");
let source = fs.readFileSync(file, "utf8");

source = source.replace(
  'const captions = args.captions !== "0" ? 1 : 0;',
  'const captions = args.captions !== "0" ? 1 : 0;\nconst theme = args.theme || "dark";',
);
source = source.replace(
  "const url = `http://127.0.0.1:${port}/promo/film/index.html?captions=${captions}`;",
  "const url = `http://127.0.0.1:${port}/promo/film/index.html?captions=${captions}&theme=${theme}`;",
);
source = source.replace(
  "console.log(`[film] ${info.duration}s @ ${fps}fps  captions=${captions}  frames=${total}`);",
  "console.log(`[film] ${info.duration}s @ ${fps}fps  captions=${captions}  theme=${theme}  frames=${total}`);",
);

fs.writeFileSync(file, source);
console.log("patched render-film.mjs");
