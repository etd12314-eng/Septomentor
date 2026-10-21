"""Builds the VR trainer's Turkish voice clips (Assets/SeptoMentor/Resources/Voice/<key>.wav) from
tools/voice_phrases.tsv with the offline Piper TTS (pip install piper-tts; voice tr_TR-dfki-medium from
https://huggingface.co/rhasspy/piper-voices). Usage: python tools/make_voice.py path/to/tr_TR-dfki-medium.onnx"""
import os, subprocess, sys

root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
out = os.path.join(root, "Assets", "SeptoMentor", "Resources", "Voice")
os.makedirs(out, exist_ok=True)
model = sys.argv[1]
for line in open(os.path.join(root, "tools", "voice_phrases.tsv"), encoding="utf-8"):
    if not line.strip() or line.startswith("#"):
        continue
    key, text = line.rstrip("\n").split("\t", 1)
    wav = os.path.join(out, key + ".wav")
    subprocess.run([sys.executable, "-m", "piper", "-m", model, "--length-scale", "0.95", "--sentence-silence", "0.15", "-f", wav],
                   input=text.encode("utf-8"), check=True, capture_output=True)
    print(key)
