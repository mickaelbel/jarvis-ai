namespace JarvisAI.Application.AutoImprovement;

/// <summary>
/// Gère la création, la persistance et le cycle de vie des outils auto-créés
/// ainsi que la mémoire « leçons » et le mode sûr (auto-amélioration désactivée).
/// </summary>
public interface ISelfImprovementManager
{
    bool SafeMode { get; set; }

    /// <summary>Recharge au démarrage les outils auto-créés persistés (respecte le safe-mode).</summary>
    void LoadPersisted();

    AutoToolCreateResult CreateTool(AutoToolSpec spec, string source);

    bool EnableTool(string name);
    bool DisableTool(string name);
    bool RemoveTool(string name);
    IReadOnlyList<AutoToolSpec> ListTools();
    AutoToolSpec? GetTool(string name);

    void AddLesson(string lesson);
    IReadOnlyList<string> GetLessons(int maxCount = 25);
}
