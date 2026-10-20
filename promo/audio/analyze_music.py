#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""Structural analysis of promo music candidates (no playback needed).

Decodes each candidate to 22050 Hz mono via ffmpeg, then reports:
  - duration / integrated loudness proxy
  - 1-second RMS envelope, printed as a coarse ASCII sparkline
  - section boundaries found by comparing 4-second block RMS
  - estimated tempo via onset envelope autocorrelation (only when no
    authoritative BPM is available)
Deterministic: fixed params, no randomness.
"""
import json
import os
import subprocess
import sys

import numpy as np

SR = 22050
BLOCK = 4.0  # seconds per structure block


def decode(path):
    cmd = [
        "ffmpeg", "-v", "error", "-i", path,
        "-ac", "1", "-ar", str(SR), "-f", "f32le", "-",
    ]
    raw = subprocess.run(cmd, capture_output=True, check=True).stdout
    return np.frombuffer(raw, dtype=np.float32)


def db(x):
    return 20.0 * np.log10(max(float(x), 1e-9))


def envelope(x, win=0.05, hop=0.01):
    w = int(win * SR)
    h = int(hop * SR)
    n = 1 + (len(x) - w) // h
    idx = np.arange(w)[None, :] + h * np.arange(n)[:, None]
    frames = x[idx]
    return np.sqrt((frames ** 2).mean(axis=1)), h / SR, w / SR


def estimate_bpm(x):
    """Onset-envelope autocorrelation, restricted to 60-180 BPM."""
    rms, hop, _ = envelope(x, 0.02, 0.01)
    flux = np.maximum(0.0, np.diff(rms))
    flux = flux - flux.mean()
    ac = np.correlate(flux, flux, mode="full")[len(flux) - 1:]
    lo = int(60.0 / 180.0 / hop)   # 180 BPM
    hi = int(60.0 / 60.0 / hop)    # 60 BPM
    if hi <= lo or hi >= len(ac):
        return None
    seg = ac[lo:hi]
    lag = lo + int(np.argmax(seg))
    return 60.0 / (lag * hop)


def analyze(path):
    x = decode(path)
    dur = len(x) / SR
    rms, hop, win = envelope(x)
    # 1-second RMS in dBFS
    per_s = int(round(1.0 / hop))
    n_s = len(rms) // per_s
    rms_s = rms[: n_s * per_s].reshape(n_s, per_s).mean(axis=1)
    rms_db = np.array([db(v) for v in rms_s])

    # structure: block RMS over BLOCK seconds, mark changes > 3 dB
    per_b = int(round(BLOCK / hop))
    n_b = len(rms) // per_b
    blk = rms[: n_b * per_b].reshape(n_b, per_b).mean(axis=1)
    blk_db = np.array([db(v) for v in blk])
    changes = []
    for i in range(1, len(blk_db)):
        d = blk_db[i] - blk_db[i - 1]
        if abs(d) >= 3.0:
            changes.append({
                "time_s": round(i * BLOCK, 1),
                "delta_db": round(float(d), 1),
                "from_db": round(float(blk_db[i - 1]), 1),
                "to_db": round(float(blk_db[i]), 1),
            })

    peak = float(np.max(np.abs(x)))
    return {
        "file": os.path.basename(path),
        "duration_s": round(dur, 2),
        "peak_dbfs": round(db(peak), 1),
        "mean_rms_dbfs": round(float(np.mean(rms_db)), 1),
        "dynamic_range_db": round(float(np.max(rms_db) - np.min(rms_db)), 1),
        "est_bpm": None if "incompetech" in path else round(estimate_bpm(x) or 0, 1),
        "rms_1s_dbfs": [round(float(v), 1) for v in rms_db],
        "block_rms_db": [round(float(v), 1) for v in blk_db],
        "block_size_s": BLOCK,
        "structure_changes": changes,
    }


def spark(vals):
    glyphs = " .:-=+*#%@"
    lo, hi = min(vals), max(vals)
    span = max(hi - lo, 1e-6)
    return "".join(glyphs[min(len(glyphs) - 1, int((v - lo) / span * (len(glyphs) - 1)))] for v in vals)


def main(paths):
    out = []
    for p in paths:
        r = analyze(p)
        out.append(r)
        print("=" * 72)
        print(f"{r['file']}  dur={r['duration_s']}s peak={r['peak_dbfs']}dBFS "
              f"meanRMS={r['mean_rms_dbfs']}dB range={r['dynamic_range_db']}dB "
              f"estBPM={r['est_bpm']}")
        print("1s RMS: " + spark(r["rms_1s_dbfs"]))
        print("blocks: " + " ".join(f"{v:.0f}" for v in r["block_rms_db"]))
        for c in r["structure_changes"]:
            print(f"  change @ {c['time_s']:>6.1f}s  {c['delta_db']:+.1f} dB  ({c['from_db']} -> {c['to_db']})")
    dest = os.path.join(os.path.dirname(os.path.abspath(__file__)), "music-analysis.json")
    with open(dest, "w", encoding="utf-8") as f:
        json.dump(out, f, ensure_ascii=False, indent=2)
    print("\nwrote " + dest)


if __name__ == "__main__":
    main(sys.argv[1:])
