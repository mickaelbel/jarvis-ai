# Setup gestes : venv isolé .venv-gestes (MediaPipe exige Python ≤3.12) + modèle hand_landmarker.task
import os
import subprocess
import sys
import urllib.request
import venv

BASE = os.path.dirname(os.path.abspath(__file__))
VENV = os.path.join(BASE, ".venv-gestes")
MODEL = os.path.join(VENV, "hand_landmarker.task")
MODEL_URL = "https://storage.googleapis.com/mediapipe-models/hand_landmarker/hand_landmarker/float16/latest/hand_landmarker.task"

def run(cmd):
    print("+", " ".join(cmd), flush=True)
    subprocess.check_call(cmd)

if not os.path.isdir(os.path.join(VENV, "Scripts")):
    print("Création du venv…", flush=True)
    venv.create(VENV, with_pip=True)

py = os.path.join(VENV, "Scripts", "python.exe")
run([py, "-m", "pip", "install", "--quiet", "--upgrade", "pip"])
run([py, "-m", "pip", "install", "--quiet", "mediapipe", "opencv-python", "numpy", "requests"])

if not os.path.isfile(MODEL):
    print("Téléchargement hand_landmarker.task (~8 Mo)…", flush=True)
    urllib.request.urlretrieve(MODEL_URL, MODEL)
    print("OK", flush=True)

print("\nGestes prêt. Lance le tracker avec :", flush=True)
print(f'  "{py}" "{os.path.join(BASE, "tracker.py")}"', flush=True)
