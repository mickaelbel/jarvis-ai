using JarvisAI.Application.Memory;
using JarvisAI.Infrastructure.Events;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public sealed class MemoryTierTests
{
    private static (MemoryService service, InMemoryMemoryStore store) CreateSystem()
    {
        var store = new InMemoryMemoryStore();
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var service = new MemoryService(store, eventBus, NullLogger<MemoryService>.Instance);
        return (service, store);
    }

    [Fact]
    public async Task SaveMemoryAsync_SetsTierAndProject()
    {
        var (service, _) = CreateSystem();

        var saved = await service.SaveMemoryAsync(
            "project.concept", "le projet vise un assistant autonome",
            MemoryType.Knowledge, MemoryCategories.Project, tier: MemoryTier.LongTerm, project: "jarvis-ai");

        Assert.Equal(MemoryTier.LongTerm, saved.Tier);
        Assert.Equal("jarvis-ai", saved.ProjectName);
    }

    [Fact]
    public async Task SaveAsync_DefaultsToLongTerm_NoProject()
    {
        var (service, _) = CreateSystem();

        var saved = await service.SaveAsync("pref", "value", MemoryType.UserPreference, "preferences");

        Assert.Equal(MemoryTier.LongTerm, saved.Tier);
        Assert.Equal(string.Empty, saved.ProjectName);
    }

    [Fact]
    public async Task SearchSemanticAsync_FiltersByTier()
    {
        var (service, _) = CreateSystem();
        await service.SaveMemoryAsync("s1", "session note about weather", MemoryType.Knowledge, "notes", tier: MemoryTier.Session, ttl: TimeSpan.FromMinutes(30));
        await service.SaveMemoryAsync("l1", "long term fact about weather", MemoryType.Knowledge, "facts", tier: MemoryTier.LongTerm);

        var sessionResults = await service.SearchSemanticAsync("weather", tier: MemoryTier.Session);
        var longTermResults = await service.SearchSemanticAsync("weather", tier: MemoryTier.LongTerm);

        Assert.Single(sessionResults);
        Assert.Equal("s1", sessionResults[0].Key);
        Assert.Single(longTermResults);
        Assert.Equal("l1", longTermResults[0].Key);
    }

    [Fact]
    public async Task SearchSemanticAsync_FiltersByProject()
    {
        var (service, _) = CreateSystem();
        await service.SaveMemoryAsync("p1", "architecture en couches propres", MemoryType.Knowledge, MemoryCategories.Project, project: "jarvis-ai");
        await service.SaveMemoryAsync("o1", "architecture en couches propres", MemoryType.Knowledge, "other", project: "autre-projet");

        var results = await service.SearchSemanticAsync("architecture", project: "jarvis-ai");

        Assert.Single(results);
        Assert.Equal("p1", results[0].Key);
    }

    [Fact]
    public async Task BuildContextAsync_ReturnsAllScopes()
    {
        var (service, _) = CreateSystem();
        await service.SaveMemoryAsync("session1", "l'utilisateur parle de docker", MemoryType.Conversation, "session", tier: MemoryTier.Session, ttl: TimeSpan.FromMinutes(30));
        await service.SaveMemoryAsync("short1", "tâche récente sur l'api", MemoryType.Fact, "tasks", tier: MemoryTier.ShortTerm, ttl: TimeSpan.FromHours(4));
        await service.SaveMemoryAsync("long1", "le serveur tourne sous linux", MemoryType.Knowledge, "facts", tier: MemoryTier.LongTerm);
        await service.SaveMemoryAsync("user1", "l'utilisateur s'appelle Alex", MemoryType.UserPreference, MemoryCategories.User);
        await service.SaveMemoryAsync("proj1", "le dépôt est sur github", MemoryType.Knowledge, MemoryCategories.Project, project: "jarvis-ai");

        var context = await service.BuildContextAsync("serveur docker", project: "jarvis-ai");

        Assert.Single(context.SessionMemories);
        Assert.Single(context.ShortTermMemories);
        Assert.Single(context.UserMemories);
        Assert.Single(context.ProjectMemories);
        Assert.Single(context.LongTermMemories);
        Assert.True(context.TotalCount >= 5);
        Assert.Contains("[SESSION", context.Render());
        Assert.Contains("[USER", context.Render());
        Assert.Contains("[PROJECT", context.Render());
    }

    [Fact]
    public async Task BuildContextAsync_WithoutProject_ExcludesProjectScope()
    {
        var (service, _) = CreateSystem();
        await service.SaveMemoryAsync("proj1", "note de projet", MemoryType.Knowledge, MemoryCategories.Project, project: "jarvis-ai");

        var context = await service.BuildContextAsync("note");

        Assert.Empty(context.ProjectMemories);
        Assert.DoesNotContain("[PROJECT", context.Render());
    }

    [Fact]
    public async Task SearchSemanticAsync_EmptyStore_ReturnsEmpty()
    {
        var (service, _) = CreateSystem();

        var results = await service.SearchSemanticAsync("anything");

        Assert.Empty(results);
    }
}
