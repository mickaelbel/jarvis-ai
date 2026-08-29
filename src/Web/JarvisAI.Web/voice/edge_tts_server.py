"""
Serveur TTS via Microsoft Edge TTS (gratuit, voix neurales).
Voix par défaut: fr-FR-HenriNeural (masculine, posée — style JARVIS).

Endpoints:
  POST /synthesize  { "text": "...", "voice": "fr-FR-HenriNeural", "rate": "+0%", "pitch": "+0Hz" }
                     -> audio/wav (binary)
  GET  /voices      -> liste des voix disponibles
  GET  /health      -> statut
"""

import os
import io
import asyncio
import edge_tts
from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse, StreamingResponse

app = FastAPI(title="Jarvis Edge TTS", docs_url=None, redoc_url=None)

DEFAULT_VOICE = os.environ.get("EDGE_TTS_VOICE", "fr-FR-HenriNeural")


@app.get("/health")
def health():
    return {"status": "ok", "default_voice": DEFAULT_VOICE}


@app.get("/voices")
async def list_voices():
    voices = await edge_tts.list_voices()
    fr_voices = [v for v in voices if v["Locale"].startswith("fr-")]
    return {"voices": fr_voices, "default": DEFAULT_VOICE}


@app.post("/synthesize")
async def synthesize(request: Request):
    body = await request.json()
    text = body.get("text", "")
    voice = body.get("voice", DEFAULT_VOICE)
    rate = body.get("rate", "+0%")
    pitch = body.get("pitch", "+0Hz")

    if not text.strip():
        return JSONResponse({"error": "text is required"}, status_code=400)

    try:
        communicate = edge_tts.Communicate(text, voice, rate=rate, pitch=pitch)
        audio_chunks = []
        async for chunk in communicate.stream():
            if chunk["type"] == "audio":
                audio_chunks.append(chunk["data"])

        audio_data = b"".join(audio_chunks)

        # edge-tts produit du MP3, on le convertit en WAV pour compatibilité
        # avec le player C# (NAudio). Utilise ffmpeg si disponible, sinon
        # on renvoie le MP3 directement.
        wav_path = await convert_to_wav(audio_data)
        if wav_path:
            with open(wav_path, "rb") as f:
                wav_bytes = f.read()
            os.unlink(wav_path)
            return StreamingResponse(io.BytesIO(wav_bytes), media_type="audio/wav")

        # Fallback: renvoyer le MP3
        return StreamingResponse(io.BytesIO(audio_data), media_type="audio/mpeg")

    except Exception as e:
        return JSONResponse({"error": str(e)}, status_code=500)


async def convert_to_wav(mp3_data: bytes) -> str | None:
    """Convertit MP3 en WAV via ffmpeg. Retourne le chemin du fichier WAV ou None."""
    import shutil
    import tempfile

    ffmpeg = shutil.which("ffmpeg")
    if not ffmpeg:
        # Chercher dans les chemins courants
        for path in [r"C:\ffmpeg\bin\ffmpeg.exe", r"C:\Program Files\ffmpeg\bin\ffmpeg.exe"]:
            if os.path.isfile(path):
                ffmpeg = path
                break

    if not ffmpeg:
        return None

    tmp_mp3 = tempfile.mktemp(suffix=".mp3")
    tmp_wav = tempfile.mktemp(suffix=".wav")
    try:
        with open(tmp_mp3, "wb") as f:
            f.write(mp3_data)
        proc = await asyncio.create_subprocess_exec(
            ffmpeg, "-i", tmp_mp3, "-ar", "22050", "-ac", "1", "-f", "wav", tmp_wav, "-y",
            stdout=asyncio.subprocess.DEVNULL, stderr=asyncio.subprocess.DEVNULL
        )
        await proc.wait()
        if proc.returncode == 0 and os.path.isfile(tmp_wav):
            return tmp_wav
    except Exception:
        pass
    finally:
        if os.path.isfile(tmp_mp3):
            os.unlink(tmp_mp3)
    return None


if __name__ == "__main__":
    import uvicorn
    port = int(os.environ.get("EDGE_TTS_PORT", "17004"))
    print(f"[edge-tts] Starting on http://127.0.0.1:{port} (voice={DEFAULT_VOICE})", flush=True)
    uvicorn.run(app, host="127.0.0.1", port=port, log_level="info")
