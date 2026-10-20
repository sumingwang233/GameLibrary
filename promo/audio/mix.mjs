// Deterministic audio mixer for the promo: music bed + SFX (+ optional voice),
// two-pass loudness normalisation, waveform still and a JSON report.
//
// Usage: node audio/mix.mjs audio/sample-plan.json
import { execFileSync, spawnSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";

const ROOT = path.resolve(import.meta.dirname, "..");
const FFMPEG = path.join(ROOT, "tools", "ffmpeg", "ffmpeg.exe");
const FFPROBE = path.join(ROOT, "tools", "ffmpeg", "ffprobe.exe");

if (!fs.existsSync(FFMPEG)) {
  console.error("missing tools/ffmpeg/ffmpeg.exe — see tools/ffmpeg/README.md");
  process.exit(1);
}

const planPath = path.resolve(ROOT, process.argv[2] || "audio/sample-plan.json");
const plan = JSON.parse(fs.readFileSync(planPath, "utf8"));
const outWav = path.resolve(ROOT, plan.out);
fs.mkdirSync(path.dirname(outWav), { recursive: true });

const db = (value) => `${Number(value).toFixed(2)}dB`;
const seconds = (value) => Number(value).toFixed(3);

function buildInputs() {
  const inputs = [];
  const filters = [];
  const mixLabels = [];

  if (plan.music) {
    const m = plan.music;
    const musicFile = path.resolve(ROOT, m.file);
    const start = Number(m.start || 0);
    const fadeIn = Number(m.fadeIn ?? 0.5);
    const fadeOut = Number(m.fadeOut ?? 1.5);
    const end = start + Number(plan.duration);
    inputs.push(musicFile);
    filters.push(
      `[0:a]atrim=start=${seconds(start)}:end=${seconds(end)},asetpts=PTS-STARTPTS,` +
        `aresample=48000,aformat=channel_layouts=stereo,volume=${db(m.gainDb ?? 0)},` +
        `afade=t=in:st=0:d=${seconds(fadeIn)},` +
        `afade=t=out:st=${seconds(Number(plan.duration) - fadeOut)}:d=${seconds(fadeOut)}[music]`,
    );
    mixLabels.push("[music]");
  }

  (plan.sfx || []).forEach((event, index) => {
    const file = path.resolve(ROOT, event.file);
    inputs.push(file);
    const ms = Math.round(Number(event.at) * 1000);
    const gain = Number(event.gainDb ?? 0);
    filters.push(
      `[${inputs.length - 1}:a]aresample=48000,aformat=channel_layouts=stereo,` +
        `volume=${db(gain)},adelay=${ms}|${ms}[sfx${index}]`,
    );
    mixLabels.push(`[sfx${index}]`);
  });

  if (plan.voice) {
    const v = plan.voice;
    inputs.push(path.resolve(ROOT, v.file));
    filters.push(
      `[${inputs.length - 1}:a]aresample=48000,aformat=channel_layouts=stereo,` +
        `volume=${db(v.gainDb ?? 0)}[voice]`,
    );
    mixLabels.push("[voice]");
  }

  filters.push(`${mixLabels.join("")}amix=inputs=${mixLabels.length}:normalize=0:dropout_transition=0[mixed]`);
  return { inputs, filter: filters.join(";") };
}

function ffmpeg(args) {
  return execFileSync(FFMPEG, args, { encoding: "utf8", maxBuffer: 64 * 1024 * 1024, stdio: ["ignore", "pipe", "pipe"] });
}

function ffmpegStderr(args) {
  const result = spawnSync(FFMPEG, args, { encoding: "utf8", maxBuffer: 64 * 1024 * 1024 });
  return String(result.stderr || "");
}

const { inputs, filter } = buildInputs();
const outDir = path.dirname(outWav);
const mixedTemp = path.join(outDir, path.basename(outWav, ".wav") + ".mixed.wav");

// 1) render the dry mix at native sample rate
ffmpeg([
  "-y", ...inputs.flatMap((file) => ["-i", file]), "-filter_complex", filter,
  "-map", "[mixed]", "-t", String(plan.duration), "-ar", "48000", "-ac", "2", "-c:a", "pcm_s24le", mixedTemp,
]);

// 2) measure loudness (pass 1)
const targetI = Number(plan.target?.lufs ?? -16);
const targetTP = Number(plan.target?.tp ?? -1.5);
const measureArgs = [
  "-hide_banner", "-nostats", "-i", mixedTemp, "-af",
  `loudnorm=I=${targetI}:TP=${targetTP}:LRA=11:print_format=json`, "-f", "null", "-",
];
const measureStderr = ffmpegStderr(measureArgs);
const jsonStart = measureStderr.lastIndexOf("{");
const jsonEnd = measureStderr.lastIndexOf("}");
let measured = null;
try {
  measured = JSON.parse(measureStderr.slice(jsonStart, jsonEnd + 1));
} catch {
  console.error("could not parse loudnorm measurement:\n" + measureStderr);
  process.exit(1);
}

// 3) apply measured linear normalisation
const applied =
  `loudnorm=I=${targetI}:TP=${targetTP}:LRA=11:measured_I=${measured.input_i}:` +
  `measured_TP=${measured.input_tp}:measured_LRA=${measured.input_lra}:` +
  `measured_thresh=${measured.input_thresh}:offset=${measured.target_offset}:linear=true,` +
  `aresample=48000`;
ffmpeg([
  "-y", "-i", mixedTemp, "-af", applied, "-t", String(plan.duration), "-ar", "48000", "-ac", "2", "-c:a", "pcm_s16le", outWav,
]);

// 4) objective verification on the delivered file
const probe = JSON.parse(
  execFileSync(FFPROBE, ["-v", "error", "-show_entries", "format=duration,bit_rate:stream=sample_rate,channels,codec_name", "-of", "json", outWav], { encoding: "utf8" }),
);
const loudness = ffmpegStderr(
  ["-hide_banner", "-nostats", "-i", outWav, "-filter_complex", "ebur128=peak=true", "-f", "null", "-"],
);
const summary = loudness.slice(loudness.lastIndexOf("Summary:"));
const pick = (label) => {
  const match = summary.match(new RegExp(`${label}:\\s*(-?[\\d.]+)`));
  return match ? Number(match[1]) : null;
};
const report = {
  plan: path.relative(ROOT, planPath).replaceAll("\\", "/"),
  output: path.relative(ROOT, outWav).replaceAll("\\", "/"),
  durationSeconds: Number(probe.format.duration),
  sampleRate: Number(probe.streams[0].sample_rate),
  channels: probe.streams[0].channels,
  codec: probe.streams[0].codec_name,
  target: { integratedLufs: targetI, truePeakDb: targetTP },
  measuredBefore: {
    integratedLufs: Number(measured.input_i),
    truePeakDb: Number(measured.input_tp),
    lra: Number(measured.input_lra),
  },
  delivered: {
    integratedLufs: pick("I"),
    lra: pick("LRA"),
    truePeakDb: pick("Peak"),
  },
};

// 5) waveform still for visual inspection
const wave = path.join(outDir, path.basename(outWav, ".wav") + "-wave.png");
ffmpeg(["-y", "-i", outWav, "-filter_complex", "showwavespic=s=1600x360:split_channels=0:colors=#66c0f4", "-frames:v", "1", wave]);
report.waveform = path.relative(ROOT, wave).replaceAll("\\", "/");
fs.writeFileSync(path.join(outDir, path.basename(outWav, ".wav") + "-report.json"), JSON.stringify(report, null, 2));
fs.rmSync(mixedTemp, { force: true });

console.log(JSON.stringify(report, null, 2));
