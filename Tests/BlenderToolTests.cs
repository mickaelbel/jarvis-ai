using JarvisAI.Application.Agents;
using JarvisAI.Infrastructure.Blender;
using JarvisAI.Infrastructure.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace JarvisAI.Tests;

public sealed class BlenderToolTests
{
    private static readonly AgentContext Context = new("blender");

    private sealed class FakeRunner : IBlenderScriptRunner
    {
        public string? LastBody { get; private set; }
        public Func<string, string>? Responder { get; set; }
        public BlenderRunResult Result { get; set; } = new(string.Empty, string.Empty, 0, null);

        public async Task<BlenderRunResult> RunAsync(string pythonBody, string jsonOutputPath, CancellationToken cancellationToken)
        {
            LastBody = pythonBody;
            if (Responder is not null)
            {
                var json = Responder(pythonBody);
                await File.WriteAllTextAsync(jsonOutputPath, json, cancellationToken);
                Result = Result with { JsonOutputPath = jsonOutputPath };
            }
            return Result;
        }
    }

    private static BlenderTool NewTool(FakeRunner runner)
        => new(NullLogger<BlenderTool>.Instance, runner);

    private static Dictionary<string, string> Params(params (string K, string V)[] items)
        => items.ToDictionary(x => x.K, x => x.V, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public async Task SceneList_SetsBodyAndParsesJson()
    {
        var runner = new FakeRunner
        {
            Responder = _ => JsonSerializer.Serialize(new
            {
                object_count = 2,
                objects = new[]
                {
                    new { name = "Cube", type = "MESH", location = new double[] { 0, 0, 0 }, dimensions = new double[] { 2, 2, 2 }, visible = true, materials = 1 },
                    new { name = "Camera", type = "CAMERA", location = new double[] { 7, -6, 5 }, dimensions = new double[] { 0, 0, 0 }, visible = true, materials = 0 }
                }
            })
        };
        var tool = NewTool(runner);

        var resultat = await tool.ExecuteAsync(Context, Params(("action", "scene_list")));

        Assert.True(resultat.Success);
        Assert.Contains("object_count : 2", resultat.Output);
        Assert.Contains("Cube", resultat.Output);
        Assert.Contains("bpy.context.scene.objects", runner.LastBody!);
    }

    [Fact]
    public async Task CreateMesh_MapsParametersIntoPython()
    {
        var runner = new FakeRunner
        {
            Responder = _ => JsonSerializer.Serialize(new { created = "ma_sphere", type = "MESH", location = new double[] { 1.5, 2, 3 } })
        };
        var tool = NewTool(runner);

        var resultat = await tool.ExecuteAsync(Context, Params(
            ("action", "create_mesh"),
            ("type", "sphere"),
            ("name", "ma_sphere"),
            ("size", "1.5"),
            ("location_x", "1,5"),
            ("location_y", "2"),
            ("location_z", "3")));

        Assert.True(resultat.Success);
        Assert.Contains("ma_sphere", resultat.Output);
        Assert.Contains("primitive_uv_sphere_add(radius=taille", runner.LastBody!);
        Assert.Contains("taille = float(\"1.5\")", runner.LastBody!);
        Assert.Contains("float(\"1.5\"), float(\"2\"), float(\"3\")", runner.LastBody!);
        Assert.Contains("type_m = \"sphere\"", runner.LastBody!);
    }

    [Fact]
    public async Task DeleteObject_UnknownObject_ReturnsError()
    {
        var runner = new FakeRunner
        {
            Responder = _ => JsonSerializer.Serialize(new { error = "Objet introuvable: Fantome" })
        };
        var tool = NewTool(runner);

        var resultat = await tool.ExecuteAsync(Context, Params(("action", "delete_object"), ("name", "Fantome")));

        Assert.False(resultat.Success);
        Assert.Contains("introuvable", resultat.ErrorMessage);
    }

    [Fact]
    public async Task RunCode_WithoutCode_Fails()
    {
        var runner = new FakeRunner();
        var tool = NewTool(runner);

        var resultat = await tool.ExecuteAsync(Context, Params(("action", "run_code")));

        Assert.False(resultat.Success);
        Assert.Null(runner.LastBody);
    }

    [Fact]
    public async Task Render_SetsEngineAndOutputPath()
    {
        var runner = new FakeRunner
        {
            Responder = _ => JsonSerializer.Serialize(new { engine = "CYCLES", rendered = 1, filepath = "C:\\render.png" })
        };
        var tool = NewTool(runner);

        var resultat = await tool.ExecuteAsync(Context, Params(
            ("action", "render"),
            ("engine", "cycles"),
            ("path", "C:\\mes renders\\shot.png")));

        Assert.True(resultat.Success);
        Assert.Contains("engine : CYCLES", resultat.Output);
        Assert.Contains("moteur = \"CYCLES\"", runner.LastBody!);
        Assert.Contains("mes renders", runner.LastBody!);
        Assert.Contains("bpy.ops.render.render(write_still=True)", runner.LastBody!);
    }

    [Fact]
    public async Task BlenderNotFound_ReturnsClearFailure()
    {
        var tool = new BlenderTool(
            NullLogger<BlenderTool>.Instance,
            new ThrowingRunner());

        var resultat = await tool.ExecuteAsync(Context, Params(("action", "create_blank")));

        Assert.False(resultat.Success);
        Assert.Contains("Blender introuvable", resultat.ErrorMessage);
    }

    [Fact]
    public async Task UnknownAction_Fails()
    {
        var tool = NewTool(new FakeRunner());

        var resultat = await tool.ExecuteAsync(Context, Params(("action", "explose")));

        Assert.False(resultat.Success);
        Assert.Contains("Action inconnue", resultat.ErrorMessage);
    }

    private sealed class ThrowingRunner : IBlenderScriptRunner
    {
        public Task<BlenderRunResult> RunAsync(string pythonBody, string jsonOutputPath, CancellationToken cancellationToken)
            => throw new BlenderNotFoundException("Blender introuvable — installe-le sous C:\\Program Files\\Blender Foundation");
    }
}