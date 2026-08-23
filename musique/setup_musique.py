# Setup musique : venv isolé .venv-shazam (shazamio-core Rust exige Python ≤3.12)
import os
import subprocess
import venv

BASE = os.path.dirname(os.path.abspath(__file__))
VENV = os.path.join(BASE, ".venv-shazam")

def run(cmd):
    print("+", " ".join(cmd), flush=True)
    subprocess.check_call(cmd)

if not os.path.isdir(os.path.join(VENV, "Scripts")):
    print("Création du venv…", flush=True)
    venv.create(VENV, with_pip=True)

py = os.path.join(VENV, "Scripts", "python.exe")
run([py, "-m", "pip", "install", "--quiet", "--upgrade", "pip"])
run([py, "-m", "pip", "install", "--quiet", "shazamio"])

print("\nReconnaissance musicale prête. Jarvis utilisera automatiquement ce venv.", flush=True)
