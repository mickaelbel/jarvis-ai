using JarvisAI.Application.Abstractions;
using JarvisAI.Domain.Events.Memory;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Application.Memory;

public sealed class MemoryService : IMemoryService
{
    private readonly IMemoryStore _store;
    private readonly IEventBus _eventBus;
    private readonly ILogger<MemoryService> _logger;
    private readonly MemorySemanticScorer _scorer;

    public MemoryService(IMemoryStore store, IEventBus eventBus, ILogger<MemoryService> logger, IEmbeddingService? embeddings = null)
    {
        _store = store;
        _eventBus = eventBus;
        _logger = logger;
        _scorer = new MemorySemanticScorer(logger, embeddings);
    }

    public async Task<MemoryEntry> SaveAsync(string key, string content, MemoryType type, string category, float importance = 0.5f, TimeSpan? ttl = null, Dictionary<string, string>? metadata = null, CancellationToken cancellationToken = default)
    {
        return await SaveMemoryAsync(
            key, content, type, category, importance, MemoryTier.LongTerm, null, ttl, metadata, cancellationToken);
    }

    public async Task<MemoryEntry> SaveMemoryAsync(string key, string content, MemoryType type, string category, float importance = 0.5f, MemoryTier tier = MemoryTier.LongTerm, string? project = null, TimeSpan? ttl = null, Dictionary<string, string>? metadata = null, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[Memory] Saving: Key={Key}, Type={Type}, Category={Category}, Tier={Tier}, Project={Project}, Importance={Importance}",
            key, type, category, tier, project, importance);

        var entry = new MemoryEntry
        {
            Key = key,
            Content = content,
            Type = type,
            Category = category,
            Tier = tier,
            ProjectName = project ?? string.Empty,
            Importance = importance,
            Metadata = metadata ?? new(),
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = ttl.HasValue ? DateTime.UtcNow.Add(ttl.Value) : null
        };

        await _store.UpsertAsync(entry, cancellationToken);

        await _eventBus.PublishAsync(
            new MemoryCreatedEvent(key, category, entry.Id),
            cancellationToken);

        return entry;
    }

    public async Task<MemoryEntry?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("[Memory] Retrieving: Key={Key}", key);

        var entry = await _store.GetAsync(key, cancellationToken);

        if (entry is not null)
        {
            entry.LastAccessedAt = DateTime.UtcNow;
            entry.AccessCount++;

            await _eventBus.PublishAsync(
                new MemoryRetrievedEvent(key, true, entry.Id),
                cancellationToken);
        }
        else
        {
            await _eventBus.PublishAsync(
                new MemoryRetrievedEvent(key, false, Guid.NewGuid()),
                cancellationToken);
        }

        return entry;
    }

    public async Task<IReadOnlyList<MemoryEntry>> SearchAsync(MemoryQuery query, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("[Memory] Searching: Key={Key}, Category={Category}, Type={Type}, Tier={Tier}, Text={Text}",
            query.Key, query.Category, query.Type, query.Tier, query.TextSearch);

        return await _store.QueryAsync(
            query.Key, query.Category, query.Type, query.TextSearch,
            query.MinImportance, query.IncludeExpired, query.Limit, query.OrderByNewest,
            query.Tier, query.Project,
            cancellationToken);
    }

    public async Task<IReadOnlyList<MemoryEntry>> SearchSemanticAsync(string query, int limit = 10, MemoryTier? tier = null, string? category = null, string? project = null, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("[Memory] Semantic search: Query={Query}, Tier={Tier}, Category={Category}, Project={Project}",
            query, tier, category, project);

        var candidates = await _store.QueryAsync(
            key: null, category: category, type: null, textSearch: null,
            minImportance: null, includeExpired: false, limit: 200, orderByNewest: false,
            tier: tier, project: project,
            cancellationToken);

        var scored = await _scorer.ScoreAsync(query, candidates, top: limit, cancellationToken);
        return scored.Select(x => x.Entry).ToList();
    }

    public async Task<MemoryContext> BuildContextAsync(string query, string? project = null, int limitPerScope = 6, CancellationToken cancellationToken = default)
    {
        var sessionTask = _store.QueryAsync(
            null, null, null, null, null, false, limitPerScope, true, MemoryTier.Session, null, cancellationToken);

        var shortTermTask = _store.QueryAsync(
            null, null, null, null, null, false, limitPerScope, true, MemoryTier.ShortTerm, null, cancellationToken);

        var userTask = _store.QueryAsync(
            null, MemoryCategories.User, null, null, null, false, limitPerScope, true, null, null, cancellationToken);

        var longTermTask = _store.QueryAsync(
            null, null, null, null, null, false, 200, false, MemoryTier.LongTerm, null, cancellationToken);

        await Task.WhenAll(sessionTask, shortTermTask, userTask, longTermTask);

        var session = await sessionTask;
        var shortTerm = await shortTermTask;
        var user = await userTask;
        var longTermCandidates = (await longTermTask)
            .Where(x => x.Category != MemoryCategories.User && x.Category != MemoryCategories.Project)
            .ToList();
        var longTerm = await _scorer.ScoreAsync(query, longTermCandidates, top: limitPerScope, cancellationToken);

        IReadOnlyList<MemoryEntry> projectMemories = Array.Empty<MemoryEntry>();
        if (!string.IsNullOrWhiteSpace(project))
        {
            var projectCandidates = await _store.QueryAsync(
                null, MemoryCategories.Project, null, null, null, false, 200, false, null, project, cancellationToken);
            var scoredProject = await _scorer.ScoreAsync(query, projectCandidates, top: limitPerScope, cancellationToken);
            projectMemories = scoredProject.Select(x => x.Entry).ToList();
        }

        return new MemoryContext
        {
            SessionMemories = session,
            ShortTermMemories = shortTerm,
            LongTermMemories = longTerm.Select(x => x.Entry).ToList(),
            UserMemories = user,
            ProjectMemories = projectMemories
        };
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[Memory] Deleting: Key={Key}", key);
        return await _store.DeleteAsync(key, cancellationToken);
    }

    public async Task<int> CleanupExpiredAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[Memory] Cleaning up expired entries");
        return await _store.DeleteExpiredAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MemoryEntry>> GetContextAsync(string category, int maxEntries = 20, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("[Memory] Getting context: Category={Category}, Max={Max}", category, maxEntries);

        return await _store.QueryAsync(
            key: null, category: category, type: null, textSearch: null,
            minImportance: null, includeExpired: false, limit: maxEntries, orderByNewest: true,
            tier: null, project: null,
            cancellationToken);
    }
}
