using JarvisAI.Application.Memory;

namespace JarvisAI.Tests;

public class EpisodicMemoryTests
{
    [Fact]
    public async Task Record_SauvegardeUnEpisodeHorodate()
    {
        var store = new FakeMemoryStore();
        var svc = new EpisodicMemoryService(store);

        await svc.RecordAsync("Quel temps fera-t-il demain ?", "Il devrait pleuvoir.", "chat");

        var entry = Assert.Single(store.Saved);
        Assert.Equal("episodic", entry.Category);
        Assert.Equal(MemoryType.Conversation, entry.Type);
        Assert.Equal(MemoryTier.ShortTerm, entry.Tier);
        Assert.StartsWith("episodic.", entry.Key);
        Assert.Contains("Quel temps fera-t-il demain ?", entry.Content);
        Assert.Contains("Il devrait pleuvoir.", entry.Content);
        Assert.Equal("chat", entry.Metadata["source"]);
    }

    [Fact]
    public async Task Record_IgnoreLesReponsesVidesOuErreurs()
    {
        var store = new FakeMemoryStore();
        var svc = new EpisodicMemoryService(store);

        await svc.RecordAsync("", "réponse", "chat");
        await svc.RecordAsync("question", "", "chat");
        await svc.RecordAsync("question", "Error: modèle absent", "chat");

        Assert.Empty(store.Saved);
    }

    [Fact]
    public async Task Recall_ConstruitUnBlocAvecHorodatage()
    {
        var store = new FakeMemoryStore();
        store.SearchResults = new List<MemoryEntry>
        {
            new()
            {
                Key = "episodic.1", Category = "episodic",
                Content = "où sont les clés de voiture\n→ Sur le bureau du salon",
                CreatedAt = DateTime.UtcNow.AddHours(-3)
            },
            new()
            {
                Key = "episodic.2", Category = "episodic",
                Content = "ancien souvenir hors fenêtre",
                CreatedAt = DateTime.UtcNow.AddDays(-60)
            }
        };
        var svc = new EpisodicMemoryService(store);

        var block = await svc.RecallBlockAsync("clés de voiture");

        Assert.NotEmpty(block);
        Assert.Contains("Souvenirs d'échanges passés", block);
        Assert.Contains("aujourd'hui", block);          // épisode récent horodaté
        Assert.DoesNotContain("hors fenêtre", block);   // épisode > 30 jours exclu
    }

    [Fact]
    public async Task Recall_RetourneVideSiAucuneRecherchePossible()
    {
        var store = new FakeMemoryStore { ThrowOnSearch = true };
        var svc = new EpisodicMemoryService(store);

        var block = await svc.RecallBlockAsync("n'importe quoi");

        Assert.Equal("", block);
    }

    private sealed class FakeMemoryStore : IMemoryService
    {
        public List<MemoryEntry> Saved { get; } = new();
        public List<MemoryEntry> SearchResults { get; set; } = new();
        public bool ThrowOnSearch { get; set; }

        public Task<MemoryEntry> SaveAsync(string key, string content, MemoryType type, string category, float importance = 0.5f, TimeSpan? ttl = null, Dictionary<string, string>? metadata = null, CancellationToken cancellationToken = default)
            => SaveMemoryAsync(key, content, type, category, importance, MemoryTier.LongTerm, null, ttl, metadata, cancellationToken);

        public Task<MemoryEntry> SaveMemoryAsync(string key, string content, MemoryType type, string category, float importance = 0.5f, MemoryTier tier = MemoryTier.LongTerm, string? project = null, TimeSpan? ttl = null, Dictionary<string, string>? metadata = null, CancellationToken cancellationToken = default)
        {
            var e = new MemoryEntry { Key = key, Content = content, Type = type, Category = category, Importance = importance, Tier = tier, Metadata = metadata ?? new() };
            Saved.Add(e);
            return Task.FromResult(e);
        }

        public Task<MemoryEntry?> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<MemoryEntry?>(null);

        public Task<IReadOnlyList<MemoryEntry>> SearchAsync(MemoryQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<MemoryEntry>>(SearchResults);

        public Task<IReadOnlyList<MemoryEntry>> SearchSemanticAsync(string query, int limit = 10, MemoryTier? tier = null, string? category = null, string? project = null, CancellationToken cancellationToken = default)
        {
            if (ThrowOnSearch) throw new InvalidOperationException("store down");
            return Task.FromResult<IReadOnlyList<MemoryEntry>>(SearchResults.Where(e => category is null || e.Category == category).ToList());
        }

        public Task<MemoryContext> BuildContextAsync(string query, string? project = null, int limitPerScope = 6, CancellationToken cancellationToken = default)
            => Task.FromResult(new MemoryContext());

        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<int> CleanupExpiredAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);

        public Task<IReadOnlyList<MemoryEntry>> GetContextAsync(string category, int maxEntries = 20, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<MemoryEntry>>(Array.Empty<MemoryEntry>());
    }
}
