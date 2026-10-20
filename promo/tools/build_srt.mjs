import { readFile, writeFile } from 'node:fs/promises';

const copy = JSON.parse(await readFile(new URL('../config/copy.json', import.meta.url), 'utf8'));

const pad = (n, w = 2) => String(Math.floor(n)).padStart(w, '0');
const stamp = (sec) => {
  const ms = Math.round(sec * 1000);
  const h = Math.floor(ms / 3600000);
  const m = Math.floor((ms % 3600000) / 60000);
  const s = Math.floor((ms % 60000) / 1000);
  const milli = ms % 1000;
  return `${pad(h)}:${pad(m)}:${pad(s)},${String(milli).padStart(3, '0')}`;
};

const rows = copy.subtitles.map((line, i) => `${i + 1}\n${stamp(line.start)} --> ${stamp(line.end)}\n${line.text}\n`);
await writeFile(new URL('../deliverables/GameLibrary-promo.srt', import.meta.url), rows.join('\n'), 'utf8');
console.log(`wrote ${rows.length} subtitle cues`);
