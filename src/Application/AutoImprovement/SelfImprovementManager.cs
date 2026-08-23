using JarvisAI.Application.Tools;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Application.AutoImprovement;

public sealed class SelfImprovementManager : ISelfImprovementManager
{
    private readonly IAutoToolStore _store;
    private readonly Lazy<IToolRegistry> _registry;
    private readonly Lazy<IToolExecutor> _executor;
    private readonly IAutoToolCompiler _compiler;
    private readonly IToolHostFactory _hostFactory;
    private readonly ILogger<SelfImprovementManager> _logger;

    public SelfImprovementManager(
        IAutoToolStore store,
        Lazy<IToolRegistry> registry,
        Lazy<IToolExecutor> executor,
        IAutoToolCompiler compiler,
        IToolHostFactory hostFactory,
        ILogger<SelfImprovementManager> logger)
    {
        _store = store;
        _registry = registry;
        _executor = executor;
        _compiler = compiler;
        _hostFactory = hostFactory;
        _logger = logger;
    }

    public bool SafeMode
    {
        get => _store.SafeMode;
        set => _store.SafeMode = value;
    }

    public void LoadPersisted()
    {
        var reg = _registry.Value;
        foreach (var spec in _store.LoadTools())
        {
            if (!spec.Enabled)
                continue;
            if (SafeMode)
            {
                _logger.LogInformation("[SelfImprovement] Skipping auto-tool {Name} (safe mode)", spec.Name);
                continue;
            }

            var tool = BuildTool(spec);
            reg.Register(tool);
            _logger.LogInformation("[SelfImprovement] Loaded auto-tool: {Name} (runtime={Runtime}, risk={Risk})",
                spec.Name, spec.Runtime, spec.RiskLevel);
        }
    }

    public AutoToolCreateResult CreateTool(AutoToolSpec spec, string source)
    {
        if (!AutoToolGuardrails.IsNameValid(spec.Name))
            return AutoToolCreateResult.Fail($"Invalid tool name '{spec.Name}' (use 3-40 lowercase letters/digits/underscores).");
        if (AutoToolGuardrails.IsReserved(spec.Name))
            return AutoToolCreateResult.Fail($"'{spec.Name}' is a reserved core tool and cannot be overridden.");
        if (string.IsNullOrWhiteSpace(spec.Description))
            return AutoToolCreateResult.Fail("A description is required.");
        if (!AutoToolGuardrails.IsRiskLevelValid(spec.RiskLevel))
            return AutoToolCreateResult.Fail("RiskLevel must be 'low', 'medium' or 'high'.");
        if (spec.Runtime is not (AutoToolRuntimes.Recipe or AutoToolRuntimes.CSharp))
            return AutoToolCreateResult.Fail("Runtime must be 'recipe' or 'csharp'.");
        if (spec.Parameters is { Count: > 12 })
            return AutoToolCreateResult.Fail("At most 12 parameters are allowed.");
        if (spec.Parameters.Any(p => string.IsNullOrWhiteSpace(p.Name)))
            return AutoToolCreateResult.Fail("Every parameter needs a name.");

        var existing = _registry.Value.GetByName(spec.Name);
        if (existing is not null && existing is not RecipeTool and not DynamicCSharpTool)
            return AutoToolCreateResult.Fail($"Tool '{spec.Name}' already exists as a core tool.");

        if (spec.Runtime == AutoToolRuntimes.Recipe)
        {
            if (spec.Steps.Count == 0)
                return AutoToolCreateResult.Fail("A recipe tool needs at least one step.");
            if (spec.Steps.Count > 20)
                return AutoToolCreateResult.Fail("At most 20 steps are allowed.");

            foreach (var step in spec.Steps)
            {
                if (string.IsNullOrWhiteSpace(step.ToolName))
                    return AutoToolCreateResult.Fail("Every step needs a toolName.");
                if (string.Equals(step.ToolName, spec.Name, StringComparison.OrdinalIgnoreCase))
                    return AutoToolCreateResult.Fail($"Recipe step cannot call the tool itself ('{spec.Name}').");

                var referenced = _registry.Value.GetByName(step.ToolName);
                if (referenced is null && _store.GetTool(step.ToolName) is null)
                    return AutoToolCreateResult.Fail($"Recipe references unknown tool '{step.ToolName}'.");
            }

            if (WouldCreateCycle(spec))
                return AutoToolCreateResult.Fail($"Recipe '{spec.Name}' would create a cycle through other auto-tools.");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(spec.Code))
                return AutoToolCreateResult.Fail("A csharp tool needs code.");
            var compile = _compiler.Compile(spec.Name, spec.Code);
            if (!compile.Success)
                return AutoToolCreateResult.Fail($"Code rejected: {compile.Error}");
        }

        spec.Category = string.IsNullOrWhiteSpace(spec.Category) ? "auto" : spec.Category.Trim();
        spec.Source = source;
        spec.Version = NextVersion(spec.Name, existing);
        spec.CreatedAt = existing is null ? DateTime.UtcNow : spec.CreatedAt == default ? DateTime.UtcNow : spec.CreatedAt;
        spec.UpdatedAt = DateTime.UtcNow;
        spec.Enabled = true;

        _store.SaveTool(spec);

        var tool = BuildTool(spec);
        var reg = _registry.Value;
        reg.Unregister(spec.Name);
        reg.Register(tool);

        _logger.LogInformation("[SelfImprovement] Created/upgraded auto-tool: {Name} (runtime={Runtime}, risk={Risk}, source={Source})",
            spec.Name, spec.Runtime, spec.RiskLevel, source);

        return AutoToolCreateResult.Ok(spec.Name);
    }

    public bool EnableTool(string name)
    {
        var spec = _store.GetTool(name);
        if (spec is null) return false;
        spec.Enabled = true;
        _store.SaveTool(spec);
        if (!SafeMode)
            _registry.Value.Register(BuildTool(spec));
        return true;
    }

    public bool DisableTool(string name)
    {
        var spec = _store.GetTool(name);
        if (spec is null) return false;
        spec.Enabled = false;
        _store.SaveTool(spec);
        _registry.Value.Unregister(name);
        return true;
    }

    public bool RemoveTool(string name)
    {
        if (_store.GetTool(name) is null) return false;
        _store.DeleteTool(name);
        _registry.Value.Unregister(name);
        return true;
    }

    public IReadOnlyList<AutoToolSpec> ListTools() => _store.LoadTools();

    public AutoToolSpec? GetTool(string name) => _store.GetTool(name);

    public void AddLesson(string lesson)
    {
        if (string.IsNullOrWhiteSpace(lesson)) return;
        var trimmed = lesson.Trim();
        if (trimmed.Length > 500) trimmed = trimmed[..500];
        _store.AppendLesson(trimmed);
    }

    public IReadOnlyList<string> GetLessons(int maxCount = 25) => _store.LoadLessons(maxCount);

    private ITool BuildTool(AutoToolSpec spec)
        => spec.Runtime == AutoToolRuntimes.CSharp
            ? new DynamicCSharpTool(spec, _compiler, _hostFactory)
            : new RecipeTool(spec, _executor.Value);

    private static string NextVersion(string name, ITool? existing)
    {
        if (existing is null) return "1";
        if (existing is RecipeTool rt) return Bump(rt.Spec.Version);
        if (existing is DynamicCSharpTool ct) return Bump(ct.Spec.Version);
        return "1";
    }

    private static string Bump(string version)
        => int.TryParse(version, out var v) ? (v + 1).ToString() : "2";

    /// <summary>
    /// Vérifie que le nouveau recipe (et ses éventuels enchaînements d'autres
    /// skills) ne forme pas un cycle : chaque skill appelé est déplié depuis le
    /// store et on cherche un chemin qui revient à <paramref name="spec"/>.
    /// </summary>
    private bool WouldCreateCycle(AutoToolSpec spec)
    {
        var start = spec.Name;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();

        foreach (var step in spec.Steps)
        {
            if (_store.GetTool(step.ToolName) is not null)
                queue.Enqueue(step.ToolName);
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (string.Equals(current, start, StringComparison.OrdinalIgnoreCase))
                return true;
            if (!visited.Add(current))
                continue;

            var currentSpec = _store.GetTool(current);
            if (currentSpec is null)
                continue;

            foreach (var step in currentSpec.Steps)
            {
                if (_store.GetTool(step.ToolName) is not null)
                    queue.Enqueue(step.ToolName);
            }
        }

        return false;
    }
}
