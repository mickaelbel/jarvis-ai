"""
CogVideoX-2B local video generation server for JarvisAI.
Real text-to-video model: 6 seconds, 720x480, 8 FPS.
Runs on RTX 4060 Ti 16GB (~15GB VRAM, ~4 min de génération).

Usage:
    python local_video_server.py

Endpoints:
    GET  /health       → {"status": "ok"}
    POST /generate     → {"prompt": "..."}
                         Returns: {"success": true, "path": "/path/to/video.mp4"}
"""

import gc
import json
import os
import subprocess
import sys
import tempfile
import threading
import time
from http.server import HTTPServer, BaseHTTPRequestHandler

MODEL_ID = "zai-org/CogVideoX-2b"
DEVICE = "cuda"
OUTPUT_DIR = os.path.join(os.path.expanduser("~"), "Videos", "JarvisAI")

pipe = None
model_loaded = False
loading = False


def load_model():
    global pipe, model_loaded, loading
    loading = True
    try:
        print(f"[CogVideoX] Chargement du modèle {MODEL_ID} sur {DEVICE}...")
        print("[CogVideoX] ~15GB VRAM, ~4 min de génération par vidéo")
        sys.stdout.flush()

        import torch
        from diffusers import CogVideoXPipeline

        pipe = CogVideoXPipeline.from_pretrained(
            MODEL_ID,
            torch_dtype=torch.float16,
        )
        pipe.to(DEVICE)
        pipe.enable_sequential_cpu_offload()
        pipe.vae.enable_slicing()
        pipe.vae.enable_tiling()

        model_loaded = True
        print("[CogVideoX] Modèle chargé avec succès !")
        sys.stdout.flush()
    except Exception as e:
        print(f"[CogVideoX] Erreur chargement: {e}")
        sys.stdout.flush()
        import traceback
        traceback.print_exc()
    finally:
        loading = False


class GenerateHandler(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path == "/health":
            status = "loading" if loading else ("ok" if model_loaded else "not_loaded")
            resp = json.dumps({"status": status, "model": MODEL_ID})
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(resp.encode())
        else:
            self.send_response(404)
            self.end_headers()

    def do_POST(self):
        if self.path != "/generate":
            self.send_response(404)
            self.end_headers()
            return

        content_length = int(self.headers.get("Content-Length", 0))
        body = self.rfile.read(content_length)

        try:
            data = json.loads(body)
            prompt = data.get("prompt", "")
            num_steps = data.get("steps", 30)
            guidance = data.get("guidance_scale", 6.0)
        except Exception as e:
            self._respond(400, {"success": False, "error": str(e)})
            return

        if not model_loaded:
            self._respond(503, {"success": False, "error": "Modèle pas encore chargé. Réessayez dans 3-5 minutes."})
            return

        if not prompt:
            self._respond(400, {"success": False, "error": "Prompt requis"})
            return

        try:
            print(f"[CogVideoX] Génération: '{prompt[:80]}...' ({num_steps} steps)")
            sys.stdout.flush()

            import torch

            generator = torch.Generator(device="cpu").manual_seed(int(time.time()) % (2**32))

            t0 = time.time()
            result = pipe(
                prompt=prompt,
                num_frames=49,  # 6 secondes à 8 FPS
                num_inference_steps=num_steps,
                guidance_scale=guidance,
                generator=generator,
            )
            elapsed = time.time() - t0

            frames = result.frames[0]  # List of 49 PIL Images

            # Sauvegarder en MP4
            os.makedirs(OUTPUT_DIR, exist_ok=True)
            timestamp = time.strftime("%Y%m%d_%H%M%S")
            mp4_path = os.path.join(OUTPUT_DIR, f"jarvis-video-{timestamp}.mp4")

            # Sauvegarder les frames en PNG temporairement
            tmp_dir = tempfile.mkdtemp()
            for i, frame in enumerate(frames):
                frame.save(os.path.join(tmp_dir, f"frame_{i:04d}.png"))

            # Assembler avec ffmpeg
            proc = subprocess.run([
                "ffmpeg", "-y", "-framerate", "8",
                "-i", os.path.join(tmp_dir, "frame_%04d.png"),
                "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2",
                "-c:v", "libx264", "-pix_fmt", "yuv420p",
                "-preset", "fast",
                mp4_path
            ], capture_output=True, timeout=120)

            # Nettoyer
            import shutil
            shutil.rmtree(tmp_dir, ignore_errors=True)

            if proc.returncode != 0 or not os.path.exists(mp4_path):
                error_msg = proc.stderr.decode() if proc.stderr else "ffmpeg failed"
                self._respond(500, {"success": False, "error": f"ffmpeg: {error_msg[:200]}"})
                return

            file_size = os.path.getsize(mp4_path)
            print(f"[CogVideoX] Vidéo générée: {mp4_path} ({file_size} bytes, {elapsed:.0f}s)")
            sys.stdout.flush()

            self._respond(200, {
                "success": True,
                "path": mp4_path,
                "format": "mp4",
                "size": file_size,
                "duration": 6,
                "generation_time": round(elapsed)
            })

            gc.collect()
            if torch.cuda.is_available():
                torch.cuda.empty_cache()

        except torch.cuda.OutOfMemoryError as e:
            print(f"[CogVideoX] CUDA OOM: {e}")
            sys.stdout.flush()
            gc.collect()
            torch.cuda.empty_cache()
            self._respond(500, {"success": False, "error": "Mémoire GPU insuffisante. Fermez d'autres applications GPU."})
        except Exception as e:
            print(f"[CogVideoX] Erreur génération: {e}")
            sys.stdout.flush()
            import traceback
            traceback.print_exc()
            self._respond(500, {"success": False, "error": str(e)})

    def _respond(self, code, data):
        resp = json.dumps(data)
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(resp.encode())

    def log_message(self, format, *args):
        pass


def main():
    port = 8190
    server = HTTPServer(("127.0.0.1", port), GenerateHandler)
    print(f"[CogVideoX] Serveur démarré sur http://127.0.0.1:{port}")
    sys.stdout.flush()

    t = threading.Thread(target=load_model, daemon=True)
    t.start()

    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("[CogVideoX] Arrêt du serveur")
        server.shutdown()


if __name__ == "__main__":
    main()
