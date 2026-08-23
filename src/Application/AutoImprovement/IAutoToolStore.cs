namespace JarvisAI.Application.AutoImprovement;

/// <summary>Persistance des outils auto-créés, des leçons et du safe-mode.</summary>
public interface IAutoToolStore
{
    bool SafeMode { get; set; }

    IReadOnlyList<AutoToolSpec> LoadTools();
    AutoToolSpec? GetTool(string name);
    void SaveTool(AutoToolSpec spec);
    void DeleteTool(string name);

    IReadOnlyList<string> LoadLessons(int maxCount = 25);
    void AppendLesson(string lesson);
    void ClearLessons();
}

public sealed class AutoToolCreateResult
{
    public bool Success { get; }
    public string Name { get; }
    public string? Error { get; }

    private AutoToolCreateResult(bool success, string name, string? error)
    {
        Success = success;
        Name = name;
        Error = error;
    }

    public static AutoToolCreateResult Ok(string name) => new(true, name, null);
    public static AutoToolCreateResult Fail(string error) => new(false, "", error);
}
