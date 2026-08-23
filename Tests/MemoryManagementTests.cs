using JarvisAI.Application.Memory;
using JarvisAI.Domain.Events.Memory;
using JarvisAI.Infrastructure.Events;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

/// <summary>
/// Exercises the memory management flows surfaced by the Memory UI page:
/// search with category + text filters, importance scoring, and deletion.
/// </summary>
public class MemoryManagementTests
{
    private static (MemoryService service, InMemoryMemoryStore store, InMemoryEventBus eventBus) CreateSystem()
    {
        var store = new InMemoryMemoryStore();
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var service = new MemoryService(store, eventBus, NullLogger<MemoryService>.Instance);
        return (service, store, eventBus);
    }

    [Fact]
    public async Task Delete_removes_memory_from_search()
    {
        var (service, _, _) = CreateSystem();
        await service.SaveAsync("key_a", "alpha content", MemoryType.Fact, "notes", importance: 0.5f);
        await service.SaveAsync("key_b", "beta content", MemoryType.Fact, "notes", importance: 0.5f);

        var deleted = await service.DeleteAsync("key_a");
        Assert.True(deleted);

        var results = await service.SearchAsync(new MemoryQuery { Category = "notes", Limit = 10 });
        Assert.Single(results);
        Assert.Equal("key_b", results[0].Key);
    }

    [Fact]
    public async Task Delete_nonexistent_returns_false()
    {
        var (service, _, _) = CreateSystem();

        var deleted = await service.DeleteAsync("does_not_exist");

        Assert.False(deleted);
    }

    [Fact]
    public async Task Search_filters_by_category()
    {
        var (service, _, _) = CreateSystem();
        await service.SaveAsync("a", "content", MemoryType.Fact, "work", importance: 0.5f);
        await service.SaveAsync("b", "content", MemoryType.Fact, "personal", importance: 0.5f);

        var results = await service.SearchAsync(new MemoryQuery { Category = "work", Limit = 10 });

        Assert.Single(results);
        Assert.Equal("a", results[0].Key);
    }

    [Fact]
    public async Task Search_filters_by_text()
    {
        var (service, _, _) = CreateSystem();
        await service.SaveAsync("a", "the quick brown fox", MemoryType.Fact, "notes");
        await service.SaveAsync("b", "a completely different thing", MemoryType.Fact, "notes");

        var results = await service.SearchAsync(new MemoryQuery { TextSearch = "fox", Limit = 10 });

        Assert.Single(results);
        Assert.Equal("a", results[0].Key);
    }

    [Fact]
    public async Task Search_orders_by_newest_first()
    {
        var (service, _, _) = CreateSystem();
        await service.SaveAsync("first", "old", MemoryType.Fact, "notes");
        await Task.Delay(5);
        await service.SaveAsync("second", "new", MemoryType.Fact, "notes");

        var results = await service.SearchAsync(new MemoryQuery { Limit = 10, OrderByNewest = true });

        Assert.Equal("second", results[0].Key);
        Assert.Equal("first", results[1].Key);
    }

    [Fact]
    public async Task Importance_scores_are_preserved_and_queryable()
    {
        var (service, _, _) = CreateSystem();
        await service.SaveAsync("important", "critical info", MemoryType.Fact, "notes", importance: 0.9f);
        await service.SaveAsync("trivial", "whatever", MemoryType.Fact, "notes", importance: 0.1f);

        var results = await service.SearchAsync(new MemoryQuery { MinImportance = 0.5f, Limit = 10 });

        Assert.Single(results);
        Assert.Equal("important", results[0].Key);
        Assert.Equal(0.9f, results[0].Importance);
    }

    [Fact]
    public async Task Save_publishes_memory_created_event()
    {
        var (service, _, eventBus) = CreateSystem();

        var raised = false;
        using var sub = eventBus.Subscribe<MemoryCreatedEvent>((_, _) => { raised = true; return Task.CompletedTask; });
        await service.SaveAsync("evt", "content", MemoryType.Fact, "notes");

        Assert.True(raised);
    }

    [Fact]
    public async Task Get_publishes_memory_retrieved_event()
    {
        var (service, _, eventBus) = CreateSystem();
        await service.SaveAsync("got", "content", MemoryType.Fact, "notes");

        var raised = false;
        using var sub = eventBus.Subscribe<MemoryRetrievedEvent>((_, _) => { raised = true; return Task.CompletedTask; });
        await service.GetAsync("got");

        Assert.True(raised);
    }

    [Fact]
    public async Task Total_count_reflects_stored_memories()
    {
        var (service, _, _) = CreateSystem();
        await service.SaveAsync("one", "1", MemoryType.Fact, "notes");
        await service.SaveAsync("two", "2", MemoryType.Fact, "notes");
        await service.SaveAsync("three", "3", MemoryType.Fact, "notes");

        var all = await service.SearchAsync(new MemoryQuery { Limit = 100 });

        Assert.Equal(3, all.Count);
    }
}
