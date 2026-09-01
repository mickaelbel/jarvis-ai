"""
JarvisAI Blender Integration Addon
==================================
Expose une API HTTP locale pour contrôler Blender depuis JarvisAI.
Port : 7777

Fonctionnalités :
- Exécuter du code Python Blender
- Lire la scène (objets, caméras, matériaux)
- Capturer des vues depuis les caméras
- Manipuler les objets (ajouter, supprimer, modifier)
- Animer
- Exporter

Installation :
1. Copier ce fichier dans ~/.blender/scripts/addons/
2. Activer dans Edit > Preferences > Addons
3. Ou utiliser le bouton Installer de JarvisAI
"""

import bpy
import json
import threading
import time
from http.server import HTTPServer, BaseHTTPRequestHandler
from urllib.parse import urlparse, parse_qs
import sys
import traceback
import io
import os
from datetime import datetime

bl_info = {
    "name": "JarvisAI Integration",
    "author": "JarvisAI",
    "version": (1, 0, 0),
    "blender": (3, 0, 0),
    "location": "View3D > Sidebar > JarvisAI",
    "description": "API HTTP pour contrôler Blender depuis JarvisAI",
    "category": "System",
}

# Global server instance
_server = None
_server_thread = None
_port = 7777


class JarvisAIHandler(BaseHTTPRequestHandler):
    """Handler HTTP pour l'API JarvisAI."""

    def log_message(self, format, *args):
        """Silence HTTP logs."""
        pass

    def do_GET(self):
        """Traite les requêtes GET."""
        parsed = urlparse(self.path)
        path = parsed.path
        params = parse_qs(parsed.query)

        try:
            if path == "/health":
                self.send_json({"status": "ok", "blender": bpy.app.version_string})

            elif path == "/scene":
                self.send_json(self.get_scene_info())

            elif path == "/objects":
                self.send_json(self.get_objects())

            elif path == "/cameras":
                self.send_json(self.get_cameras())

            elif path == "/materials":
                self.send_json(self.get_materials())

            elif path == "/view":
                camera = params.get("camera", [None])[0]
                self.send_json(self.capture_view(camera))

            elif path == "/render":
                camera = params.get("camera", [None])[0]
                width = int(params.get("width", [800])[0])
                height = int(params.get("height", [600])[0])
                self.send_json(self.render_view(camera, width, height))

            elif path == "/eval":
                code = params.get("code", [""])[0]
                self.send_json(self.eval_code(code))

            else:
                self.send_json({"error": f"Unknown endpoint: {path}"}, 404)

        except Exception as e:
            self.send_json({"error": str(e), "traceback": traceback.format_exc()}, 500)

    def do_POST(self):
        """Traite les requêtes POST."""
        parsed = urlparse(self.path)
        path = parsed.path

        content_length = int(self.headers.get('Content-Length', 0))
        body = self.rfile.read(content_length) if content_length > 0 else b''

        try:
            data = json.loads(body) if body else {}

            if path == "/exec":
                code = data.get("code", "")
                self.send_json(self.eval_code(code))

            elif path == "/add_object":
                self.send_json(self.add_object(data))

            elif path == "/delete_object":
                name = data.get("name", "")
                self.send_json(self.delete_object(name))

            elif path == "/modify_object":
                self.send_json(self.modify_object(data))

            elif path == "/add_keyframe":
                self.send_json(self.add_keyframe(data))

            elif path == "/set_material":
                self.send_json(self.set_material(data))

            elif path == "/export":
                self.send_json(self.export_scene(data))

            elif path == "/new_scene":
                self.send_json(self.new_scene(data))

            elif path == "/save":
                self.send_json(self.save_file(data))

            else:
                self.send_json({"error": f"Unknown endpoint: {path}"}, 404)

        except Exception as e:
            self.send_json({"error": str(e), "traceback": traceback.format_exc()}, 500)

    def send_json(self, data, code=200):
        """Envoie une réponse JSON."""
        response = json.dumps(data, ensure_ascii=False, default=str)
        self.send_response(code)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Access-Control-Allow-Origin', '*')
        self.end_headers()
        self.wfile.write(response.encode('utf-8'))

    # ── Scene Info ──────────────────────────────────────────────

    def get_scene_info(self):
        """Retourne les infos de la scène."""
        scene = bpy.context.scene
        return {
            "name": scene.name,
            "objects_count": len(scene.objects),
            "camera": scene.camera.name if scene.camera else None,
            "frame_current": scene.frame_current,
            "frame_start": scene.frame_start,
            "frame_end": scene.frame_end,
            "render_engine": scene.render.engine,
            "resolution_x": scene.render.resolution_x,
            "resolution_y": scene.render.resolution_y,
            "materials_count": len(bpy.data.materials),
            "meshes_count": len(bpy.data.meshes),
            "cameras_count": len(bpy.data.cameras),
            "lights_count": len(bpy.data.lights),
        }

    def get_objects(self):
        """Liste tous les objets de la scène."""
        objects = []
        for obj in bpy.context.scene.objects:
            objects.append({
                "name": obj.name,
                "type": obj.type,
                "location": list(obj.location),
                "rotation": list(obj.rotation_euler),
                "scale": list(obj.scale),
                "visible": obj.visible_get(),
                "parent": obj.parent.name if obj.parent else None,
            })
        return {"objects": objects, "count": len(objects)}

    def get_cameras(self):
        """Liste toutes les caméras."""
        cameras = []
        for cam in bpy.data.cameras:
            cameras.append({
                "name": cam.name,
                "type": cam.type,
                "lens": cam.lens,
                "clip_start": cam.clip_start,
                "clip_end": cam.clip_end,
            })
        return {"cameras": cameras, "count": len(cameras)}

    def get_materials(self):
        """Liste tous les matériaux."""
        materials = []
        for mat in bpy.data.materials:
            materials.append({
                "name": mat.name,
                "use_nodes": mat.use_nodes,
                "diffuse_color": list(mat.diffuse_color) if hasattr(mat, 'diffuse_color') else None,
            })
        return {"materials": materials, "count": len(materials)}

    # ── Capture / Render ────────────────────────────────────────

    def capture_view(self, camera_name=None):
        """Capture la vue actuelle en base64."""
        try:
            # Rediriger vers OpenGL render
            bpy.context.scene.render.filepath = "//tmp_capture.png"
            bpy.ops.render.render(write_still=False)

            # Capturer via screeen
            bpy.ops.screen.screenshot(filepath=bpy.path.abspath("//tmp_capture.png"))

            return {"success": True, "message": "Vue capturée"}
        except Exception as e:
            return {"error": str(e)}

    def render_view(self, camera_name=None, width=800, height=600):
        """Render depuis une caméra spécifique."""
        scene = bpy.context.scene
        old_camera = scene.camera
        old_x = scene.render.resolution_x
        old_y = scene.render.resolution_y

        try:
            if camera_name and camera_name in bpy.data.objects:
                scene.camera = bpy.data.objects[camera_name]

            scene.render.resolution_x = width
            scene.render.resolution_y = height

            output_path = bpy.path.abspath(f"//jarvis_render_{int(time.time())}.png")
            scene.render.filepath = output_path
            bpy.ops.render.render(write_still=True)

            return {"success": True, "path": output_path, "width": width, "height": height}
        except Exception as e:
            return {"error": str(e)}
        finally:
            scene.camera = old_camera
            scene.render.resolution_x = old_x
            scene.render.resolution_y = old_y

    # ── Eval Code ───────────────────────────────────────────────

    def eval_code(self, code):
        """Exécute du code Python dans le contexte Blender."""
        if not code.strip():
            return {"error": "Code vide"}

        old_stdout = sys.stdout
        sys.stdout = io.StringIO()

        try:
            exec(code, {"bpy": bpy, "bpy.context": bpy.context, "bpy.data": bpy.data})
            output = sys.stdout.getvalue()
            return {"success": True, "output": output}
        except Exception as e:
            return {"error": str(e), "traceback": traceback.format_exc()}
        finally:
            sys.stdout = old_stdout

    # ── Object Manipulation ─────────────────────────────────────

    def add_object(self, data):
        """Ajoute un objet à la scène."""
        obj_type = data.get("type", "MESH")
        name = data.get("name", "JarvisAI_Object")
        location = data.get("location", [0, 0, 0])

        try:
            if obj_type == "CUBE":
                bpy.ops.mesh.primitive_cube_add(size=1, location=location)
            elif obj_type == "SPHERE":
                bpy.ops.mesh.primitive_uv_sphere_add(radius=0.5, location=location)
            elif obj_type == "CYLINDER":
                bpy.ops.mesh.primitive_cylinder_add(radius=0.5, depth=1, location=location)
            elif obj_type == "PLANE":
                bpy.ops.mesh.primitive_plane_add(size=1, location=location)
            elif obj_type == "TORUS":
                bpy.ops.mesh.primitive_torus_add(location=location)
            elif obj_type == "LIGHT":
                bpy.ops.object.light_add(type='POINT', location=location)
            elif obj_type == "CAMERA":
                bpy.ops.object.camera_add(location=location)
            else:
                return {"error": f"Type non supporté : {obj_type}"}

            obj = bpy.context.active_object
            obj.name = name
            return {"success": True, "name": obj.name, "type": obj_type}
        except Exception as e:
            return {"error": str(e)}

    def delete_object(self, name):
        """Supprime un objet par son nom."""
        try:
            if name in bpy.data.objects:
                obj = bpy.data.objects[name]
                bpy.data.objects.remove(obj, do_unlink=True)
                return {"success": True, "deleted": name}
            return {"error": f"Objet non trouvé : {name}"}
        except Exception as e:
            return {"error": str(e)}

    def modify_object(self, data):
        """Modifie les propriétés d'un objet."""
        name = data.get("name", "")
        try:
            if name not in bpy.data.objects:
                return {"error": f"Objet non trouvé : {name}"}

            obj = bpy.data.objects[name]

            if "location" in data:
                obj.location = data["location"]
            if "rotation" in data:
                obj.rotation_euler = data["rotation"]
            if "scale" in data:
                obj.scale = data["scale"]
            if "visible" in data:
                obj.hide_viewport = not data["visible"]
                obj.hide_render = not data["visible"]

            return {"success": True, "modified": name}
        except Exception as e:
            return {"error": str(e)}

    def add_keyframe(self, data):
        """Ajoute une keyframe à un objet."""
        name = data.get("name", "")
        frame = data.get("frame", bpy.context.scene.frame_current)
        property_name = data.get("property", "location")

        try:
            if name not in bpy.data.objects:
                return {"error": f"Objet non trouvé : {name}"}

            obj = bpy.data.objects[name]
            obj.keyframe_insert(data_path=property_name, frame=frame)
            return {"success": True, "object": name, "frame": frame, "property": property_name}
        except Exception as e:
            return {"error": str(e)}

    def set_material(self, data):
        """Applique un matériau à un objet."""
        name = data.get("name", "")
        mat_name = data.get("material", "")
        color = data.get("color", [1, 1, 1, 1])

        try:
            if name not in bpy.data.objects:
                return {"error": f"Objet non trouvé : {name}"}

            obj = bpy.data.objects[name]

            if mat_name and mat_name in bpy.data.materials:
                mat = bpy.data.materials[mat_name]
            else:
                mat = bpy.data.materials.new(name=f"JarvisAI_Mat_{name}")
                mat.use_nodes = True
                bsdf = mat.node_tree.nodes.get("Principled BSDF")
                if bsdf:
                    bsdf.inputs["Base Color"].default_value = color

            obj.data.materials.clear()
            obj.data.materials.append(mat)
            return {"success": True, "object": name, "material": mat.name}
        except Exception as e:
            return {"error": str(e)}

    def export_scene(self, data):
        """Exporte la scène."""
        format = data.get("format", "FBX")
        path = data.get("path", bpy.path.abspath(f"//export_{int(time.time())}.{format.lower()}"))

        try:
            if format.upper() == "FBX":
                bpy.ops.export_scene.fbx(filepath=path)
            elif format.upper() == "GLTF":
                bpy.ops.export_scene.gltf(filepath=path)
            elif format.upper() == "OBJ":
                bpy.ops.export_scene.obj(filepath=path)
            elif format.upper() == "STL":
                bpy.ops.export_mesh.stl(filepath=path)
            else:
                return {"error": f"Format non supporté : {format}"}

            return {"success": True, "path": path, "format": format}
        except Exception as e:
            return {"error": str(e)}

    def new_scene(self, data):
        """Crée une nouvelle scène."""
        clear_default = data.get("clear_default", True)

        try:
            bpy.ops.wm.read_factory_settings(use_empty=not clear_default)

            if clear_default:
                # Supprimer cube, lampe, caméra
                bpy.ops.object.select_all(action='SELECT')
                bpy.ops.object.delete()

            return {"success": True, "message": "Nouvelle scène créée"}
        except Exception as e:
            return {"error": str(e)}

    def save_file(self, data):
        """Enregistre le fichier Blender."""
        path = data.get("path", "")

        try:
            if path:
                bpy.ops.wm.save_as_mainfile(filepath=path)
            else:
                bpy.ops.wm.save_mainfile()
            return {"success": True, "path": path or bpy.data.filepath}
        except Exception as e:
            return {"error": str(e)}


def start_server():
    """Démarre le serveur HTTP."""
    global _server, _port

    try:
        _server = HTTPServer(('127.0.0.1', _port), JarvisAIHandler)
        print(f"[JarvisAI] Serveur démarré sur http://127.0.0.1:{_port}")
        _server.serve_forever()
    except OSError as e:
        if "already in use" in str(e) or "address already in use" in str(e):
            print(f"[JarvisAI] Port {_port} occupé, tentative sur {_port + 1}")
            _port += 1
            start_server()
        else:
            print(f"[JarvisAI] Erreur serveur : {e}")


def stop_server():
    """Arrête le serveur HTTP."""
    global _server
    if _server:
        _server.shutdown()
        _server = None
        print("[JarvisAI] Serveur arrêté")


# ── Blender Addon Registration ─────────────────────────────────

class JARVISAI_OT_StartServer(bpy.types.Operator):
    """Démarre le serveur JarvisAI"""
    bl_idname = "jarvisai.start_server"
    bl_label = "Démarrer Serveur"
    bl_description = "Démarre le serveur API pour JarvisAI"

    def execute(self, context):
        global _server_thread
        if _server_thread and _server_thread.is_alive():
            self.report({'WARNING'}, "Serveur déjà en cours")
            return {'CANCELLED'}

        _server_thread = threading.Thread(target=start_server, daemon=True)
        _server_thread.start()
        self.report({'INFO'}, f"Serveur démarré sur port {_port}")
        return {'FINISHED'}


class JARVISAI_OT_StopServer(bpy.types.Operator):
    """Arrête le serveur JarvisAI"""
    bl_idname = "jarvisai.stop_server"
    bl_label = "Arrêter Serveur"
    bl_description = "Arrête le serveur API"

    def execute(self, context):
        stop_server()
        self.report({'INFO'}, "Serveur arrêté")
        return {'FINISHED'}


class JARVISAI_PT_Panel(bpy.types.Panel):
    """Panel JarvisAI dans la sidebar"""
    bl_label = "JarvisAI"
    bl_idname = "JARVISAI_PT_Panel"
    bl_space_type = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category = 'JarvisAI'

    def draw(self, context):
        layout = self.layout

        row = layout.row()
        row.operator("jarvisai.start_server", icon='PLAY')
        row.operator("jarvisai.stop_server", icon='PAUSE')

        layout.label(text=f"Port : {_port}")
        layout.label(text=f"Statut : {'En cours' if _server_thread and _server_thread.is_alive() else 'Arrêté'}")


classes = (
    JARVISAI_OT_StartServer,
    JARVISAI_OT_StopServer,
    JARVISAI_PT_Panel,
)


def register():
    for cls in classes:
        bpy.utils.register_class(cls)
    print("[JarvisAI] Addon enregistré")


def unregister():
    stop_server()
    for cls in reversed(classes):
        bpy.utils.unregister_class(cls)
    print("[JarvisAI] Addon désenregistré")


if __name__ == "__main__":
    register()
