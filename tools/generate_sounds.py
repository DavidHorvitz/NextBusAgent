"""Generate the BusPulse notification sounds.

Provenance: both sounds (arrival.wav, delay.wav) are original works
synthesized from scratch by this script (sine waves + a quiet 2nd harmonic
with an exponential-decay envelope). No third-party samples or recordings are
used. The script is deterministic and stdlib-only (wave, struct, math).

Usage (from repo root):
    python -I tools/generate_sounds.py
"""

import math
import os
import struct
import wave

# ---- Editable parameters -------------------------------------------------
SAMPLE_RATE = 44100          # Hz, 16-bit PCM mono
HARMONIC2_LEVEL = 0.15       # relative amplitude of the 2nd harmonic
ATTACK_S = 0.005             # linear attack
FADE_OUT_S = 0.015           # final fade to exactly zero
DECAY_TAU_S = 0.14           # exponential decay time constant per note

# Each sound: (output filename, peak amplitude 0..1, [(freq_hz, duration_s), ...])
SOUNDS = {
    "arrival.wav": (0.5, [(659.25, 0.34), (880.00, 0.36)]),    # E5 -> A5, ~700 ms
    "delay.wav": (0.45, [(440.00, 0.30), (349.23, 0.30)]),     # A4 -> F4, ~600 ms
}

OUT_DIR = os.path.join(
    os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
    "BusPulse", "Assets", "Sounds",
)
# --------------------------------------------------------------------------


def note_samples(freq, dur):
    n = int(round(dur * SAMPLE_RATE))
    attack_n = int(ATTACK_S * SAMPLE_RATE)
    fade_n = int(FADE_OUT_S * SAMPLE_RATE)
    out = []
    for i in range(n):
        t = i / SAMPLE_RATE
        s = math.sin(2 * math.pi * freq * t) + HARMONIC2_LEVEL * math.sin(2 * math.pi * 2 * freq * t)
        env = math.exp(-t / DECAY_TAU_S)
        if i < attack_n:
            env *= i / attack_n
        if i >= n - fade_n:
            env *= (n - 1 - i) / fade_n  # reaches exactly 0 on last sample
        out.append(s * env)
    return out


def render(notes, peak):
    buf = []
    for freq, dur in notes:
        buf.extend(note_samples(freq, dur))
    m = max(abs(x) for x in buf)
    scale = peak * 32767 / m
    return [int(round(x * scale)) for x in buf]


def write_wav(path, samples):
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SAMPLE_RATE)
        w.writeframes(struct.pack("<%dh" % len(samples), *samples))


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    for name, (peak, notes) in SOUNDS.items():
        samples = render(notes, peak)
        path = os.path.join(OUT_DIR, name)
        write_wav(path, samples)
        print("wrote", path, "%.0f ms" % (1000 * len(samples) / SAMPLE_RATE))


if __name__ == "__main__":
    main()
