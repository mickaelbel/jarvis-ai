"""
JarvisAI Blender Integration Addon
==================================
Expose une API HTTP locale pour controller Blender depuis JarvisAI.
Port : 7777

bl_info dict for Blender addon registration.
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

bl_info = {
    "name": "JarvisAI Integration",
    "author": "JarvisAI",
    "version": (1, 0, 0),
    "blender": (3, 0, 0),
    "location": "View3D > Sidebar > JarvisAI",
    "description": "HTTP API to control Blender from JarvisAI",
    "category": "System",
}

_server = None
_server_thread = None
_port = 7777


class JarvisAIHandler(BaseHTTPRequestHandler):

    def log_message(self, format, *args):
        pass

    def do_GET(self):
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
            elif path == "/eval":
                code = params.get("code", [""])[0]
                self.send_json(self.eval_code(code))
            else:
                self.send_json({"error": "Unknown endpoint: " + path}, 404)
        except Exception as e:
            self.send_json({"error": str(e), "traceback": traceback.format_exc()}, 500)

    def do_POST(self):
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
            elif path == "/new_scene":
                self.send_json(self.new_scene(data))
            elif path == "/save":
                self.send_json(self.save_file(data))
            else:
                self.send_json({"error": "Unknown endpoint: " + path}, 404)
        except Exception as e:
            self.send_json({"error": str(e), "traceback": traceback.format_exc()}, 500)

    def send_json(self, data, code=200):
        response = json.dumps(data, ensure_ascii=False, default=str)
        self.send_response(code)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Access-Control-Allow-Origin', '*')
        self.end_headers()
        self.wfile.write(response.encode('utf-8'))

    def get_scene_info(self):
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
        }

    def get_objects(self):
        objects = []
        for obj in bpy.context.scene.objects:
            objects.append({
                "name": obj.name,
                "type": obj.type,
                "location": list(obj.location),
                "rotation": list(obj.rotation_euler),
                "scale": list(obj.scale),
                "visible": obj.visible_get(),
            })
        return {"objects": objects, "count": len(objects)}

    def get_cameras(self):
        cameras = []
        for cam in bpy.data.cameras:
            cameras.append({
                "name": cam.name,
                "lens": cam.lens,
                "clip_start": cam.clip_start,
                "clip_end": cam.clip_end,
            })
        return {"cameras": cameras, "count": len(cameras)}

    def get_materials(self):
        materials = []
        for mat in bpy.data.materials:
            materials.append({
                "name": mat.name,
                "use_nodes": mat.use_nodes,
            })
        return {"materials": materials, "count": len(materials)}

    def eval_code(self, code):
        if not code.strip():
            return {"error": "Empty code"}

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

    def add_object(self, data):
        obj_type = data.get("type", "CUBE")
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
            elif obj_type == "LIGHT":
                bpy.ops.object.light_add(type='POINT', location=location)
            elif obj_type == "CAMERA":
                bpy.ops.object.camera_add(location=location)
            else:
                return {"error": "Unsupported type: " + obj_type}

            obj = bpy.context.active_object
            obj.name = name
            return {"success": True, "name": obj.name, "type": obj_type}
        except Exception as e:
            return {"error": str(e)}

    def delete_object(self, name):
        try:
            if name in bpy.data.objects:
                obj = bpy.data.objects[name]
                bpy.data.objects.remove(obj, do_unlink=True)
                return {"success": True, "deleted": name}
            return {"error": "Object not found: " + name}
        except Exception as e:
            return {"error": str(e)}

    def modify_object(self, data):
        name = data.get("name", "")
        try:
            if name not in bpy.data.objects:
                return {"error": "Object not found: " + name}

            obj = bpy.data.objects[name]

            if "location" in data:
                obj.location = data["location"]
            if "rotation" in data:
                obj.rotation_euler = data["rotation"]
            if "scale" in data:
                obj.scale = data["scale"]

            return {"success": True, "modified": name}
        except Exception as e:
            return {"error": str(e)}

    def new_scene(self, data):
        try:
            bpy.ops.wm.read_factory_settings(use_empty=True)
            return {"success": True, "message": "New scene created"}
        except Exception as e:
            return {"error": str(e)}

    def save_file(self, data):
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
    global _server, _port
    try:
        _server = HTTPServer(('127.0.0.1', _port), JarvisAIHandler)
        print("[JarvisAI] Server started on http://127.0.0.1:" + str(_port))
        _server.serve_forever()
    except OSError as e:
        if "already in use" in str(e):
            _port += 1
            start_server()
        else:
            print("[JarvisAI] Server error: " + str(e))


def stop_server():
    global _server
    if _server:
        _server.shutdown()
        _server = None
        print("[JarvisAI] Server stopped")


class JARVISAI_OT_StartServer(bpy.types.Operator):
    bl_idname = "jarvisai.start_server"
    bl_label = "Start Server"
    bl_description = "Start the JarvisAI API server"

    def execute(self, context):
        global _server_thread
        if _server_thread and _server_thread.is_alive():
            self.report({'WARNING'}, "Server already running")
            return {'CANCELLED'}
        _server_thread = threading.Thread(target=start_server, daemon=True)
        _server_thread.start()
        self.report({'INFO'}, "Server started on port " + str(_port))
        return {'FINISHED'}


class JARVISAI_OT_StopServer(bpy.types.Operator):
    bl_idname = "jarvisai.stop_server"
    bl_label = "Stop Server"
    bl_description = "Stop the JarvisAI API server"

    def execute(self, context):
        stop_server()
        self.report({'INFO'}, "Server stopped")
        return {'FINISHED'}


class JARVISAI_PT_Panel(bpy.types.Panel):
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
        layout.label(text="Port: " + str(_port))
        status = "Running" if _server_thread and _server_thread.is_alive() else "Stopped"
        layout.label(text="Status: " + status)


classes = (
    JARVISAI_OT_StartServer,
    JARVISAI_OT_StopServer,
    JARVISAI_PT_Panel,
)


def register():
    for cls in classes:
        bpy.utils.register_class(cls)
    print("[JarvisAI] Addon registered")


def unregister():
    stop_server()
    for cls in reversed(classes):
        bpy.utils.unregister_class(cls)
    print("[JarvisAI] Addon unregistered")


if __name__ == "__main__":
    register()
