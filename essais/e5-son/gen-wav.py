#!/usr/bin/env python3
"""Essai E5 — génère le WAV de test (sans dépendance, stdlib seulement).

10 s, 44 100 Hz, stéréo, 16 bits. Sinus 440 Hz.
Canal gauche : l'amplitude change tous les 100 ms selon un escalier de 8 niveaux
(cycle 0..7, pas géométrique ~2,2 dB) : la page reconstruit la suite des niveaux
à partir du RMS et détecte une rejoue (niveau qui recule, bogue v86 #1660) ou un trou (silence).
Canal droit : amplitude constante 0,5 (continuité, premier échantillon non nul).
Usage : python -I gen-wav.py <sortie.wav> [secondes]
"""
import math
import struct
import sys
import wave

out = sys.argv[1]
seconds = float(sys.argv[2]) if len(sys.argv) > 2 else 10.0
RATE = 44100
FREQ = 440.0
BLOCK = RATE // 10  # 100 ms
LEVELS = [0.9 * (0.78 ** (7 - k)) for k in range(8)]  # 0.16 .. 0.9, croissant

n = int(RATE * seconds)
frames = bytearray()
for i in range(n):
    s = math.sin(2 * math.pi * FREQ * i / RATE)
    level = LEVELS[(i // BLOCK) % 8]
    left = int(32767 * level * s)
    right = int(32767 * 0.5 * s)
    frames += struct.pack("<hh", left, right)

with wave.open(out, "wb") as w:
    w.setnchannels(2)
    w.setsampwidth(2)
    w.setframerate(RATE)
    w.writeframes(bytes(frames))
print(f"{out}: {n} frames, {len(frames) + 44} octets, niveaux={[round(x, 3) for x in LEVELS]}")
