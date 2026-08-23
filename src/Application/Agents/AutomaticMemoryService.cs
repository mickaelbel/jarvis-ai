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
}

public interface IAutomaticMemoryService
{
    Task<bool> ConsiderSaveAsync(string content, string? goal, MemoryType type = MemoryType.Knowledge, CancellationToken cancellationToken = default);
    Task SaveObservationAsync(string content, string? goal, MemoryType type, int importance, CancellationToken cancellationToken = default);
    int ComputeImportance(string content);
    MemoryType Categorize(string content);
    MemoryTier DecideTier(int importance);
    TimeSpan? DecideExpiration(int importance, MemoryTier tier);
    bool ShouldSave(string content, int importance);
}

public sealed class AutomaticMemoryService : IAutomaticMemoryService
{
    private static readonly string[] UserPreferenceKeywords =
    {
        "je préfère", "je prefere", "je préfèrerais", "préférence", "preference",
        "ne fais jamais", "ne fais pas", "toujours", "jamais", "souviens-toi", "n'oublie pas",
        "remember", "never", "always", "prefer", "don't forget", "do not forget"
    };

    private static readonly string[] ImportantKeywords =
    {
        "important", "urgent", "critique", "critical", "réunion", "reunion", "meeting",
        "rendez-vous", "deadline", "attention", "important:"
    };

    private readonly IMemoryService _memory;
    private readonly AutomaticMemoryOptions _options;
    private readonly ILogger<AutomaticMemoryService> _logger;

    public AutomaticMemoryService(
        IMemoryService memory,
        AutomaticMemoryOptions? options = null,
        ILogger<AutomaticMemoryService>? logger = null)
    {
        _memory = memory;
        _options = options ?? new AutomaticMemoryOptions();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AutomaticMemoryService>.Instance;
    }

    public async Task<bool> ConsiderSaveAsync(string content, string? goal, MemoryType type = MemoryType.Knowledge, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) return false;
        if (string.IsNullOrWhiteSpace(content)) return false;
        if (content.Length > _options.MaxContentLength)
            content = content[.._options.MaxContentLength];

        var importance = ComputeImportance(content);
        if (!ShouldSave(content, importance))
            return false;

        var key = BuildKey(content);
        var existing = await _memory.GetAsync(key, cancellationToken);
        if (existing is not null)
            return false;

        var category = type == MemoryType.ToolResult ? "tool_result" : "agent_observation";
        var tier = DecideTier(importance);
        var ttl = DecideExpiration(importance, tier);

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

    public async Task SaveObservationAsync(string content, string? goal, MemoryType type, int importance, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(content)) return;
        if (content.Length > _options.MaxContentLength)
            content = content[.._options.MaxContentLength];

        var key = BuildKey(content);
        var existing = await _memory.GetAsync(key, cancellationToken);
        if (existing is not null) return;

        var category = "agent_observation";
        var tier = DecideTier(importance);
        var ttl = DecideExpiration(importance, tier);

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
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AutomaticMemory] Failed to save observation");
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
        => tier == MemoryTier.LongTerm ? null : _options.ShortTermTtl;

    public bool ShouldSave(string content, int importance)
        => !string.IsNullOrWhiteSpace(content) && importance >= _options.MinImportanceToSave;

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
