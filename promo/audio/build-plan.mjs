// Builds the final mix plan from the shot list so every click sound lands on a real
// on-screen click instead of a hand-typed timecode.
import fs from "node:fs";
import path from "node:path";

const ROOT = path.resolve(import.meta.dirname, "..");
const shots = JSON.parse(fs.readFileSync(path.join(ROOT, "config", "shots.json"), "utf8"));
const film = JSON.parse(fs.readFileSync(path.join(ROOT, "config", "film.json"), "utf8"));
const duration = film.film.duration;

const clicks = [];
for (const chunk of shots.chunks) {
  for (const entry of chunk.cursor || []) {
    if (entry.click) clicks.push(+(chunk.t0 + entry.t).toFixed(3));
  }
}

// soft scene changes get a whoosh; keep it under the music
const transitions = [12.0, 38.0, 60.0, 78.0, 105.0, 122.0, 140.0];

const sfx = [
  ...clicks.map((at) => ({ file: "assets/sfx/sfx_click_light.wav", at, gainDb: -7 })),
  ...transitions.map((at) => ({ file: "assets/sfx/sfx_whoosh_soft.wav", at, gainDb: -15 })),
];
sfx.sort((a, b) => a.at - b.at);

const plan = {
  name: "GameLibrary-promo",
  duration,
  out: "out/audio/mix-final.wav",
  target: { lufs: -16, tp: -1.5 },
  music: {
    file: "assets/music/incompetech_Pamgaea.mp3",
    start: 10,
    gainDb: -2,
    fadeIn: 2.5,
    fadeOut: 3.5,
  },
  sfx,
};
fs.writeFileSync(path.join(ROOT, "audio", "final-plan.json"), JSON.stringify(plan, null, 2));
console.log(`[plan] ${sfx.length} sfx (${clicks.length} clicks + ${transitions.length} whooshes), duration ${duration}s`);
