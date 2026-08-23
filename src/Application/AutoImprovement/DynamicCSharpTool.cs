using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;

namespace JarvisAI.Application.AutoImprovement;

/// <summary>
/// Outil auto-créé dont l'implémentation est du code C# compilé à chaud (Roslyn)
/// et exécuté dans un sandbox à capabilities (IToolHost) avec deadline stricte.
/// </summary>
public sealed class DynamicCSharpTool : ITool
{
    private readonly IAutoToolCompiler _compiler;
    private readonly IToolHostFactory _hostFactory;
    private readonly TimeSpan _timeout;

    public AutoToolSpec Spec { get; }
    public string Name => Spec.Name;
    public string Description => Spec.Description;
    public string Category => Spec.Category;
    public SecurityRiskLevel RiskLevel => RecipeTool.ParseRisk(Spec.RiskLevel);

    public IReadOnlyList<ToolParameter> Parameters =>
        Spec.Parameters.Select(p => new ToolParameter(p.Name, p.Description, ResolveType(p.Type), p.Required)).ToList();

    public DynamicCSharpTool(AutoToolSpec spec, IAutoToolCompiler compiler, IToolHostFactory hostFactory, TimeSpan? timeout = null)
    {
        Spec = spec;
        _compiler = compiler;
        _hostFactory = hostFactory;
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        try
        {
            var host = _hostFactory.Create();
            var runTask = Task.Run(() => _compiler.Execute(Spec.Name, host, parameters, _timeout), cancellationToken);
            var completed = await Task.WhenAny(runTask, Task.Delay(_timeout, cancellationToken));

            if (completed != runTask)
                return ToolResult.Failed($"Auto-tool '{Spec.Name}' timed out after {_timeout.TotalSeconds:F0}s.");

            var output = await runTask;
            return string.IsNullOrWhiteSpace(output)
                ? ToolResult.Succeeded("(no output)")
                : ToolResult.Succeeded(output);
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Failed($"Auto-tool '{Spec.Name}' was cancelled.");
        }
        catch (Exception ex)
        {
            return ToolResult.Failed($"Auto-tool '{Spec.Name}' failed: {ex.Message}");
        }
    }

    private static Type ResolveType(string type) => type.ToLowerInvariant() switch
    {
        "number" or "double" or "int" => typeof(double),
        "boolean" or "bool" => typeof(bool),
        _ => typeof(string)
    };
}
