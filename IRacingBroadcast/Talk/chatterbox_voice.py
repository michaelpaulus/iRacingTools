"""Voices commentary lines with Chatterbox (Resemble AI) for IRacingBroadcast.

Usage: python chatterbox_voice.py job.json

job.json: {"device": "cuda", "lines": [{"text", "out", "voice", "exaggeration", "cfg"}]}
Each line is spoken in the voice of its reference clip ("voice", a WAV of ~10 s; "" = Chatterbox's built-in voice) and written to "out" as 16-bit
mono WAV. Lines whose "out" already exists are skipped, so a stopped run carries on where it left off.
Prints "line <done>/<total>" after each line.
"""
import json
import os
import sys
import wave

import numpy as np
import torch
from chatterbox.tts import ChatterboxTTS


def write_wav(path, samples, rate):
    pcm = (np.clip(samples, -1.0, 1.0) * 32767).astype("<i2")
    tmp = path + ".part"
    with wave.open(tmp, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(pcm.tobytes())
    os.replace(tmp, path)


def main():
    with open(sys.argv[1], encoding="utf-8") as f:
        job = json.load(f)
    lines = job["lines"]
    todo = [l for l in lines if not os.path.exists(l["out"])]
    print(f"line {len(lines) - len(todo)}/{len(lines)}", flush=True)
    if not todo:
        return

    device = job.get("device", "cuda")
    if device == "cuda" and not torch.cuda.is_available():
        print("CUDA isn't available; voicing on the CPU (slow).", flush=True)
        device = "cpu"
    model = ChatterboxTTS.from_pretrained(device=device)

    # One set of conditionals per voice, prepared once and swapped in per line. "" = Chatterbox's built-in voice.
    conds = {"": model.conds}
    done = len(lines) - len(todo)
    for l in todo:
        voice = l["voice"]
        if voice not in conds:
            model.prepare_conditionals(voice, exaggeration=l["exaggeration"])
            conds[voice] = model.conds
        model.conds = conds[voice]
        with torch.inference_mode():
            wav = model.generate(l["text"], exaggeration=l["exaggeration"], cfg_weight=l["cfg"])
        conds[voice] = model.conds  # generate() swaps in new emotion settings when they change
        write_wav(l["out"], wav.squeeze(0).cpu().numpy(), model.sr)
        done += 1
        print(f"line {done}/{len(lines)}", flush=True)


if __name__ == "__main__":
    main()
