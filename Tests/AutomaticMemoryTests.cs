using JarvisAI.Application.Agents;
using JarvisAI.Application.Memory;
using JarvisAI.Infrastructure.Events;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public class AutomaticMemoryTests
{
    private static (AutomaticMemoryService service, IMemoryService memory) Create(InMemoryMemoryStore? store = null)
    {
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var memory = new MemoryService(store ?? new InMemoryMemoryStore(), eventBus, NullLogger<MemoryService>.Instance);
        return (new AutomaticMemoryService(memory, new AutomaticMemoryOptions(), NullLogger<AutomaticMemoryService>.Instance), memory);
    }

    [Fact]
    public void ComputeImportance_zero_for_empty()
    {
        var (service, _) = Create();
        Assert.Equal(0, service.ComputeImportance(string.Empty));
        Assert.Equal(0, service.ComputeImportance("   "));
    }

    [Fact]
    public void ComputeImportance_preference_keyword_adds_points()
    {
        var (service, _) = Create();
        Assert.Equal(4, service.ComputeImportance("je préfère le café noir"));
    }

    [Fact]
    public void ComputeImportance_important_keyword_adds_points()
    {
        var (service, _) = Create();
        Assert.True(service.ComputeImportance("Ceci est important: finaliser le rapport") >= 3);
    }

    [Fact]
    public void ComputeImportance_long_content_adds_point()
    {
        var (service, _) = Create();
        var longText = new string('a', 150);
        Assert.Equal(1, service.ComputeImportance(longText));
    }

    [Fact]
    public void ComputeImportance_colon_adds_point()
    {
        var (service, _) = Create();
        Assert.True(service.ComputeImportance("note: rien de spécial") >= 1);
    }

    [Fact]
    public void ComputeImportance_is_capped_at_ten()
    {
        var (service, _) = Create();
        var content = "je préfère ne jamais oublier ceci qui est très important: " + new string('x', 300);
        Assert.True(service.ComputeImportance(content) <= 10);
    }

    [Fact]
    public void ShouldSave_respects_minimum_importance()
    {
        var (service, _) = Create();
        Assert.False(service.ShouldSave("short text", 1));
        Assert.True(service.ShouldSave("important: something", 4));
    }

    [Fact]
    public void Categorize_returns_user_preference()
    {
        var (service, _) = Create();
        Assert.Equal(MemoryType.UserPreference, service.Categorize("je préfère utiliser vim"));
    }

    [Fact]
    public void Categorize_returns_tool_result()
    {
        var (service, _) = Create();
        Assert.Equal(MemoryType.ToolResult, service.Categorize("the tool returned 42"));
    }

    [Fact]
    public void Categorize_returns_fact_by_default()
    {
        var (service, _) = Create();
        Assert.Equal(MemoryType.Fact, service.Categorize("l'utilisateur travaille sur Windows"));
    }

    [Fact]
    public void DecideTier_long_term_above_threshold()
    {
        var (service, _) = Create();
        Assert.Equal(MemoryTier.LongTerm, service.DecideTier(7));
        Assert.Equal(MemoryTier.LongTerm, service.DecideTier(10));
        Assert.Equal(MemoryTier.ShortTerm, service.DecideTier(6));
        Assert.Equal(MemoryTier.ShortTerm, service.DecideTier(0));
    }

    [Fact]
    public void DecideExpiration_null_for_long_term()
    {
        var (service, _) = Create();
        Assert.Null(service.DecideExpiration(7, MemoryTier.LongTerm));
        Assert.NotNull(service.DecideExpiration(3, MemoryTier.ShortTerm));
    }

    [Fact]
    public async Task ConsiderSaveAsync_saves_high_importance_content()
    {
        var (service, memory) = Create();
        var saved = await service.ConsiderSaveAsync("Important: le rapport est dû vendredi", "project");
        Assert.True(saved);
        var entries = await memory.SearchAsync(new MemoryQuery { TextSearch = "rapport" });
        Assert.Single(entries);
    }

    [Fact]
    public async Task ConsiderSaveAsync_skips_low_importance_content()
    {
        var (service, memory) = Create();
        var saved = await service.ConsiderSaveAsync("petit mot rapide", null);
        Assert.False(saved);
        var entries = await memory.SearchAsync(new MemoryQuery { TextSearch = "mot" });
        Assert.Empty(entries);
    }

    [Fact]
    public async Task ConsiderSaveAsync_skips_empty_content()
    {
        var (service, memory) = Create();
        Assert.False(await service.ConsiderSaveAsync("", null));
        Assert.False(await service.ConsiderSaveAsync(null!, null));
        var all = await memory.SearchAsync(new MemoryQuery());
        Assert.Empty(all);
    }

    [Fact]
    public async Task ConsiderSaveAsync_respects_disabled_option()
    {
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var memory = new MemoryService(new InMemoryMemoryStore(), eventBus, NullLogger<MemoryService>.Instance);
        var service = new AutomaticMemoryService(memory, new AutomaticMemoryOptions { Enabled = false }, NullLogger<AutomaticMemoryService>.Instance);

        Assert.False(await service.ConsiderSaveAsync("Important: quelque chose", null));
        var all = await memory.SearchAsync(new MemoryQuery());
        Assert.Empty(all);
    }

    [Fact]
    public async Task ConsiderSaveAsync_deduplicates_exact_content()
    {
        var (service, memory) = Create();
        var first = await service.ConsiderSaveAsync("Important: note unique du matin", null);
        var second = await service.ConsiderSaveAsync("Important: note unique du matin", null);

        Assert.True(first);
        Assert.False(second);
        var entries = await memory.SearchAsync(new MemoryQuery { TextSearch = "note" });
        Assert.Single(entries);
    }

    [Fact]
    public async Task SaveObservationAsync_stores_content()
    {
        var (service, memory) = Create();
        await service.SaveObservationAsync("Important: observation à garder", "goal", MemoryType.Fact, 5);
        var entries = await memory.SearchAsync(new MemoryQuery { TextSearch = "observation" });
        Assert.Single(entries);
    }

    [Fact]
    public async Task SaveObservationAsync_noop_when_disabled()
    {
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var memory = new MemoryService(new InMemoryMemoryStore(), eventBus, NullLogger<MemoryService>.Instance);
        var service = new AutomaticMemoryService(memory, new AutomaticMemoryOptions { Enabled = false }, NullLogger<AutomaticMemoryService>.Instance);

        await service.SaveObservationAsync("Important: observation", null, MemoryType.Fact, 8);
        var all = await memory.SearchAsync(new MemoryQuery());
        Assert.Empty(all);
    }

    [Fact]
    public async Task ConsiderSaveAsync_truncates_very_long_content()
    {
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var memory = new MemoryService(new InMemoryMemoryStore(), eventBus, NullLogger<MemoryService>.Instance);
        var service = new AutomaticMemoryService(memory, new AutomaticMemoryOptions { MaxContentLength = 100 }, NullLogger<AutomaticMemoryService>.Instance);

        var longContent = "Important: " + new string('x', 500);
        await service.ConsiderSaveAsync(longContent, null);
        var entries = await memory.SearchAsync(new MemoryQuery { TextSearch = "Important" });
        Assert.Single(entries);
        Assert.True(entries[0].Content.Length <= 100);
    }

    [Fact]
    public async Task ConsiderSaveAsync_key_is_stable_and_prefixed()
    {
        var (service, memory) = Create();
        await service.ConsiderSaveAsync("Important: contenu stable", null);
        var entries = await memory.SearchAsync(new MemoryQuery { TextSearch = "contenu" });
        Assert.Single(entries);
        Assert.StartsWith("agent.", entries[0].Key);
    }
}
