#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""Procedural SFX generator for the 150 s promo film.

All sounds are synthesized from scratch with numpy: no third-party samples,
no external dependencies beyond numpy. Everything here is original work
made for this project and carries no upstream licence obligations.

Fixed parameters only - running this file twice produces byte-identical WAVs.

Outputs (24-bit PCM WAV, 48 kHz, stereo, peak <= -6 dBFS):
    sfx_click_light.wav     UI light click
    sfx_confirm_up.wav      soft two-note rising confirm
    sfx_whoosh_soft.wav     gentle transition whoosh
    sfx_scroll_rustle.wav   very light typing / scrolling rustle
"""
import hashlib
import os
import wave

import numpy as np

SR = 48000
PEAK_CEIL_DBFS = -6.0
OUT_DIR = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "assets", "sfx")

RNG = np.random.default_rng(20260101)  # fixed seed -> reproducible noise beds


# ---------------------------------------------------------------- helpers
def t_axis(dur):
    return np.arange(int(round(dur * SR)), dtype=np.float64) / SR


def window(x, attack, release, curve=1.0):
    """Asymmetric fade envelope: fast attack, cosine release."""
    n = len(x)
    a = max(1, int(attack * SR))
    r = max(1, int(release * SR))
    env = np.ones(n)
    a = min(a, n)
    env[:a] = np.linspace(0.0, 1.0, a) ** curve
    r = min(r, n)
    env[n - r:] = (0.5 * (1.0 + np.cos(np.linspace(0.0, np.pi, r)))) ** curve
    return x * env


def lowpass(x, cutoff, order=2):
    """Zero-phase butterworth-ish lowpass via cascaded one-pole sections."""
    a = np.exp(-2.0 * np.pi * cutoff / SR)
    y = x.astype(np.float64).copy()
    for _ in range(order):
        out = np.empty_like(y)
        prev = 0.0
        for i in range(len(y)):
            prev = (1.0 - a) * y[i] + a * prev
            out[i] = prev
        y = out
    return y


def lowpass_fft(x, cutoff, tilt=2.0):
    """Frequency-domain lowpass with a smooth 1/f^tilt rolloff above cutoff."""
    n = len(x)
    spec = np.fft.rfft(x)
    freqs = np.fft.rfftfreq(n, 1.0 / SR)
    gain = 1.0 / (1.0 + (freqs / cutoff) ** (2.0 * tilt))
    return np.fft.irfft(spec * gain, n)


def highpass_fft(x, cutoff, slope=1.0):
    n = len(x)
    spec = np.fft.rfft(x)
    freqs = np.fft.rfftfreq(n, 1.0 / SR)
    f = np.maximum(freqs, 1.0)
    gain = 1.0 / (1.0 + (cutoff / f) ** (2.0 * slope))
    return np.fft.irfft(spec * gain, n)


def soft_clip(x, drive=1.2):
    return np.tanh(drive * x) / np.tanh(drive)


def normalize_peak(x, target_dbfs):
    peak = np.max(np.abs(x)) or 1.0
    return x * (10.0 ** (target_dbfs / 20.0) / peak)


def stereo(x, width=0.15, delay_ms=6.0):
    """Mild Haas widening with a mono-safe blend."""
    d = int(delay_ms * SR / 1000.0)
    right = np.concatenate([np.zeros(d), x[:-d]]) if d else x
    l = (1.0 - width / 2.0) * x + (width / 2.0) * right
    r = (1.0 - width / 2.0) * right + (width / 2.0) * x
    return np.stack([l, r], axis=1)


def write_wav(name, data, peak_dbfs=PEAK_CEIL_DBFS):
    mono = data if data.ndim == 1 else data.mean(axis=1)
    data = normalize_peak(mono, peak_dbfs)
    data = np.stack([data, data], axis=1) if data.ndim == 1 else data
    pcm = np.clip(data * 32767.0, -32768, 32767).astype("<i2")
    path = os.path.join(OUT_DIR, name)
    with wave.open(path, "wb") as w:
        w.setnchannels(2)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(pcm.tobytes())
    with open(path, "rb") as f:
        digest = hashlib.sha256(f.read()).hexdigest()
    peak = 20.0 * np.log10(max(float(np.max(np.abs(data))), 1e-9))
    print(f"{name:26s} {len(data)/SR:5.3f}s  {os.path.getsize(path):>7d} B  "
          f"peak {peak:5.1f} dBFS  sha256 {digest[:16]}...")
    return digest


# ---------------------------------------------------------------- sounds
def make_click():
    """Light UI click: body around 1.1 kHz plus a soft 3 kHz tick, 120 ms."""
    t = t_axis(0.12)
    body = np.sin(2 * np.pi * 1180 * t) * np.exp(-t * 48.0)
    tick = np.sin(2 * np.pi * 2950 * t) * np.exp(-t * 90.0) * 0.28
    thump = np.sin(2 * np.pi * 190 * t) * np.exp(-t * 26.0) * 0.35
    x = body + tick + thump
    x = lowpass_fft(x, 6000.0)
    return window(x, 0.001, 0.03)


def make_confirm():
    """Soft rising two-note confirm: A4 (440) -> D5 (587.33), bell-ish."""
    dur = 0.62
    t = t_axis(dur)
    x = np.zeros_like(t)

    def voice(f0, start, length, amp):
        i0 = int(start * SR)
        i1 = min(len(t), i0 + int(length * SR))
        tt = np.arange(i1 - i0) / SR
        env = np.exp(-tt * 6.0) * (1.0 - np.exp(-tt * 260.0))
        tone = (np.sin(2 * np.pi * f0 * tt) +
                0.26 * np.sin(2 * np.pi * 2.0 * f0 * tt) +
                0.09 * np.sin(2 * np.pi * 3.01 * f0 * tt))
        x[i0:i1] += amp * env * tone * 0.6

    voice(440.00, 0.00, 0.34, 1.00)
    voice(587.33, 0.11, 0.48, 0.85)
    # gentle air so it does not sound sterile
    air = lowpass_fft(RNG.standard_normal(len(t)), 4200.0) * 0.012
    x = lowpass_fft(x + window(air, 0.01, 0.05), 7000.0)
    return window(x, 0.004, 0.10)


def make_whoosh():
    """Soft band-limited noise whoosh with a rising-then-falling center."""
    dur = 1.05
    t = t_axis(dur)
    n = len(t)
    noise = RNG.standard_normal(n)

    # time-varying lowpass: sweep the cutoff by crossfading three bands
    bands = []
    for cut in (700.0, 1600.0, 3200.0):
        bands.append(lowpass_fft(noise, cut))
    pos = np.linspace(0.0, 1.0, n)                       # 0 -> 1
    env_a = np.clip(1.0 - np.abs(pos - 0.30) / 0.30, 0.0, 1.0) ** 1.5
    env_b = np.clip(1.0 - np.abs(pos - 0.62) / 0.38, 0.0, 1.0) ** 1.5
    sweep = bands[0] * (1.0 - pos) + bands[1] * pos
    sweep = sweep * (1.0 - env_b) + bands[2] * env_b
    amp = np.sin(np.pi * np.clip(pos / 0.62, 0.0, 1.0)) ** 1.4
    amp = amp * np.where(pos < 0.62, 1.0, np.exp(-(pos - 0.62) * 6.0))
    x = sweep * amp * (0.55 + 0.45 * env_a)
    x = highpass_fft(x, 260.0)
    x = lowpass_fft(x, 4200.0)                            # never harsh
    # tiny low sine to give it weight
    x += np.sin(2 * np.pi * 110 * t) * np.exp(-pos * 4.0) * 0.05
    x = soft_clip(x * 0.9, 1.1)
    return window(x, 0.02, 0.22)


def make_rustle():
    """Very light typing / scroll rustle: gated noise, soft and dark."""
    dur = 1.15
    n = int(round(dur * SR))
    x = np.zeros(n)
    # irregular but fixed micro-bursts, like keys or wheel ticks
    starts = np.cumsum(RNG.uniform(0.055, 0.135, 12))[:-1]
    for st in starts:
        i0 = int(st * SR)
        blen = int(RNG.uniform(0.018, 0.045) * SR)
        i1 = min(n, i0 + blen)
        if i1 - i0 < 8:
            continue
        seg = RNG.standard_normal(i1 - i0)
        seg = lowpass_fft(seg, float(RNG.uniform(1400.0, 3200.0)))
        seg = highpass_fft(seg, 450.0)
        tt = np.arange(i1 - i0) / SR
        seg *= np.exp(-tt * 55.0) * RNG.uniform(0.45, 1.0)
        x[i0:i1] += seg
    x = lowpass_fft(x, 5200.0)
    x = soft_clip(x * 1.4, 1.3)
    return window(x, 0.005, 0.10)


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    print(f"numpy {np.__version__}  SR={SR}  ceiling={PEAK_CEIL_DBFS} dBFS")
    write_wav("sfx_click_light.wav", make_click())
    write_wav("sfx_confirm_up.wav", make_confirm(), peak_dbfs=-7.0)
    write_wav("sfx_whoosh_soft.wav", make_whoosh())
    write_wav("sfx_scroll_rustle.wav", make_rustle(), peak_dbfs=-9.0)


if __name__ == "__main__":
    main()
