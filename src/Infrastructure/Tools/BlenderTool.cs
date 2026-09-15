using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Blender;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Tools;

/// <summary>
/// Contrôle de Blender en arrière-plan (headless) : analyse de scène, création /
/// suppression d'objets, rendu et exécution de code bpy arbitraire. Blender tourne
/// sans fenêtre : le focus de l'utilisateur n'est jamais volé.
/// </summary>
public sealed class BlenderTool : ToolBase
{
    private readonly IBlenderScriptRunner _runner;

    public override string Name => "blender";
    public override string Description =>
        "Contrôle Blender en arrière-plan (headless, sans voler le focus). Actions : " +
        "scene_list (objets de la scène), scene_info (infos moteur/cadre/compteurs), " +
        "create_mesh (cube, sphere, cylinder, cone, torus, plane ; params type, name, size, location_x/y/z), " +
        "delete_object (name), render (moteur EEVEE/CYCLES, image PNG), save (path), " +
        "create_blank (nouvelle scène vierge sur le Bureau), run_code (code bpy arbitraire).";
    public override string Category => "dev";
    public override SecurityRiskLevel RiskLevel => SecurityRiskLevel.High;
    public override TimeSpan Timeout => TimeSpan.FromMinutes(15);
    public override string WaitingPhrase => "J'ouvre Blender en arrière-plan…";

    public override IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "scene_list, scene_info, create_mesh, delete_object, render, save, create_blank, run_code", typeof(string), required: true),
        new ToolParameter("type", "Forme à créer : cube, sphere, cylinder, cone, torus, plane (pour create_mesh)", typeof(string)),
        new ToolParameter("name", "Nom de l'objet (pour create_mesh / delete_object)", typeof(string)),
        new ToolParameter("size", "Taille de l'objet à créer (pour create_mesh)", typeof(double)),
        new ToolParameter("location_x", "Position X (pour create_mesh)", typeof(double)),
        new ToolParameter("location_y", "Position Y (pour create_mesh)", typeof(double)),
        new ToolParameter("location_z", "Position Z (pour create_mesh)", typeof(double)),
        new ToolParameter("engine", "Moteur de rendu : EEVEE, EEVEE_NEXT ou CYCLES (pour render)", typeof(string)),
        new ToolParameter("samples", "Échantillons Cycles (pour render)", typeof(int)),
        new ToolParameter("path", "Chemin du fichier .blend à enregistrer (pour save) ou de sortie du rendu (pour render)", typeof(string)),
        new ToolParameter("code", "Code Python bpy à exécuter (pour run_code)", typeof(string))
    };

    public BlenderTool(ILogger<BlenderTool> logger, IBlenderScriptRunner runner)
        : base(logger)
    {
        _runner = runner;
    }

    protected override async Task<ToolResult> ExecuteCoreAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        var action = RequireParam(parameters, "action").ToLowerInvariant();

        return action switch
        {
            "scene_list" => await ActionAsync(_ => SceneListBody, ct),
            "scene_info" => await ActionAsync(_ => SceneInfoBody, ct),
            "create_mesh" => await ActionAsync(_ => new Template(parameters).CreateMesh(), ct),
            "delete_object" => await ActionAsync(_ => new Template(parameters).DeleteObject(), ct),
            "render" => await ActionAsync(_ => new Template(parameters).Render(), ct),
            "save" => await ActionAsync(_ => new Template(parameters).Save(), ct),
            "create_blank" => await ActionAsync(_ => CreateBlankBody, ct),
            "run_code" => await ActionAsync(_ => new Template(parameters).RunCode(), ct),
            _ => Fail($"Action inconnue : '{action}'. Valides : scene_list, scene_info, create_mesh, delete_object, render, save, create_blank, run_code")
        };
    }

    private async Task<ToolResult> ActionAsync(Func<IReadOnlyDictionary<string, string>, string> mapBody, CancellationToken ct)
    {
        try
        {
            var jsonPath = Path.Combine(Path.GetTempPath(), $"jarvis_blender_out_{Guid.NewGuid():N}.json");
            var body = mapBody(EmptyDict) + JsonEpilogue(jsonPath);
            var resultat = await _runner.RunAsync(body, jsonPath, ct);

            if (resultat.JsonOutputPath is not null)
            {
                try
                {
                    var json = await File.ReadAllTextAsync(resultat.JsonOutputPath, ct);
                    try { File.Delete(resultat.JsonOutputPath); } catch { /* best-effort */ }

                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("error", out var e))
                        return Fail($"Blender : {e.GetString()}{(resultat.ExitCode != 0 ? $" (code {resultat.ExitCode})" : string.Empty)}");

                    return Ok(FormatJson(root));
                }
                catch (JsonException)
                {
                    return Fail("Blender a produit une sortie invalide.");
                }
            }

            var detail = string.Join(Environment.NewLine,
                new[] { resultat.StandardOutput, resultat.StandardError }.Where(s => !string.IsNullOrWhiteSpace(s)));
            return Fail($"Blender s'est terminé avec le code {resultat.ExitCode} sans résultat.{(detail.Length > 0 ? $"\n{Truncate(detail, 800)}" : string.Empty)}");
        }
        catch (BlenderNotFoundException ex)
        {
            return Fail(ex.Message);
        }
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyDict = new Dictionary<string, string>();

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value.Substring(0, max) + "…";

    private static string JsonEpilogue(string path) =>
        "import json\n" +
        $"with open(r\"{path}\", \"w\", encoding=\"utf-8\") as _f:\n" +
        "    json.dump(_out, _f, ensure_ascii=False, indent=2)\n";

    private const string SceneListBody = """
        import bpy
        objets = []
        for o in bpy.context.scene.objects:
            d = o.dimensions
            loc = o.location
            objets.append({
                "name": o.name,
                "type": o.type,
                "location": [round(loc[0], 3), round(loc[1], 3), round(loc[2], 3)],
                "dimensions": [round(d[0], 3), round(d[1], 3), round(d[2], 3)],
                "visible": not o.hide_viewport,
                "materials": len(o.data.materials) if o.data is not None and hasattr(o.data, "materials") else 0,
            })
        objets.sort(key=lambda x: x["name"].lower())
        _out = {"object_count": len(objets), "objects": objets}
        """;

    private const string SceneInfoBody = """
        import bpy
        sc = bpy.context.scene
        moteur = sc.render.engine
        echantillons = None
        if "cycles" in bpy.context.scene:
            echantillons = sc.cycles.samples
        _out = {
            "scene_name": sc.name,
            "engine": moteur,
            "frame_start": sc.frame_start,
            "frame_end": sc.frame_end,
            "resolution": [sc.render.resolution_x, sc.render.resolution_y],
            "render_percentage": sc.render.resolution_percentage,
            "cycles_samples": echantillons,
            "object_count": len(sc.objects),
            "mesh_count": len([o for o in sc.objects if o.type == "MESH"]),
            "materials_count": len(bpy.data.materials),
            "collections": [c.name for c in bpy.data.collections],
        }
        """;

    private const string CreateBlankBody = """
        import bpy
        chemin = r"__OUTPUT_PATH__"
        bpy.ops.wm.read_factory_settings(use_empty=True)
        bpy.context.scene.name = "Scene"
        bpy.ops.wm.save_as_mainfile(filepath=chemin)
        _out = {"saved": bpy.path.abspath(chemin)}
        """;

    private const string CreateMeshBody = """
        import bpy
        type_m = "__TYPE__"
        nom = __NAME__
        taille = float("__SIZE__")
        loc = (float("__X__"), float("__Y__"), float("__Z__"))
        bpy.ops.object.select_all(action="DESELECT")
        if type_m == "cube":
            bpy.ops.mesh.primitive_cube_add(size=taille, location=loc)
        elif type_m == "sphere":
            bpy.ops.mesh.primitive_uv_sphere_add(radius=taille, location=loc)
        elif type_m == "cylinder":
            bpy.ops.mesh.primitive_cylinder_add(radius=taille, depth=taille * 2, location=loc)
        elif type_m == "cone":
            bpy.ops.mesh.primitive_cone_add(radius1=taille, radius2=0.0, depth=taille * 2, location=loc)
        elif type_m == "torus":
            bpy.ops.mesh.primitive_torus_add(major_radius=taille, minor_radius=taille * 0.35, location=loc)
        elif type_m == "plane":
            bpy.ops.mesh.primitive_plane_add(size=taille, location=loc)
        else:
            _out = {"error": f"Forme inconnue: {type_m}. Valides: cube, sphere, cylinder, cone, torus, plane"}
            raise RuntimeError()
        obj = bpy.context.active_object
        if nom:
            obj.name = nom
        _out = {"created": obj.name, "type": obj.type, "location": [round(loc[0], 3), round(loc[1], 3), round(loc[2], 3)]}
        """;

    private const string DeleteObjectBody = """
        import bpy
        cible = __NAME__
        obj = bpy.data.objects.get(cible)
        if obj is None:
            _out = {"error": f"Objet introuvable: {cible}"}
            raise RuntimeError()
        bpy.data.objects.remove(obj, do_unlink=True)
        _out = {"deleted": cible}
        """;

    private const string RenderBody = """
        import bpy, os, glob
        moteur = __ENGINE__
        echantillons = __SAMPLES__
        sortie = r"__OUTPUT_PATH__"
        cfg = {}
        if moteur:
            try:
                bpy.context.scene.render.engine = moteur
            except TypeError as e:
                cfg["engine_error"] = str(e)
        cfg["engine"] = bpy.context.scene.render.engine
        if "cycles" in bpy.context.scene and echantillons is not None:
            bpy.context.scene.cycles.samples = echantillons
        bpy.context.scene.render.filepath = sortie
        bpy.context.scene.render.image_settings.file_format = "PNG"
        bpy.ops.render.render(write_still=True)
        base, ext = os.path.splitext(bpy.path.abspath(sortie))
        cfg["rendered"] = len(glob.glob(base + "*" + ext))
        cfg["filepath"] = bpy.path.abspath(sortie)
        _out = cfg
        """;

    private const string SaveBody = """
        import bpy
        chemin = __PATH__
        bpy.ops.wm.save_as_mainfile(filepath=chemin)
        _out = {"saved": bpy.path.abspath(chemin)}
        """;

    private const string RunCodeBody = """
        import json
        __CODE__
        _out = locals().get("_out", {"message": "Exécuté sans résultat JSON"})
        """;

    private static string FormatJson(JsonElement root)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                sb.AppendLine($"{prop.Name} : {prop.Value.GetRawText()}");
            else
                sb.AppendLine($"{prop.Name} : {prop.Value}");
        }
        return sb.ToString().TrimEnd();
    }

    private static string Quote(string value)
        => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string PyLiteralOrNone(string? value)
        => string.IsNullOrWhiteSpace(value) ? "None" : Quote(value.Trim());

    private static string FloatInvariant(string raw)
        => double.TryParse(raw.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v.ToString(CultureInfo.InvariantCulture)
            : "1.0";

    private static string CoordInvariant(string raw)
        => double.TryParse(raw.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v.ToString(CultureInfo.InvariantCulture)
            : "0.0";

    private sealed class Template
    {
        private readonly IReadOnlyDictionary<string, string> _p;

        public Template(IReadOnlyDictionary<string, string> parameters)
        {
            _p = parameters;
        }

        public string CreateMesh() => CreateMeshBody
            .Replace("__TYPE__", Get("type", "cube").Trim().ToLowerInvariant())
            .Replace("__NAME__", PyLiteralOrNone(Get("name", null)))
            .Replace("__SIZE__", FloatInvariant(Get("size", "1.0")))
            .Replace("__X__", CoordInvariant(Get("location_x", "0")))
            .Replace("__Y__", CoordInvariant(Get("location_y", "0")))
            .Replace("__Z__", CoordInvariant(Get("location_z", "0")));

        public string DeleteObject()
        {
            var nom = Get("name", null);
            if (string.IsNullOrWhiteSpace(nom))
                throw new ArgumentException("Paramètre requis manquant : 'name'.");
            return DeleteObjectBody.Replace("__NAME__", Quote(nom.Trim()));
        }

        public string Render()
        {
            var path = Get("path", null);
            if (string.IsNullOrWhiteSpace(path))
                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "jarvis_render.png");
            var moteur = Get("engine", null)?.Trim().ToUpperInvariant() switch
            {
                "EEVEE" => "BLENDER_EEVEE",
                "EEVEE_NEXT" => "BLENDER_EEVEE_NEXT",
                "CYCLES" => "CYCLES",
                "WORKBENCH" => "BLENDER_WORKBENCH",
                var m when !string.IsNullOrEmpty(m) => m,
                _ => null
            };
            return RenderBody
                .Replace("__ENGINE__", PyLiteralOrNone(moteur))
                .Replace("__SAMPLES__", int.TryParse(Get("samples", null), out var s) ? s.ToString() : "None")
                .Replace("__OUTPUT_PATH__", path);
        }

        public string Save()
        {
            var path = Get("path", null);
            if (string.IsNullOrWhiteSpace(path))
                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "jarvis_blender.blend");
            return SaveBody.Replace("__PATH__", Quote(path));
        }

        public string RunCode()
        {
            var code = Get("code", null);
            if (string.IsNullOrWhiteSpace(code))
                throw new ArgumentException("Paramètre requis manquant : 'code'.");
            return RunCodeBody.Replace("__CODE__", code.TrimEnd());
        }

        private string Get(string key, string? defaut)
            => _p.TryGetValue(key, out var v) && v.Trim().Length > 0 ? v : (defaut ?? string.Empty);
    }
}