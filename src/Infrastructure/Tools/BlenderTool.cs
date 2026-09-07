using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;

namespace JarvisAI.Infrastructure.Tools;

public sealed class BlenderTool : ToolBase
{
    public override string Name => "blender";
    public override string Description => "Crée une scène Blender vierge (sans cube) et l'enregistre sur le Bureau sous 'sans cube.blend'. Usage: blender(action: \"create_blank\").";
    public override string Category => "dev";
    public override SecurityRiskLevel RiskLevel => SecurityRiskLevel.High;
    public override TimeSpan Timeout => TimeSpan.FromMinutes(5);
    public override string WaitingPhrase => "J'ouvre Blender et je prépare la scène…";

    public override IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "create_blank", typeof(string), required: true),
    };

    public BlenderTool(ILogger<BlenderTool> logger)
        : base(logger)
    {
    }

    protected override async Task<ToolResult> ExecuteCoreAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        var action = RequireParam(parameters, "action").ToLowerInvariant();
        if (action != "create_blank")
            return Fail($"Action inconnue : '{action}'. Action valide : create_blank");

        var blenderExe = FindBlender();
        if (blenderExe is null)
            return Fail("Blender introuvable — installe-le sous C:\\Program Files\\Blender Foundation");

        var outputPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "sans cube.blend");
        var scriptPath = Path.Combine(Path.GetTempPath(), "jarvis_blender_create_blank.py");
        var python = "import bpy\n" +
                     "bpy.ops.wm.read_factory_settings(use_empty=True)\n" +
                     "bpy.context.scene.name = \"Scene\"\n" +
                     $"bpy.ops.wm.save_as_mainfile(filepath=r\"{outputPath}\")\n";
        await File.WriteAllTextAsync(scriptPath, python, ct);

        var psi = new ProcessStartInfo
        {
            FileName = blenderExe,
            Arguments = $"--background --python \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(psi);
        if (process is null)
            return Fail("Impossible de démarrer Blender.");

        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        var blenderOutput = string.Join(Environment.NewLine, new[] { stdout, stderr }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (!File.Exists(outputPath))
            return Fail($"La scène Blender n'a pas été créée (code {process.ExitCode}).\nSortie Blender :\n{blenderOutput}");

        var sb = new StringBuilder();
        sb.AppendLine($"Scène Blender créée : {outputPath}");
        if (!string.IsNullOrWhiteSpace(stdout))
        {
            sb.AppendLine("Sortie :");
            sb.AppendLine(stdout.TrimEnd());
        }
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            sb.AppendLine("Erreurs Blender :");
            sb.AppendLine(stderr.TrimEnd());
        }

        return Ok(sb.ToString());
    }

    private static string? FindBlender()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Blender Foundation");
        if (!Directory.Exists(root)) return null;

        var candidates = Directory.GetDirectories(root)
            .Where(d => Path.GetFileName(d).StartsWith("Blender "))
            .OrderByDescending(d => Path.GetFileName(d));

        foreach (var dir in candidates)
        {
            var exe = Path.Combine(dir, "blender.exe");
            if (File.Exists(exe))
                return exe;
        }

        return null;
    }
}