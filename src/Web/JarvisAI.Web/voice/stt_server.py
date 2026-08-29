# Ensure CUDA DLLs are discoverable (nvidia-cublas-cu12 / nvidia-cudnn-cu12 pip packages)
# Must be done BEFORE importing faster_whisper / ctranslate2 / numpy / ANYTHING
import os
import sys

_voice_dir = os.path.dirname(__file__)  # JarvisAI.Web/voice/
_venv_site = os.path.join(_voice_dir, ".venv", "Lib", "site-packages")
_cublas_bin = os.path.join(_venv_site, "nvidia", "cublas", "bin")
_cudnn_bin = os.path.join(_venv_site, "nvidia", "cudnn", "bin")
for d in (_cublas_bin, _cudnn_bin):
    if os.path.isdir(d):
        if hasattr(os, "add_dll_directory"):
            os.add_dll_directory(d)
        os.environ["PATH"] = d + os.pathsep + os.environ.get("PATH", "")
        print(f"[stt] Added DLL directory: {d}", flush=True)

import io
import time
import traceback
import wave
import threading
import asyncio

import numpy as np
from scipy.signal import resample
from fastapi import FastAPI, Request, WebSocket, WebSocketDisconnect
from fastapi.responses import JSONResponse

MODEL_NAME = os.environ.get("WHISPER_MODEL", "small")


def _detect_device():
    """CUDA si disponible, sinon CPU — la config doit marcher sur tout PC.
    Surcharge possible via WHISPER_DEVICE / WHISPER_COMPUTE_TYPE."""
    forced = os.environ.get("WHISPER_DEVICE")
    if forced:
        compute = os.environ.get("WHISPER_COMPUTE_TYPE")
        if not compute:
            compute = "float16" if forced == "cuda" else "int8"
        return forced, compute
    try:
        import ctranslate2
        if ctranslate2.get_cuda_device_count() > 0:
            return "cuda", "float16"
    except Exception:
        pass
    return "cpu", "int8"


DEVICE, COMPUTE_TYPE = _detect_device()

_whisper_model = None
_model_lock = threading.Lock()


def get_model():
    global _whisper_model
    if _whisper_model is None:
        from faster_whisper import WhisperModel

        print(f"[stt] Loading faster-whisper model '{MODEL_NAME}' (device={DEVICE}, compute={COMPUTE_TYPE})...",
              flush=True)
        t0 = time.time()
        _whisper_model = WhisperModel(MODEL_NAME, device=DEVICE, compute_type=COMPUTE_TYPE)
        print(f"[stt] Model ready in {time.time() - t0:.1f}s", flush=True)
    return _whisper_model


def decode_audio(body: bytes, content_type: str, header_rate: int = 16000) -> np.ndarray:
    if content_type and "wav" in content_type.lower():
        with wave.open(io.BytesIO(body), "rb") as w:
            params = w.getparams()
            rate = params.framerate
            channels = params.nchannels
            width = params.sampwidth
            frames = w.readframes(w.getnframes())
        audio = np.frombuffer(frames, dtype=np.int16)
        if channels > 1:
            audio = audio.reshape(-1, channels)[:, 0]
        if width == 2:
            audio = audio.astype(np.float32) / 32768.0
        else:
            audio = audio.astype(np.float32)
    else:
        rate = header_rate if header_rate and header_rate > 0 else 16000
        audio = np.frombuffer(body, dtype=np.int16).astype(np.float32) / 32768.0

    if rate != 16000:
        target_len = int(len(audio) * 16000 / rate)
        audio = resample(audio, target_len)

    return audio, 16000


def transcribe(audio: np.ndarray, sample_rate: int):
    model = get_model()
    if audio.size == 0:
        return {"text": "", "language": None, "duration": 0.0, "segments": [], "confidence": 0.0}
    with _model_lock:
        segments, info = model.transcribe(
            audio,
            language=None,
            beam_size=1,
            vad_filter=True,
            vad_parameters={"min_silence_duration_ms": 500},
            condition_on_previous_text=False,
            initial_prompt="Voici Jarvis. Commands: ouvrir, créer, cherche, etc.",
        )
        seg_list = []
        total_prob = 0.0
        count = 0
        for seg in segments:
            seg_list.append({"text": seg.text, "start": seg.start, "end": seg.end, "avg_logprob": seg.avg_logprob})
            total_prob += seg.avg_logprob
            count += 1
        text = "".join(s["text"] for s in seg_list).strip()
        confidence = max(0.0, min(1.0, 1.0 + (total_prob / max(count, 1))))  # normalize logprob to 0-1
    return {
        "text": text,
        "language": info.language,
        "duration": round(info.duration or 0.0, 3),
        "segments": seg_list,
        "confidence": round(confidence, 3),
    }


app = FastAPI(title="Jarvis STT", docs_url=None, redoc_url=None)


@app.get("/warmup")
def warmup():
    """Force le chargement du modèle immédiatement (appelé au boot par Jarvis)
    pour que la première commande vocale ne paie pas le coût du chargement."""
    try:
        get_model()
        return {"status": "ok", "model": MODEL_NAME, "loaded": True}
    except Exception as exc:
        return {"status": "error", "error": str(exc)}


@app.get("/")
def health():
    return {
        "status": "ok",
        "model": MODEL_NAME,
        "device": DEVICE,
        "compute_type": COMPUTE_TYPE,
    }


@app.get("/health")
def health_check():
    return {"status": "ok", "model": MODEL_NAME}


@app.post("/transcribe")
async def transcribe_endpoint(request: Request):
    body = await request.body()
    if not body:
        return JSONResponse({"error": "empty body"}, status_code=400)
    content_type = request.headers.get("content-type", "")
    try:
        header_rate = int(request.headers.get("x-sample-rate", "16000") or "16000")
        audio, rate = decode_audio(body, content_type, header_rate)
        t0 = time.time()
        result = transcribe(audio, rate)
        result["elapsed_ms"] = int((time.time() - t0) * 1000)
        return result
    except Exception as exc:
        traceback.print_exc()
        return JSONResponse({"error": str(exc)}, status_code=500)


@app.websocket("/ws/transcribe")
async def ws_transcribe(websocket: WebSocket):
    """WebSocket streaming: reçoit des chunks audio WAV en continu,
    renvoie des résultats de transcription partiels en temps réel.
    Le client envoie des messages binary (WAV chunks) et reçoit
    des messages JSON: {"text": "...", "partial": true/false, "language": "..."}."""
    await websocket.accept()
    print("[stt] WebSocket client connecté", flush=True)
    buffer = bytearray()
    try:
        while True:
            msg = await websocket.receive()
            if msg["type"] == "websocket.receive":
                if "bytes" in msg and msg["bytes"]:
                    buffer.extend(msg["bytes"])
                elif "text" in msg:
                    import json
                    data = json.loads(msg["text"])
                    cmd = data.get("command", "")
                    if cmd == "flush" and len(buffer) > 0:
                        audio, rate = decode_audio(bytes(buffer), "audio/wav", 16000)
                        buffer = bytearray()
                        result = transcribe(audio, rate)
                        import json as _json
                        await websocket.send_text(_json.dumps({
                            "text": result["text"],
                            "partial": False,
                            "language": result.get("language"),
                        }))
                    elif cmd == "close":
                        break
            elif msg["type"] == "websocket.disconnect":
                break
    except WebSocketDisconnect:
        print("[stt] WebSocket client déconnecté", flush=True)
    except Exception as e:
        traceback.print_exc()
        print(f"[stt] WebSocket erreur: {e}", flush=True)


if __name__ == "__main__":
    import uvicorn
    import traceback

    port = int(os.environ.get("STT_PORT", "17001"))
    print(f"[stt] Starting STT server on http://0.0.0.0:{port}", flush=True)
    try:
        uvicorn.run(app, host="127.0.0.1", port=port, log_level="info", access_log=True)
    except Exception as e:
        traceback.print_exc()
        print(f"[stt] FATAL: {e}", flush=True)
        raise
