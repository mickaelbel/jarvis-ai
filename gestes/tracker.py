# Tracker de gestes main 100 % local (MediaPipe HandLandmarker + FSM anti-faux positifs).
# Aucune image ne sort : seuls les labels (« poing », « main_ouverte », « pincement_haut »…)
# sont POSTés vers Jarvis (/api/gestes, header X-Gestes-Token).
# Config passée par env GESTES_CONF (JSON) : {device, fps, largeur, hauteur, api_url, token,
#   seuils:{pincement, tenue_s, cooldown_s}, mapping:{geste: action}}
import json
import os
import sys
import time

import cv2
import mediapipe as mp
import requests

CONF = {
    "device": 0,
    "fps": 24,
    "largeur": 640,
    "hauteur": 480,
    "api_url": "http://127.0.0.1:51844/api/gestes",
    "token": "",
    "seuils": {"pincement": 0.06, "tenue_s": 1.0, "cooldown_s": 1.5},
}
if os.environ.get("GESTES_CONF"):
    CONF.update(json.loads(os.environ["GESTES_CONF"]))

SEUIL_PINCEMENT = CONF["seuils"].get("pincement", 0.06)
TENUE_S = CONF["seuils"].get("tenue_s", 1.0)
COOLDOWN_S = CONF["seuils"].get("cooldown_s", 1.5)

# Landmarks MediaPipe : poignet 0, pouce 4, index 8 (pointe), index PIP 6,
# majeur 12/10, annulaire 16/14, auriculaire 20/18, MCP 5 et 17.
TIPS = [4, 8, 12, 16, 20]
PIP = [6, 10, 14, 18]
MCP = [5, 17]


def distance(a, b):
    return ((a.x - b.x) ** 2 + (a.y - b.y) ** 2) ** 0.5


class GesteFSM:
    """Machine à états : un geste doit être TENU tenue_s puis déclenche (cooldown)."""

    def __init__(self):
        self.geste_courant = None
        self.debut = 0.0
        self.dernier_envoi = {"": 0.0}

    def update(self, geste):
        now = time.time()
        if geste != self.geste_courant:
            self.geste_courant = geste
            self.debut = now
            return None
        if geste is None:
            return None
        tenu = now - self.debut >= TENUE_S
        cooldown_ok = now - self.dernier_envoi.get(geste, 0.0) >= COOLDOWN_S
        if tenu and cooldown_ok:
            self.dernier_envoi[geste] = now
            # reset pour permettre une nouvelle tenue du même geste
            self.debut = now
            return geste
        return None


def classifier(lm):
    """Retourne le label de geste à partir des landmarks normalisés."""
    echelle = distance(lm[0], lm[MCP[0]]) or 1e-6  # taille de paume comme référence

    pincement = distance(lm[4], lm[8]) / echelle
    doigts_leves = []
    for tip, pip in zip(TIPS[1:], PIP[1:]):
        doigts_leves.append(lm[tip].y < lm[pip].y - 0.01)
    pouce_leve = abs(lm[4].x - lm[MCP[0]].x) / echelle > 0.9

    nb_leves = sum(doigts_leves)

    if pincement < SEUIL_PINCEMENT and not doigts_leves[1] and not doigts_leves[2]:
        # pincement pouce-index : hauteur de la main → haut/bas
        return "pincement_haut" if lm[8].y < lm[0].y else "pincement_bas"
    if nb_leves >= 4:
        return "main_ouverte"
    if nb_leves == 0:
        return "poing"
    if doigts_leves[1] and doigts_leves[2] and not doigts_leves[0] and not doigts_leves[3]:
        return "paix"  # victoire — swipe si mouvement latéral géré côté C#
    if pouce_leve and nb_leves == 0:
        return "pouce"
    return None


def envoyer(geste):
    try:
        requests.post(
            CONF["api_url"],
            json={"geste": geste},
            headers={"X-Gestes-Token": CONF.get("token", "")},
            timeout=2,
        )
        print(f"[gestes] → {geste}", flush=True)
    except Exception as e:
        print(f"[gestes] envoi échoué : {e}", flush=True)


def main():
    cap = cv2.VideoCapture(CONF["device"])
    cap.set(cv2.CAP_PROP_FPS, CONF["fps"])
    cap.set(cv2.CAP_PROP_FRAME_WIDTH, CONF["largeur"])
    cap.set(cv2.CAP_PROP_FRAME_HEIGHT, CONF["hauteur"])
    if not cap.isOpened():
        print("[gestes] caméra indisponible", flush=True)
        sys.exit(1)

    model_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "hand_landmarker.task")
    options = mp.tasks.BaseOptions(model_asset_path=model_path)
    detector = mp.tasks.vision.HandLandmarker.create_from_options(
        mp.tasks.vision.HandLandmarkerOptions(
            base_options=options,
            running_mode=mp.tasks.vision.RunningMode.VIDEO,
            num_hands=1,
        )
    )

    fsm = GesteFSM()
    dernier_t = time.time()
    print("[gestes] tracking démarré (Ctrl+C pour arrêter)", flush=True)
    try:
        while True:
            ok, frame = cap.read()
            if not ok:
                break
            rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
            mp_image = mp.Image(image_format=mp.ImageFormat.SRGB, data=rgb)
            t_ms = int((time.time() - dernier_t) * 1000)
            result = detector.detect_for_video(mp_image, t_ms)

            geste = None
            if result.hand_landmarks:
                lm = result.hand_landmarks[0]
                geste = classifier(lm)

            action = fsm.update(geste)
            if action:
                envoyer(action)
    except KeyboardInterrupt:
        pass
    finally:
        cap.release()
        print("[gestes] arrêté", flush=True)


if __name__ == "__main__":
    main()
