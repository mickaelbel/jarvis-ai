namespace JarvisAI.Application.AutoImprovement;

/// <summary>Compilation + exécution du code C# des outils auto-créés (sandbox).</summary>
public interface IAutoToolCompiler
{
    /// <summary>Valide et compile le code. En cas d'échec, renvoie un résultat non-OK.</summary>
    CompileResult Compile(string toolName, string code);

    /// <summary>Exécute le code compilé pour l'outil donné. Renvoie la sortie texte (ou une erreur).</summary>
    string Execute(string toolName, IToolHost host, IReadOnlyDictionary<string, string> args, TimeSpan timeout);
}

public sealed class CompileResult
{
    public bool Success { get; }
    public string? Error { get; }

    private CompileResult(bool success, string? error)
    {
        Success = success;
        Error = error;
    }

    public static CompileResult Ok() => new(true, null);
    public static CompileResult Fail(string error) => new(false, error);
}
