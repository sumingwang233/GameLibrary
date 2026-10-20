// Objective checks on the delivered MP4s. Everything here is measured output,
// not a claim that the film "looks fine" — visual judgements are recorded separately.
import fs from "node:fs";
import path from "node:path";
import { spawnSync } from "node:child_process";

const ROOT = path.resolve(import.meta.dirname, "..");
const FFMPEG = path.join(ROOT, "tools", "ffmpeg", "ffmpeg.exe");
const FFPROBE = path.join(ROOT, "tools", "ffmpeg", "ffprobe.exe");
const files = {
  subtitled: path.join(ROOT, "out", "film", "GameLibrary-promo-1080p60.mp4"),
  clean: path.join(ROOT, "out", "film", "GameLibrary-promo-1080p60-nosub.mp4"),
};

const probe = (file) =>
  JSON.parse(
    spawnSync(FFPROBE, ["-v", "error", "-show_entries", "format=duration,size,bit_rate:stream=codec_type,codec_name,width,height,r_frame_rate,sample_rate,channels,pix_fmt", "-of", "json", file], { encoding: "utf8" }).stdout,
  );

const stderrOf = (args) => String(spawnSync(FFMPEG, args, { encoding: "utf8", maxBuffer: 1 << 26 }).stderr || "");

const report = { generatedAt: new Date().toISOString(), files: {} };

for (const [name, file] of Object.entries(files)) {
  if (!fs.existsSync(file)) {
    report.files[name] = { exists: false };
    continue;
  }
  const info = probe(file);
  const stream = info.streams.find((s) => s.codec_type === "video");
  const audio = info.streams.find((s) => s.codec_type === "audio");

  // black frames: any run of >= 0.1 s with luma under the threshold
  const blackLog = stderrOf(["-hide_banner", "-nostats", "-i", file, "-vf", "blackdetect=d=0.1:pix_th=0.10", "-an", "-f", "null", "-"]);
  const blackSegments = [...blackLog.matchAll(/black_start:([\d.]+) black_end:([\d.]+)/g)].map((m) => [+m[1], +m[2]]);

  // per-frame luma average -> largest frame-to-frame jump (reported as measured, not a safety rating)
  const statsRun = spawnSync(FFMPEG, [
    "-hide_banner", "-nostats", "-i", file,
    "-vf", "signalstats,metadata=print:key=lavfi.signalstats.YAVG",
    "-an", "-f", "null", "-",
  ], { encoding: "utf8", maxBuffer: 1 << 28 });
  const statsLog = String(statsRun.stdout || "") + String(statsRun.stderr || "");
  const luma = [...statsLog.matchAll(/YAVG=([\d.]+)/g)].map((m) => +m[1]);
  let maxJump = 0;
  let maxJumpAt = 0;
  for (let i = 1; i < luma.length; i += 1) {
    const jump = Math.abs(luma[i] - luma[i - 1]);
    if (jump > maxJump) {
      maxJump = jump;
      maxJumpAt = i / 60;
    }
  }

  const loudLog = stderrOf(["-hide_banner", "-nostats", "-i", file, "-filter_complex", "ebur128=peak=true", "-f", "null", "-"]);
  const summary = loudLog.slice(loudLog.lastIndexOf("Summary:"));
  const pick = (key) => {
    const m = summary.match(new RegExp(key + ":\\s+(-?[\\d.]+)"));
    return m ? +m[1] : null;
  };

  report.files[name] = {
    exists: true,
    durationSeconds: +info.format.duration,
    sizeMB: +(info.format.size / 1048576).toFixed(1),
    bitrateMbps: +(info.format.bit_rate / 1e6).toFixed(2),
    video: { codec: stream.codec_name, width: stream.width, height: stream.height, fps: stream.r_frame_rate, pixFmt: stream.pix_fmt },
    audio: audio ? { codec: audio.codec_name, sampleRate: +audio.sample_rate, channels: audio.channels } : null,
    blackSegments,
    lumaFrames: luma.length,
    maxLumaJump: +maxJump.toFixed(2),
    maxLumaJumpAtSeconds: +maxJumpAt.toFixed(2),
    loudness: { integratedLufs: pick("I"), truePeakDb: pick("Peak"), lra: pick("LRA") },
  };
}

fs.mkdirSync(path.join(ROOT, "out", "check"), { recursive: true });
fs.writeFileSync(path.join(ROOT, "out", "check", "verify.json"), JSON.stringify(report, null, 2));
console.log(JSON.stringify(report, null, 2));
