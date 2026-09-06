"""
Qwen-Image local server for JarvisAI.
Runs Qwen-Image-2.0 via diffusers on CUDA (RTX 4060 Ti 16GB).
Exposes HTTP API on http://127.0.0.1:8189.

Usage:
    python qwen_image_server.py

Endpoints:
    GET  /health       → {"status": "ok", "model": "loaded"}
    POST /generate     → {"prompt": "...", "width": 1024, "height": 1024}
                         Returns: {"image": "<base64>", "success": true}
"""

import base64
import io
import json
import sys
import threading
import time
from http.server import HTTPServer, BaseHTTPRequestHandler

MODEL_ID = "Qwen/Qwen-Image-2.0"
DEVICE = "cuda"
DTYPE = "float16"

pipe = None
model_loaded = False
loading = False


def load_model():
    global pipe, model_loaded, loading
    loading = True
    try:
        print(f"[QwenImage] Chargement du modèle {MODEL_ID} sur {DEVICE}...")
        sys.stdout.flush()

        from diffusers import DiffusionPipeline
        import torch

        pipe = DiffusionPipeline.from_pretrained(
            MODEL_ID,
            torch_dtype=getattr(torch, DTYPE),
            variant="fp16",
        )
        pipe.to(DEVICE)

        model_loaded = True
        print("[QwenImage] Modèle chargé avec succès !")
        sys.stdout.flush()
    except Exception as e:
        print(f"[QwenImage] Erreur chargement: {e}")
        sys.stdout.flush()
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
            width = data.get("width", 1024)
            height = data.get("height", 1024)
            num_steps = data.get("steps", 28)
        except Exception as e:
            self._respond(400, {"success": False, "error": str(e)})
            return

        if not model_loaded:
            self._respond(503, {"success": False, "error": "Modèle pas encore chargé"})
            return

        try:
            print(f"[QwenImage] Génération: '{prompt[:60]}...' ({width}x{height}, {num_steps} steps)")
            sys.stdout.flush()

            import torch

            generator = torch.Generator(device=DEVICE).manual_seed(int(time.time()) % (2**32))
            result = pipe(
                prompt=prompt,
                width=width,
                height=height,
                num_inference_steps=num_steps,
                generator=generator,
            )
            image = result.images[0]

            buf = io.BytesIO()
            image.save(buf, format="PNG")
            b64 = base64.b64encode(buf.getvalue()).decode("utf-8")

            print(f"[QwenImage] Image générée ({len(b64)} chars base64)")
            sys.stdout.flush()

            self._respond(200, {"success": True, "image": b64})
        except Exception as e:
            print(f"[QwenImage] Erreur génération: {e}")
            sys.stdout.flush()
            self._respond(500, {"success": False, "error": str(e)})

    def _respond(self, code, data):
        resp = json.dumps(data)
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(resp.encode())

    def log_message(self, format, *args):
        pass  # Suppress default logging


def main():
    port = 8189
    server = HTTPServer(("127.0.0.1", port), GenerateHandler)
    print(f"[QwenImage] Serveur démarré sur http://127.0.0.1:{port}")
    sys.stdout.flush()

    # Load model in background thread
    t = threading.Thread(target=load_model, daemon=True)
    t.start()

    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("[QwenImage] Arrêt du serveur")
        server.shutdown()


if __name__ == "__main__":
    main()
