namespace JarvisAI.Application.AutoImprovement;

/// <summary>Runtimes supportés par les outils auto-créés.</summary>
public static class AutoToolRuntimes
{
    /// <summary>Suite d'étapes qui appellent des outils déjà approuvés.</summary>
    public const string Recipe = "recipe";

    /// <summary>Code C# compilé à chaud (Roslyn) dans un sandbox à capabilities.</summary>
    public const string CSharp = "csharp";
}

public sealed class AutoToolParameterDef
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Type { get; set; } = "string";
    public bool Required { get; set; }
}

public sealed class AutoToolRecipeStep
{
    public string ToolName { get; set; } = "";
    public Dictionary<string, string> Arguments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Définition persistée d'un outil auto-créé (par l'IA ou l'utilisateur).</summary>
public sealed class AutoToolSpec
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "auto";
    public string RiskLevel { get; set; } = "low";
    public string Runtime { get; set; } = AutoToolRuntimes.Recipe;
    public List<AutoToolParameterDef> Parameters { get; set; } = new();
    public List<AutoToolRecipeStep> Steps { get; set; } = new();
    public string Code { get; set; } = "";
    public string Version { get; set; } = "1";
    public bool Enabled { get; set; } = true;
    public string Source { get; set; } = "ai";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
