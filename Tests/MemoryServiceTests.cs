using JarvisAI.Application.Memory;
using JarvisAI.Infrastructure.Events;
using JarvisAI.Domain.Events.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public class MemoryServiceTests
{
    private static (MemoryService service, InMemoryMemoryStore store) CreateSystem()
    {
        var store = new InMemoryMemoryStore();
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var service = new MemoryService(store, eventBus, NullLogger<MemoryService>.Instance);
        return (service, store);
    }

    [Fact]
    public async Task Save_and_retrieve_memory()
    {
        var (service, _) = CreateSystem();

        var saved = await service.SaveAsync("fav_color", "blue", MemoryType.UserPreference, "preferences", importance: 0.9f);
        Assert.Equal("fav_color", saved.Key);
        Assert.Equal("blue", saved.Content);

        var retrieved = await service.GetAsync("fav_color");
        Assert.NotNull(retrieved);
        Assert.Equal("blue", retrieved!.Content);
        Assert.Equal(MemoryType.UserPreference, retrieved.Type);
        Assert.Equal("preferences", retrieved.Category);
        Assert.Equal(0.9f, retrieved.Importance);
        Assert.Equal(1, retrieved.AccessCount);
    }

    [Fact]
    public async Task Retrieve_nonexistent_returns_null()
    {
        var (service, _) = CreateSystem();

        var result = await service.GetAsync("nonexistent_key");
        Assert.Null(result);
    }

    [Fact]
    public async Task Delete_memory()
    {
        var (service, _) = CreateSystem();

        await service.SaveAsync("to_delete", "some content", MemoryType.Fact, "temp");
        var deleted = await service.DeleteAsync("to_delete");
        Assert.True(deleted);

        var result = await service.GetAsync("to_delete");
        Assert.Null(result);
    }

    [Fact]
    public async Task Delete_nonexistent_returns_false()
    {
        var (service, _) = CreateSystem();
        var deleted = await service.DeleteAsync("nonexistent");
        Assert.False(deleted);
    }

    [Fact]
    public async Task Search_by_category()
    {
        var (service, _) = CreateSystem();

        await service.SaveAsync("k1", "content1", MemoryType.Fact, "work");
        await service.SaveAsync("k2", "content2", MemoryType.Fact, "personal");
        await service.SaveAsync("k3", "content3", MemoryType.Fact, "work");

        var results = await service.SearchAsync(new MemoryQuery { Category = "work" });
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal("work", r.Category));
    }

    [Fact]
    public async Task Search_by_text()
    {
        var (service, _) = CreateSystem();

        await service.SaveAsync("k1", "The quick brown fox", MemoryType.Fact, "animals");
        await service.SaveAsync("k2", "The lazy dog sleeps", MemoryType.Fact, "animals");
        await service.SaveAsync("k3", "The fast car runs", MemoryType.Fact, "vehicles");

        var results = await service.SearchAsync(new MemoryQuery { TextSearch = "fox" });
        Assert.Single(results);
        Assert.Contains("fox", results[0].Content);
    }

    [Fact]
    public async Task Search_by_type()
    {
        var (service, _) = CreateSystem();

        await service.SaveAsync("k1", "user said hello", MemoryType.Conversation, "chat");
        await service.SaveAsync("k2", "tool returned data", MemoryType.ToolResult, "system");
        await service.SaveAsync("k3", "user said goodbye", MemoryType.Conversation, "chat");

        var results = await service.SearchAsync(new MemoryQuery { Type = MemoryType.Conversation });
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(MemoryType.Conversation, r.Type));
    }

    [Fact]
    public async Task Search_with_limit()
    {
        var (service, _) = CreateSystem();

        for (int i = 0; i < 10; i++)
            await service.SaveAsync($"k{i}", $"content{i}", MemoryType.Fact, "test");

        var results = await service.SearchAsync(new MemoryQuery { Limit = 3 });
        Assert.Equal(3, results.Count);
    }

    [Fact]
    public async Task Upsert_updates_existing_memory()
    {
        var (service, _) = CreateSystem();

        await service.SaveAsync("key1", "original", MemoryType.Fact, "test");
        await service.SaveAsync("key1", "updated", MemoryType.Fact, "test");

        var result = await service.GetAsync("key1");
        Assert.NotNull(result);
        Assert.Equal("updated", result!.Content);
    }

    [Fact]
    public async Task Expired_memory_is_excluded_by_default()
    {
        var (service, _) = CreateSystem();

        await service.SaveAsync("active", "still here", MemoryType.Fact, "test");
        await service.SaveAsync("expired", "gone", MemoryType.Fact, "test", ttl: TimeSpan.FromMilliseconds(-1));

        var results = await service.SearchAsync(new MemoryQuery { IncludeExpired = false });
        Assert.Single(results);
        Assert.Equal("active", results[0].Key);
    }

    [Fact]
    public async Task Expired_memory_is_included_when_requested()
    {
        var (service, _) = CreateSystem();

        await service.SaveAsync("active", "still here", MemoryType.Fact, "test");
        await service.SaveAsync("expired", "gone", MemoryType.Fact, "test", ttl: TimeSpan.FromMilliseconds(-1));

        var results = await service.SearchAsync(new MemoryQuery { IncludeExpired = true });
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task Cleanup_removes_expired_entries()
    {
        var (service, _) = CreateSystem();

        await service.SaveAsync("active", "still here", MemoryType.Fact, "test");
        await service.SaveAsync("expired1", "gone1", MemoryType.Fact, "test", ttl: TimeSpan.FromMilliseconds(-1));
        await service.SaveAsync("expired2", "gone2", MemoryType.Fact, "test", ttl: TimeSpan.FromMilliseconds(-1));

        var cleaned = await service.CleanupExpiredAsync();
        Assert.Equal(2, cleaned);

        var remaining = await service.SearchAsync(new MemoryQuery());
        Assert.Single(remaining);
    }

    [Fact]
    public async Task GetContext_returns_recent_entries_for_category()
    {
        var (service, _) = CreateSystem();

        await service.SaveAsync("k1", "msg1", MemoryType.Conversation, "conversation");
        await service.SaveAsync("k2", "msg2", MemoryType.Conversation, "conversation");
        await service.SaveAsync("k3", "other", MemoryType.Fact, "other_category");

        var context = await service.GetContextAsync("conversation", maxEntries: 10);
        Assert.Equal(2, context.Count);
        Assert.All(context, r => Assert.Equal("conversation", r.Category));
    }

    [Fact]
    public async Task Save_with_ttl_sets_expiration()
    {
        var (service, _) = CreateSystem();

        var entry = await service.SaveAsync("ttl_key", "content", MemoryType.Fact, "test", ttl: TimeSpan.FromHours(1));
        Assert.NotNull(entry.ExpiresAt);
        Assert.True(entry.ExpiresAt > DateTime.UtcNow);
    }

    [Fact]
    public async Task Save_without_ttl_has_no_expiration()
    {
        var (service, _) = CreateSystem();

        var entry = await service.SaveAsync("no_ttl", "content", MemoryType.Fact, "test");
        Assert.Null(entry.ExpiresAt);
    }

    [Fact]
    public async Task Search_respects_min_importance()
    {
        var (service, _) = CreateSystem();

        await service.SaveAsync("low", "low importance", MemoryType.Fact, "test", importance: 0.2f);
        await service.SaveAsync("high", "high importance", MemoryType.Fact, "test", importance: 0.9f);

        var results = await service.SearchAsync(new MemoryQuery { MinImportance = 0.5f });
        Assert.Single(results);
        Assert.Equal("high", results[0].Key);
    }

    [Fact]
    public async Task Memory_events_are_published()
    {
        var store = new InMemoryMemoryStore();
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var publishedEvents = new List<object>();

        eventBus.Subscribe<MemoryCreatedEvent>((e, ct) =>
        {
            publishedEvents.Add(e);
            return Task.CompletedTask;
        });

        eventBus.Subscribe<MemoryRetrievedEvent>((e, ct) =>
        {
            publishedEvents.Add(e);
            return Task.CompletedTask;
        });

        var service = new MemoryService(store, eventBus, NullLogger<MemoryService>.Instance);

        await service.SaveAsync("key1", "content", MemoryType.Fact, "test");
        await service.GetAsync("key1");
        await service.GetAsync("nonexistent");

        Assert.Equal(3, publishedEvents.Count);
        Assert.IsType<MemoryCreatedEvent>(publishedEvents[0]);
        Assert.IsType<MemoryRetrievedEvent>(publishedEvents[1]);
        Assert.IsType<MemoryRetrievedEvent>(publishedEvents[2]);
    }
}
