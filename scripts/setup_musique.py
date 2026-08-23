# Setup musique : venv isolé Python 3.12 + shazamio (le core Rust n'a pas de wheel 3.13).
# Nécessite ffmpeg accessible dans le PATH.
# Usage : py -3.12 scripts/setup_musique.py
import os
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MUSIQUE_DIR = os.path.join(ROOT, "musique")
VENV = os.path.join(MUSIQUE_DIR, ".venv-shazam")


def run(cmd):
    print("+", " ".join(cmd))
    subprocess.check_call(cmd)


def main():
    os.makedirs(MUSIQUE_DIR, exist_ok=True)
    os.makedirs(os.path.join(MUSIQUE_DIR, "captures"), exist_ok=True)
    if not os.path.exists(os.path.join(VENV, "Scripts", "python.exe")):
        run([sys.executable, "-m", "venv", VENV])
    pip = os.path.join(VENV, "Scripts", "python.exe")
    run([pip, "-m", "pip", "install", "--upgrade", "pip"])
    run([pip, "-m", "pip", "install", "shazamio"])
    try:
        subprocess.check_call(["ffmpeg", "-version"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        print("ffmpeg OK")
    except FileNotFoundError:
        print("⚠ ffmpeg introuvable dans le PATH — installe-le (winget install ffmpeg) pour shazamio.")
    print("\nMusique prête. Dans Jarvis : dis « c'est quoi cette musique ? »")


if __name__ == "__main__":
    main()
