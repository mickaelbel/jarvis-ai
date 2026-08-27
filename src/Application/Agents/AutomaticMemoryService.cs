using JarvisAI.Application.Memory;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;

namespace JarvisAI.Application.Agents;

public sealed class AutomaticMemoryOptions
{
    public int MinImportanceToSave { get; init; } = 4;
    public TimeSpan ShortTermTtl { get; init; } = TimeSpan.FromDays(30);
    public int MaxContentLength { get; init; } = 2000;
    public bool Enabled { get; init; } = true;

    public TimeSpan ShortTermTtlShort { get; init; } = TimeSpan.FromDays(7);
    public TimeSpan ShortTermTtlMedium { get; init; } = TimeSpan.FromDays(30);
    public TimeSpan ShortTermTtlLong { get; init; } = TimeSpan.FromDays(90);
}

public interface IAutomaticMemoryService
{
    /// <summary>Décide si un contenu est suffisamment durable pour la mémoire long terme.</summary>
    bool IsDurable(string content, MemoryType type, string category);

    /// <summary>Calcule le couple (tier, ttl) selon l'importance, la durabilité et la catégorie.</summary>
    (MemoryTier Tier, TimeSpan? Ttl) DecideStorage(int importance, string content, MemoryType type, string category);

    Task<bool> ConsiderSaveAsync(string content, string? goal, MemoryType type = MemoryType.Knowledge, CancellationToken cancellationToken = default);
    Task SaveObservationAsync(string content, string? goal, MemoryType type, int importance, CancellationToken cancellationToken = default);
    int ComputeImportance(string content);
    MemoryType Categorize(string content);
    MemoryTier DecideTier(int importance);
    TimeSpan? DecideExpiration(int importance, MemoryTier tier);
    bool ShouldSave(string content, int importance);

    /// <summary>
    /// Source de vérité d'activation de la sauvegarde mémoire : <see langword="false"/>
    /// quand « Mémoire activée » est désactivé (ou verrouillé par la configuration).
    /// Les points d'écriture (automatique et outils mémoire) l'utilisent pour bloquer
    /// toute nouvelle sauvegarde ; la lecture/recherche/modification ne passent pas par ici.
    /// </summary>
    bool IsSavingEnabled();
}

public sealed class AutomaticMemoryService : IAutomaticMemoryService
{
    private static readonly string[] UserPreferenceKeywords =
    {
        "je préfère", "je prefere", "je préfèrerais", "préférence", "preference",
        "ne fais jamais", "ne fais pas", "toujours", "jamais", "souviens-toi", "n'oublie pas",
        "remember", "never", "always", "prefer", "don't forget", "do not forget"
    };

    private static readonly string[] DurableKeywords =
    {
        "retiens", "souviens-toi", "n'oublie pas", "remember", "mémorise",
        "je préfère", "je prefere", "préférence", "preference", "toujours", "jamais", "never", "always"
    };

    private static readonly string[] ImportantKeywords =
    {
        "important", "urgent", "critique", "critical", "réunion", "reunion", "meeting",
        "rendez-vous", "deadline", "attention", "important:"
    };

    private static readonly string[] NoiseMarkers =
    {
        "merci", "bonjour", "salut", "ok", "d'accord", "génial", "super bien",
        "hello", "hi", "thanks", "thank you", "okay"
    };

    // Marqueur métadonnée qui empêche la consolidation de reléguer un souvenir
    // volontairement épinglé par l'utilisateur.
    public const string PinnedMetadataKey = "pinned";

    private readonly IMemoryService _memory;
    private readonly AutomaticMemoryOptions _options;
    private readonly ILogger<AutomaticMemoryService> _logger;
    private readonly IMemorySettingsStore? _settingsStore;

    public AutomaticMemoryService(
        IMemoryService memory,
        AutomaticMemoryOptions? options = null,
        ILogger<AutomaticMemoryService>? logger = null,
        IMemorySettingsStore? settingsStore = null)
    {
        _memory = memory;
        _options = options ?? new AutomaticMemoryOptions();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AutomaticMemoryService>.Instance;
        _settingsStore = settingsStore;
    }

    /// <summary>
    /// Décision unique d'activation de la sauvegarde automatique. Le toggle UI
    /// (<see cref="MemorySettings.MemoryEnabled"/>, persisté via
    /// <see cref="IMemorySettingsStore"/>) est la source de vérité à l'exécution ;
    /// <see cref="AutomaticMemoryOptions.Enabled"/> fournit un verrouillage au
    /// niveau configuration (défaut : activé).
    /// </summary>
    private bool IsAutomaticSavingEnabled()
    {
        if (!_options.Enabled) return false;

        // Le toggle UI est la source de vérité à l'exécution. S'il est absent
        // (ex. tests sans store), on ne le consulte pas.
        if (_settingsStore is not null && _settingsStore.Get() is { MemoryEnabled: false })
            return false;

        return true;
    }

    /// <summary>Source de vérité d'activation pour toute nouvelle sauvegarde mémoire.</summary>
    public bool IsSavingEnabled() => IsAutomaticSavingEnabled();

    public async Task<bool> ConsiderSaveAsync(string content, string? goal, MemoryType type = MemoryType.Knowledge, CancellationToken cancellationToken = default)
    {
        if (!IsAutomaticSavingEnabled()) return false;
        if (string.IsNullOrWhiteSpace(content)) return false;
        if (content.Length > _options.MaxContentLength)
            content = content[.._options.MaxContentLength];

        var importance = ComputeImportance(content);
        if (!ShouldSave(content, importance))
            return false;

        var category = type == MemoryType.ToolResult ? "tool_result" : "agent_observation";
        var (tier, ttl) = DecideStorage(importance, content, type, category);

        return await SaveIfNewAsync(content, type, category, importance, tier, ttl, goal, cancellationToken);
    }

    public async Task SaveObservationAsync(string content, string? goal, MemoryType type, int importance, CancellationToken cancellationToken = default)
    {
        if (!IsAutomaticSavingEnabled() || string.IsNullOrWhiteSpace(content)) return;
        if (content.Length > _options.MaxContentLength)
            content = content[.._options.MaxContentLength];

        var category = "agent_observation";
        var (tier, ttl) = DecideStorage(importance, content, type, category);

        await SaveIfNewAsync(content, type, category, importance, tier, ttl, goal, cancellationToken);
    }

    private async Task<bool> SaveIfNewAsync(
        string content, MemoryType type, string category, int importance,
        MemoryTier tier, TimeSpan? ttl, string? goal, CancellationToken cancellationToken)
    {
        var key = BuildKey(content);
        var existing = await _memory.GetAsync(key, cancellationToken);
        if (existing is not null)
            return false;

        try
        {
            await _memory.SaveMemoryAsync(
                key, content, type, category,
                importance: Math.Clamp(importance / 10f, 0f, 1f),
                tier: tier,
                project: goal is null ? null : Truncate(goal, 120),
                ttl: ttl,
                metadata: new Dictionary<string, string> { ["agent"] = "true" },
                cancellationToken: cancellationToken);

            _logger.LogDebug("[AutomaticMemory] Saved memory (importance={Importance}, tier={Tier})", importance, tier);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AutomaticMemory] Failed to save memory");
            return false;
        }
    }

    public int ComputeImportance(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return 0;

        var normalized = content.ToLowerInvariant();
        var importance = 0;

        if (UserPreferenceKeywords.Any(normalized.Contains))
            importance += 4;
        if (ImportantKeywords.Any(normalized.Contains))
            importance += 3;
        if (content.Length > 120)
            importance += 1;
        if (content.Contains(':'))
            importance += 1;

        return Math.Clamp(importance, 0, 10);
    }

    public MemoryType Categorize(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return MemoryType.Knowledge;
        var normalized = content.ToLowerInvariant();

        if (UserPreferenceKeywords.Any(normalized.Contains))
            return MemoryType.UserPreference;
        if (normalized.Contains("tool"))
            return MemoryType.ToolResult;

        return MemoryType.Fact;
    }

    public MemoryTier DecideTier(int importance)
        => importance >= 7 ? MemoryTier.LongTerm : MemoryTier.ShortTerm;

    public TimeSpan? DecideExpiration(int importance, MemoryTier tier)
        => tier == MemoryTier.LongTerm ? null : AdaptiveTtl(importance);

    /// <summary>
    /// Filtre strict : un souvenir ne va en long terme (sans expiration) que s'il est
    /// réellement durable — porte une mention explicite « retiens/souviens-toi », une
    /// préférence, ou une catégorie durable (préférences, proches, projets, faits) avec
    /// un minimum d'importance. Tout le reste part en court terme avec expiration.
    /// </summary>
    public bool IsDurable(string content, MemoryType type, string category)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;

        var normalized = content.ToLowerInvariant();
        var isDurableCategory = category.Equals("preferences", StringComparison.OrdinalIgnoreCase)
            || category.Equals("personne", StringComparison.OrdinalIgnoreCase)
            || category.Equals("projet", StringComparison.OrdinalIgnoreCase)
            || category.Equals(MemoryCategories.Project, StringComparison.OrdinalIgnoreCase)
            || category.Equals(MemoryCategories.User, StringComparison.OrdinalIgnoreCase);

        var isExplicit = DurableKeywords.Any(normalized.Contains)
            || type == MemoryType.UserPreference;

        // Une préférence/proche/projet déclaré est durable par nature.
        return isExplicit || isDurableCategory;
    }

    /// <summary>
    /// Calcule le couple (tier, ttl) final pour un stockage : long terme (sans expiration)
    /// si durable, sinon court terme avec une expiration adaptée au contexte (7/30/90 jours).
    /// </summary>
    public (MemoryTier Tier, TimeSpan? Ttl) DecideStorage(int importance, string content, MemoryType type, string category)
    {
        if (IsDurable(content, type, category) || importance >= 7)
            return (MemoryTier.LongTerm, null);

        return (MemoryTier.ShortTerm, AdaptiveTtl(importance));
    }

    public bool ShouldSave(string content, int importance)
        => !string.IsNullOrWhiteSpace(content)
           && importance >= _options.MinImportanceToSave
           && !IsNoise(content);

    /// <summary>
    /// Anti-bruit : refuse de mémoriser les échanges triviaux (salutations, politesses,
    /// réponses sans consistance) qui n'ont aucune utilité future.
    /// </summary>
    private static bool IsNoise(string content)
    {
        var trimmed = content.Trim();
        var normalized = trimmed.ToLowerInvariant();

        // Trop court pour porter un sens durable (sauf mention explicite, déjà couvert).
        if (trimmed.Length < 12)
            return true;

        // Salutations / politesses isolées.
        if (NoiseMarkers.Any(m => normalized.StartsWith(m, StringComparison.Ordinal)))
            return true;

        return false;
    }

    private TimeSpan AdaptiveTtl(int importance)
        => importance >= 6 ? _options.ShortTermTtlLong
         : importance >= 4 ? _options.ShortTermTtlMedium
         : _options.ShortTermTtlShort;

    private static string BuildKey(string content)
    {
        var normalized = string.Join(' ', content.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        var hex = Convert.ToHexString(bytes).ToLowerInvariant();
        return $"agent.{hex[..16]}";
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..(maxLength - 3)] + "...";
}
