# Serveur de wake-word local (OpenWakeWord).
# Écoute sur le port 17002. Reçoit des morceaux PCM16 mono 16 kHz et maintient
# un tampon glissant ; il déclenche uniquement si un modèle de mot-clé actif
# (dossier wakeword_models/ à côté de ce fichier) dépasse le seuil.
#
# Sans modèle installé, le serveur répond disponible mais "triggered=false"
# (mode dégradé). Pour activer la détection, déposer un modèle OpenWakeWord
# (.onnx + .json d'embeddings) dans wakeword_models/.
import io
import os
import time
import traceback

import numpy as np
from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse

VOICE_DIR = os.path.dirname(__file__)
MODELS_DIR = os.path.join(VOICE_DIR, "wakeword_models")
PORT = int(os.environ.get("WAKE_WORD_PORT", "17002"))
THRESHOLD = float(os.environ.get("WAKE_WORD_THRESHOLD", "0.5"))
TARGET_SAMPLE_RATE = 16000
BUFFER_SECONDS = 5.0

_model = None
_model_error = None
_audio_buffer = np.zeros(0, dtype=np.float32)
_np_models = 0


def load_model():
    global _model, _np_models, _model_error
    if _model is not None or _model_error is not None:
        return _model

    if not os.path.isdir(MODELS_DIR):
        _model_error = f"dossier introuvable : {MODELS_DIR}"
        print(f"[wakeword] {_model_error}", flush=True)
        return None

    files = [f for f in os.listdir(MODELS_DIR) if f.endswith(".onnx")]
    if not files:
        _model_error = f"aucun modèle .onnx dans {MODELS_DIR}"
        print(f"[wakeword] {_model_error}", flush=True)
        return None

    try:
        from openwakeword import Model
        print(f"[wakeword] Chargement des modèles : {files}", flush=True)
        t0 = time.time()
        # openwakeword >= 0.6 : liste explicite de chemins (wakeword_models).
        # L'ancien wakeword_models_directory est ignoré → chargeait TOUS les
        # modèles par défaut et plantait sur les .onnx absents du package.
        # inference_framework="onnx" : tflite-runtime n'existe pas sous Windows.
        chemins = [os.path.join(MODELS_DIR, f) for f in files]
        _model = Model(wakeword_models=chemins, inference_framework="onnx")
        _np_models = len(_model.prediction_buffer)
        print(f"[wakeword] {_np_models} modèle(s) prêt(s) en {time.time() - t0:.1f}s", flush=True)
    except Exception as exc:
        _model_error = str(exc)
        traceback.print_exc()
        print(f"[wakeword] ERREUR openwakeword : {exc}", flush=True)
    return _model


app = FastAPI(title="Jarvis Wake-Word", docs_url=None, redoc_url=None)


@app.get("/")
def root():
    model = load_model()
    return {
        "status": "ok",
        "model_loaded": model is not None,
        "models": _np_models,
        "threshold": THRESHOLD,
        "error": _model_error,
    }


@app.get("/health")
def health():
    return {"status": "ok", "model_loaded": load_model() is not None}


@app.post("/reset")
async def reset():
    global _audio_buffer
    _audio_buffer = np.zeros(0, dtype=np.float32)
    if _model is not None:
        _model.reset()
    return JSONResponse({"ok": True})


@app.post("/detect")
async def detect(request: Request):
    global _audio_buffer
    body = await request.body()
    if not body:
        return JSONResponse({"error": "empty body"}, status_code=400)

    model = load_model()
    if model is None:
        return JSONResponse({"triggered": False, "score": 0.0, "clicks": 0.0, "elapsed_ms": 0})

    try:
        header_rate = int(request.headers.get("x-sample-rate", "16000") or "16000")
        audio = np.frombuffer(body, dtype=np.int16).astype(np.float32) / 32768.0
        if len(audio) == 0:
            return JSONResponse({"triggered": False, "score": 0.0, "clicks": 0.0, "elapsed_ms": 0})

        if header_rate != TARGET_SAMPLE_RATE:
            from scipy.signal import resample
            target_len = int(len(audio) * TARGET_SAMPLE_RATE / header_rate)
            audio = resample(audio, target_len)

        _audio_buffer = np.concatenate([_audio_buffer, audio])
        max_samples = int(BUFFER_SECONDS * TARGET_SAMPLE_RATE)
        if len(_audio_buffer) > max_samples:
            _audio_buffer = _audio_buffer[-max_samples:]

        t0 = time.time()
        prediction = model.predict(_audio_buffer)

        # openwakeword 0.6 : predict() renvoie {modèle: score float} (0.5.x
        # renvoyait des listes) — accepter les deux formats.
        def _score(v):
            if isinstance(v, (list, tuple)):
                return max(v) if len(v) else 0.0
            return float(v)

        max_score = max((_score(prediction[k]) for k in prediction), default=0.0)
        triggered = max_score >= THRESHOLD
        if triggered:
            model.reset()
        elapsed = int((time.time() - t0) * 1000)

        return JSONResponse({
            "triggered": triggered,
            "score": round(max_score, 4),
            "clicks": 0.0,
            "elapsed_ms": elapsed,
        })
    except Exception as exc:
        traceback.print_exc()
        return JSONResponse({"error": str(exc)}, status_code=500)


if __name__ == "__main__":
    import uvicorn

    print(f"[wakeword] Démarrage du serveur wake-word sur http://0.0.0.0:{PORT}", flush=True)
    print(f"[wakeword] Dossier de modèles : {MODELS_DIR}", flush=True)
    try:
        # 127.0.0.1 : jamais 0.0.0.0 — ce serveur traite du micro, il ne doit
        # pas être joignable depuis le réseau local.
        uvicorn.run(app, host="127.0.0.1", port=PORT, log_level="info", access_log=True)
    except Exception as e:
        traceback.print_exc()
        print(f"[wakeword] FATAL: {e}", flush=True)
        raise
