namespace JarvisAI.Application.AI;

public sealed record KnownModelSpec(string Name, string Parameters, string DownloadSize, string Note);

/// <summary>
/// Registre des modèles connus, ordonnés par pertinence pour chaque catégorie de tâche.
/// Ces entrées servent au sélecteur IA pour recommander le meilleur modèle par tâche
/// (et proposer un téléchargement si le modèle n'est pas installé).
/// </summary>
public static class KnownModels
{
    public const string BaseFastModel = "qwen3.5:2b";

    private static readonly IReadOnlyDictionary<TaskCategory, IReadOnlyList<KnownModelSpec>> _byCategory =
        new Dictionary<TaskCategory, IReadOnlyList<KnownModelSpec>>
        {
            [TaskCategory.Quick] = new[]
            {
                new KnownModelSpec("qwen3.5:2b", "2B", "1.6 Go", "Rapide et léger pour les demandes simples"),
            },
            [TaskCategory.General] = new[]
            {
                new KnownModelSpec("qwen3.5:8b", "8B", "5.2 Go", "Meilleur généraliste FR et raisonnement"),
                new KnownModelSpec("gemma3:12b", "12B", "8.1 Go", "Excellent multilingue"),
                new KnownModelSpec("llama3.1:8b", "8B", "4.7 Go", "Généraliste Meta"),
                new KnownModelSpec("mistral:7b", "7B", "4.4 Go", "Rapide et fiable"),
            },
            [TaskCategory.Code] = new[]
            {
                new KnownModelSpec("qwen3-coder:30b", "30B", "19 Go", "Meilleur en codage complexe"),
                new KnownModelSpec("qwen3-coder:14b", "14B", "9 Go", "Excellent rapport qualité/VRAM"),
                new KnownModelSpec("qwen2.5-coder:14b", "14B", "9 Go", "Codage fiable"),
                new KnownModelSpec("qwen2.5-coder:7b", "7B", "4.7 Go", "Bon codage léger"),
                new KnownModelSpec("deepseek-coder:6.7b", "6.7B", "3.8 Go", "Codage léger"),
            },
            [TaskCategory.Math] = new[]
            {
                new KnownModelSpec("deepseek-r1:14b", "14B", "9 Go", "Raisonnement mathématique fort"),
                new KnownModelSpec("deepseek-r1:8b", "8B", "4.9 Go", "Maths avec moins de VRAM"),
                new KnownModelSpec("qwen3-reasoning:8b", "8B", "4.7 Go", "Pensée Qwen"),
            },
            [TaskCategory.Reasoning] = new[]
            {
                new KnownModelSpec("deepseek-r1:14b", "14B", "9 Go", "Raisonnement approfondi"),
                new KnownModelSpec("llama3.3:70b", "70B", "40 Go", "Très fort si la VRAM le permet"),
                new KnownModelSpec("qwen3-reasoning:8b", "8B", "4.7 Go", "Raisonnement léger"),
            },
            [TaskCategory.Planning] = new[]
            {
                new KnownModelSpec("qwen3.5:8b", "8B", "5.2 Go", "Bon pour structurer des plans"),
                new KnownModelSpec("deepseek-r1:8b", "8B", "4.9 Go", "Planification réfléchie"),
            },
            [TaskCategory.Research] = new[]
            {
                new KnownModelSpec("qwen3.5:14b", "14B", "9 Go", "Synthèse et recherche"),
                new KnownModelSpec("llama3.3:70b", "70B", "40 Go", "Recherche approfondie"),
                new KnownModelSpec("qwen3.5:8b", "8B", "5.2 Go", "Recherche légère"),
            },
            [TaskCategory.Creative] = new[]
            {
                new KnownModelSpec("gemma3:12b", "12B", "8.1 Go", "Écriture créative multilingue"),
                new KnownModelSpec("qwen3.5:8b", "8B", "5.2 Go", "Créatif et français"),
            },
        };

    public static IReadOnlyList<KnownModelSpec> Get(TaskCategory category)
        => _byCategory.TryGetValue(category, out var specs) ? specs : _byCategory[TaskCategory.General];

    public static KnownModelSpec? Find(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return null;
        var baseName = modelName.Trim().Split(':')[0].ToLowerInvariant();
        foreach (var specs in _byCategory.Values)
        {
            var match = specs.FirstOrDefault(s =>
                string.Equals(s.Name.Split(':')[0], baseName, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return null;
    }
}
