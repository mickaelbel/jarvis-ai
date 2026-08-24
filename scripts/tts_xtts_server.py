# -*- coding: utf-8 -*-
"""
Serveur de synthèse vocale XTTS-v2 (voix clonée) pour Jarvis AI.

Installation (une fois, GPU NVIDIA recommandé) :
    pip install fastapi uvicorn python-multipart TTS
    # CUDA : installer le torch correspondant, ex:
    pip install torch --index-url https://download.pytorch.org/whl/cu121

Clonage de voix :
    placer un extrait propre de 6 à 30 secondes (mono, 22-44 kHz) dans ref.wav
    à côté de ce script, OU uploader via POST /speaker (multipart, champ "file").

Lancement :
    python tts_xtts_server.py            # port 17003
Endpoints :
    GET  /health                 -> {"ok":true,"device":"cuda","speaker":true}
    POST /synthesize             -> WAV   body JSON {"text":"...", "language":"fr"}
    POST /speaker                -> {"ok":true}  multipart file=ref.wav (clonage)
"""
import io
import os
import sys

from fastapi import FastAPI, File, UploadFile
from fastapi.responses import Response
from pydantic import BaseModel

PORT = int(os.environ.get("XTTS_PORT", "17003"))
REF_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "ref.wav")
DEFAULT_LANGUAGE = "fr"

app = FastAPI(title="Jarvis XTTS")
_tts = None
_speaker_wav = None
_device = "cpu"


def _load():
    global _tts, _speaker_wav, _device
    if _tts is not None:
        return True
    try:
        import torch
        from TTS.api import TTS
    except ImportError as e:
        print(f"[xtts] dépendances manquantes: {e}", file=sys.stderr)
        return False
    _device = "cuda" if torch.cuda.is_available() else "cpu"
    print(f"[xtts] chargement xtts_v2 sur {_device} ...", file=sys.stderr)
    _tts = TTS("tts_models/multilingual/multi-dataset/xtts_v2").to(_device)

    global _speaker_wav
    if os.path.exists(REF_PATH):
        import torchaudio  # fourni avec TTS
        wav, sr = torchaudio.load(REF_PATH)
        _speaker_wav = wav.squeeze(0).numpy()
        print(f"[xtts] voix de référence chargée ({REF_PATH})", file=sys.stderr)
    return True


class Synthese(BaseModel):
    text: str
    language: str = DEFAULT_LANGUAGE


@app.get("/health")
def health():
    ok = _tts is not None or _load()
    return {"ok": bool(ok), "device": _device, "speaker": _speaker_wav is not None}


@app.post("/speaker")
async def clone(file: UploadFile = File(...)):
    """Clonage : enregistre la voix de référence (WAV 6-30 s)."""
    data = await file.read()
    with open(REF_PATH, "wb") as f:
        f.write(data)
    global _speaker_wav
    _speaker_wav = None  # rechargée à la prochaine synthèse
    if _tts is not None:
        import torchaudio
        wav, _sr = torchaudio.load(REF_PATH)
        globals()["_speaker_wav"] = wav.squeeze(0).numpy()
    return {"ok": True}


@app.post("/synthesize")
def synthesize(body: Synthese):
    if not _load():
        return Response('{"error":"moteur non charge"}', status_code=503,
                        media_type="application/json")
    texte = (body.text or "").strip()
    if not texte:
        return Response(b"", status_code=200, media_type="audio/wav")

    import numpy as np
    # XTTS-v2 exige une voix : soit la référence clonée, soit une voix native.
    kwargs = {"language": body.language or DEFAULT_LANGUAGE}
    if _speaker_wav is not None:
        kwargs["speaker_wav"] = _speaker_wav
    else:
        kwargs["speaker"] = os.environ.get("XTTS_DEFAULT_SPEAKER", "Claribel Dervla")
    wav = _tts.tts(text=texte[:600], **kwargs)
    buf = io.BytesIO()
    import scipy.io.wavfile as wavfile
    arr = np.array(wav, dtype=np.float32)
    pcm = (np.clip(arr, -1.0, 1.0) * 32767).astype(np.int16)
    wavfile.write(buf, 24000, pcm)
    return Response(content=buf.getvalue(), media_type="audio/wav")


if __name__ == "__main__":
    import uvicorn
    _load()
    uvicorn.run(app, host="127.0.0.1", port=PORT, log_level="warning")
