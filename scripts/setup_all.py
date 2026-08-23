# =====================================================================
#  setup_all.py — installe TOUTES les dépendances optionnelles de Jarvis
#  en une commande (idempotent : ne réinstalle pas ce qui existe déjà).
#
#    py scripts/setup_all.py
#
#  Installe / vérifie :
#    1. venv gestes   (MediaPipe + OpenCV, modèle main ~8 Mo)   [scripts/setup_gestes.py]
#    2. venv musique  (shazamio)                                 [scripts/setup_musique.py]
#    3. ffmpeg        (winget, requis par shazamio/yt-dlp)
#    4. yt-dlp        (winget, hub d'inspiration YouTube +2000 sites)
#
#  Tout est 100 % local ; seuls les téléchargements pip/winget sortent du PC.
#  Les outils restent utilisables sans ces dépendances : Jarvis annonce ce
#  qu'il manque et la commande exacte à lancer.
# =====================================================================
import os
import shutil
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def trouver_python():
    """Cherche un Python >= 3.11 via le lanceur py puis l'exécutable courant."""
    for args in (["py", "-3.12"], ["py", "-3.11"], ["py"], [sys.executable]):
        try:
            out = subprocess.check_output(args + ["-c", "import sys;print(sys.version_info[:2])"],
                                          text=True, stderr=subprocess.DEVNULL)
            majeur, mineur = eval(out.strip())
            if (majeur, mineur) >= (3, 11):
                exe = subprocess.check_output(args + ["-c", "import sys;print(sys.executable)"],
                                              text=True).strip()
                return exe
        except Exception:
            continue
    return None


def winget_installer(nom, ident):
    if shutil.which(nom):
        print(f"[ok] {nom} déjà installé")
        return True
    print(f"==> installation {nom} via winget…")
    try:
        subprocess.check_call(["winget", "install", "--id", ident, "--exact",
                               "--accept-source-agreements", "--accept-package-agreements"])
        print(f"[ok] {nom} installé")
        return True
    except Exception as e:
        print(f"[!] {nom} : installation échouée ({e})")
        return False


def yt_dlp_deja_la():
    """PATH + emplacements winget (portable, pas de shim PATH)."""
    if shutil.which("yt-dlp"):
        return True
    local = os.environ.get("LOCALAPPDATA", "")
    candidats = [
        os.path.join(local, "Microsoft", "WinGet", "Links", "yt-dlp.exe"),
    ]
    packages = os.path.join(local, "Microsoft", "WinGet", "Packages")
    if os.path.isdir(packages):
        for racine, _, fichiers in os.walk(packages):
            if "yt-dlp.exe" in fichiers:
                return True
    return any(os.path.exists(c) for c in candidats)


def main():
    python = trouver_python()
    if python is None:
        print("[!] Python 3.11+ introuvable. Installe-le avec :")
        print("    winget install Python.Python.3.12")
        return 1
    print(f"[ok] Python : {python}")

    scripts = [
        ("gestes", os.path.join(ROOT, "scripts", "setup_gestes.py")),
        ("musique", os.path.join(ROOT, "scripts", "setup_musique.py")),
    ]
    for nom, script in scripts:
        marqueur = (".venv-tracker" if nom == "gestes" else ".venv-shazam")
        base = os.path.join(ROOT, nom)
        venv_python = os.path.join(base, marqueur, "Scripts", "python.exe")
        if os.path.exists(venv_python):
            print(f"[ok] venv {nom} déjà prêt")
        else:
            print(f"==> installation des dépendances « {nom} »…")
            r = subprocess.call([python, script])
            if r != 0:
                print(f"[!] {nom} : setup échoué (code {r}) — relance plus tard :")
                print(f"    \"{python}\" \"{script}\"")

    winget_installer("ffmpeg", "Gyan.FFmpeg")
    if yt_dlp_deja_la():
        print("[ok] yt-dlp déjà installé")
    else:
        winget_installer("yt-dlp", "yt-dlp.yt-dlp")

    print("")
    print("Dépendances à jour. Redémarre Jarvis si tournait pendant l'installation.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
