# Setup gestes : venv isolé Python 3.11 + MediaPipe + modèle main (~8 Mo).
# Usage : py -3.11 scripts/setup_gestes.py   (ou python si 3.11 par défaut)
# 100 % local : aucune image ne quitte le PC, seuls les labels de geste sont envoyés à Jarvis.
import os
import subprocess
import sys
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
GESTES_DIR = os.path.join(ROOT, "gestes")
VENV = os.path.join(GESTES_DIR, ".venv-tracker")
MODEL_PATH = os.path.join(GESTES_DIR, "hand_landmarker.task")
MODEL_URL = "https://storage.googleapis.com/mediapipe-models/hand_landmarker/hand_landmarker/float16/1/hand_landmarker.task"


def run(cmd, **kw):
    print("+", " ".join(cmd))
    subprocess.check_call(cmd, **kw)


def main():
    os.makedirs(GESTES_DIR, exist_ok=True)
    if not os.path.exists(os.path.join(VENV, "Scripts", "python.exe")):
        run([sys.executable, "-m", "venv", VENV])
    pip = os.path.join(VENV, "Scripts", "python.exe")
    run([pip, "-m", "pip", "install", "--upgrade", "pip"])
    run([pip, "-m", "pip", "install", "opencv-python", "mediapipe", "requests"])
    if not os.path.exists(MODEL_PATH):
        print(f"Téléchargement du modèle main…")
        urllib.request.urlretrieve(MODEL_URL, MODEL_PATH)
        print("Modèle OK :", MODEL_PATH)
    print("\nGestes prêts. Dans Jarvis : dis « active les gestes » ou POST /api/gestes/start")


if __name__ == "__main__":
    main()
