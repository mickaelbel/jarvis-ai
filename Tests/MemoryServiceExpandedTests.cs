using JarvisAI.Application.Memory;
using JarvisAI.Infrastructure.Events;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public sealed class MemoryServiceExpandedTests
{
    private static (MemoryService service, InMemoryMemoryStore store) CreateSystem()
    {
        var store = new InMemoryMemoryStore();
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var service = new MemoryService(store, eventBus, NullLogger<MemoryService>.Instance);
        return (service, store);
    }

    [Fact]
    public async Task SaveMemoryAsync_stores_tier()
    {
        var (service, _) = CreateSystem();
        var entry = await service.SaveMemoryAsync("k", "c", MemoryType.Fact, "cat", tier: MemoryTier.Session);
        Assert.Equal(MemoryTier.Session, entry.Tier);

        var found = await service.GetAsync("k");
        Assert.Equal(MemoryTier.Session, found!.Tier);
    }

    [Fact]
    public async Task SaveMemoryAsync_stores_project()
    {
        var (service, _) = CreateSystem();
        await service.SaveMemoryAsync("k", "c", MemoryType.Fact, "cat", project: "proj");

        var results = await service.SearchAsync(new MemoryQuery { Project = "proj" });
        Assert.Single(results);
        Assert.Equal("proj", results[0].ProjectName);
    }

    [Fact]
    public async Task GetAsync_increments_access_count()
    {
        var (service, _) = CreateSystem();
        await service.SaveAsync("k", "c", MemoryType.Fact, "cat");
        await service.GetAsync("k");
        var second = await service.GetAsync("k");
        Assert.Equal(2, second!.AccessCount);
    }

    [Fact]
    public async Task GetAsync_updates_last_accessed()
    {
        var (service, _) = CreateSystem();
        await service.SaveAsync("k", "c", MemoryType.Fact, "cat");
        var entry = await service.GetAsync("k");
        Assert.NotNull(entry!.LastAccessedAt);
    }

    [Fact]
    public async Task SaveAsync_keeps_metadata()
    {
        var (service, _) = CreateSystem();
        var entry = await service.SaveAsync("k", "c", MemoryType.Fact, "cat",
            metadata: new Dictionary<string, string> { ["lang"] = "fr" });
        Assert.Equal("fr", entry.Metadata["lang"]);
    }

    [Fact]
    public async Task SearchSemanticAsync_returns_top_lexical_matches()
    {
        var (service, _) = CreateSystem();
        await service.SaveAsync("k1", "server uses postgresql database", MemoryType.Fact, "tech");
        await service.SaveAsync("k2", "user likes hiking on weekends", MemoryType.Fact, "perso");

        var results = await service.SearchSemanticAsync("postgresql database", limit: 5);
        Assert.Equal("k1", results[0].Key);
    }

    [Fact]
    public async Task SearchSemanticAsync_respects_tier_filter()
    {
        var (service, _) = CreateSystem();
        await service.SaveMemoryAsync("k1", "sql server stuff", MemoryType.Fact, "tech", tier: MemoryTier.Session);
        await service.SaveMemoryAsync("k2", "sql server stuff", MemoryType.Fact, "tech", tier: MemoryTier.LongTerm);

        var results = await service.SearchSemanticAsync("sql server", tier: MemoryTier.Session);
        Assert.Single(results);
        Assert.Equal("k1", results[0].Key);
    }

    [Fact]
    public async Task SearchSemanticAsync_respects_limit()
    {
        var (service, _) = CreateSystem();
        for (int i = 0; i < 5; i++)
            await service.SaveAsync($"k{i}", $"database server entry {i}", MemoryType.Fact, "tech");

        var results = await service.SearchSemanticAsync("database server", limit: 2);
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task BuildContextAsync_fills_all_scopes()
    {
        var (service, _) = CreateSystem();
        await service.SaveMemoryAsync("s1", "session note", MemoryType.Fact, "chat", tier: MemoryTier.Session);
        await service.SaveMemoryAsync("st1", "short note", MemoryType.Fact, "chat", tier: MemoryTier.ShortTerm);
        await service.SaveAsync("u1", "user info", MemoryType.UserPreference, MemoryCategories.User);
        await service.SaveAsync("l1", "long term fact", MemoryType.Fact, "general");

        var context = await service.BuildContextAsync("note");
        Assert.Single(context.SessionMemories);
        Assert.Single(context.ShortTermMemories);
        Assert.Single(context.UserMemories);
        Assert.Single(context.LongTermMemories);
        Assert.Empty(context.ProjectMemories);
    }

    [Fact]
    public async Task BuildContextAsync_includes_project_scope()
    {
        var (service, _) = CreateSystem();
        await service.SaveMemoryAsync("p1", "projet alpha note", MemoryType.Fact, MemoryCategories.Project, project: "alpha");

        var context = await service.BuildContextAsync("note", project: "alpha");
        Assert.Single(context.ProjectMemories);
        Assert.Equal("p1", context.ProjectMemories[0].Key);
    }

    [Fact]
    public async Task BuildContextAsync_limits_session_scope()
    {
        var (service, _) = CreateSystem();
        for (int i = 0; i < 8; i++)
            await service.SaveMemoryAsync($"s{i}", $"session note {i}", MemoryType.Fact, "chat", tier: MemoryTier.Session);

        var context = await service.BuildContextAsync("note", limitPerScope: 6);
        Assert.Equal(6, context.SessionMemories.Count);
    }

    [Fact]
    public async Task BuildContextAsync_excludes_user_from_long_term()
    {
        var (service, _) = CreateSystem();
        await service.SaveAsync("u1", "user preference coffee", MemoryType.UserPreference, MemoryCategories.User);
        await service.SaveAsync("l1", "documented fact about coffee", MemoryType.Fact, "general");

        var context = await service.BuildContextAsync("coffee");
        Assert.DoesNotContain(context.LongTermMemories, m => m.Key == "u1");
        Assert.Contains(context.LongTermMemories, m => m.Key == "l1");
    }
}
