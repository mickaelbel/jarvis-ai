using JarvisAI.Application.Agents;
using JarvisAI.Application.Memory;
using JarvisAI.Application.Tools;
using JarvisAI.Infrastructure.Events;
using JarvisAI.Infrastructure.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

/// <summary>
/// Le toggle UI « Mémoire activée » (MemorySettings.MemoryEnabled, source de vérité
/// exposée par IMemorySettingsStore) doit bloquer TOUTE nouvelle sauvegarde mémoire
/// (automatique comme explicite/outil), tout en laissant disponibles la lecture,
/// la recherche, la modification et la suppression.
/// </summary>
public class AutomaticMemoryDisableTests
{
    private static IMemoryService CreateMemory() =>
        new MemoryService(new InMemoryMemoryStore(), new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance), NullLogger<MemoryService>.Instance);

    private static AutomaticMemoryService CreateService(IMemoryService memory, IMemorySettingsStore? settings)
        => new(memory, new AutomaticMemoryOptions(), NullLogger<AutomaticMemoryService>.Instance, settings);

    [Fact]
    public async Task ConsiderSaveAsync_skips_when_ui_toggle_off()
    {
        var memory = CreateMemory();
        var service = CreateService(memory, new FakeSettingsStore { MemoryEnabled = false });

        Assert.False(await service.ConsiderSaveAsync("Important: quelque chose à retenir", null));
        Assert.Empty(await memory.SearchAsync(new MemoryQuery()));
    }

    [Fact]
    public async Task ConsiderSaveAsync_saves_when_ui_toggle_on()
    {
        var memory = CreateMemory();
        var service = CreateService(memory, new FakeSettingsStore { MemoryEnabled = true });

        Assert.True(await service.ConsiderSaveAsync("Important: quelque chose à retenir", null));
        Assert.Single(await memory.SearchAsync(new MemoryQuery()));
    }

    [Fact]
    public async Task SaveObservationAsync_skips_when_ui_toggle_off()
    {
        var memory = CreateMemory();
        var service = CreateService(memory, new FakeSettingsStore { MemoryEnabled = false });

        await service.SaveObservationAsync("Important: observation à garder", null, MemoryType.Fact, 8);
        Assert.Empty(await memory.SearchAsync(new MemoryQuery()));
    }

    [Fact]
    public async Task SaveObservationAsync_saves_when_ui_toggle_on()
    {
        var memory = CreateMemory();
        var service = CreateService(memory, new FakeSettingsStore { MemoryEnabled = true });

        await service.SaveObservationAsync("Important: observation à garder", null, MemoryType.Fact, 8);
        Assert.Single(await memory.SearchAsync(new MemoryQuery()));
    }

    [Fact]
    public async Task Automatic_disabled_does_not_block_pure_tiering_logic()
    {
        var memory = CreateMemory();
        var service = CreateService(memory, new FakeSettingsStore { MemoryEnabled = false });

        // Méthodes pures (durabilité/tiering/importance) restent disponibles même désactivé.
        Assert.True(service.IsDurable("retiens que je préfère le café noir", MemoryType.UserPreference, "preferences"));
        Assert.Equal(MemoryTier.LongTerm, service.DecideStorage(5, "retiens que je préfère le thé", MemoryType.UserPreference, "preferences").Tier);
        Assert.True(service.ShouldSave("Important: un fait réellement durable", 4));
    }

    [Fact]
    public async Task MemoryTool_explicit_save_is_blocked_when_memory_disabled()
    {
        var memory = CreateMemory();
        var auto = CreateService(memory, new FakeSettingsStore { MemoryEnabled = false });
        var tool = new MemoryTool(memory, auto, NullLogger<MemoryTool>.Instance);

        var res = await tool.ExecuteAsync(
            new AgentContext("test"),
            new Dictionary<string, string>
            {
                ["action"] = "save",
                ["content"] = "retiens que je préfère le café noir",
                ["category"] = "preferences"
            });

        Assert.False(res.Success);
        Assert.Empty(await memory.SearchAsync(new MemoryQuery { Category = "preferences" }));
    }

    [Fact]
    public async Task MemoryTool_explicit_save_works_when_memory_enabled()
    {
        var memory = CreateMemory();
        var auto = CreateService(memory, new FakeSettingsStore { MemoryEnabled = true });
        var tool = new MemoryTool(memory, auto, NullLogger<MemoryTool>.Instance);

        var res = await tool.ExecuteAsync(
            new AgentContext("test"),
            new Dictionary<string, string>
            {
                ["action"] = "save",
                ["content"] = "retiens que je préfère le café noir",
                ["category"] = "preferences"
            });

        Assert.True(res.Success);
        Assert.Single(await memory.SearchAsync(new MemoryQuery { Category = "preferences" }));
    }

    [Fact]
    public async Task MemoryTool_get_and_delete_stay_available_when_memory_disabled()
    {
        var memory = CreateMemory();
        await memory.SaveMemoryAsync("pref.1", "je préfère le café noir", MemoryType.UserPreference, "preferences");
        var auto = CreateService(memory, new FakeSettingsStore { MemoryEnabled = false });
        var tool = new MemoryTool(memory, auto, NullLogger<MemoryTool>.Instance);

        var get = await tool.ExecuteAsync(
            new AgentContext("test"),
            new Dictionary<string, string> { ["action"] = "get", ["key"] = "pref.1" });
        Assert.True(get.Success);

        var del = await tool.ExecuteAsync(
            new AgentContext("test"),
            new Dictionary<string, string> { ["action"] = "delete", ["key"] = "pref.1" });
        Assert.True(del.Success);
        Assert.Empty(await memory.SearchAsync(new MemoryQuery { Category = "preferences" }));
    }

    [Fact]
    public async Task Reading_memory_stays_available_when_disabled()
    {
        var memory = CreateMemory();
        var service = CreateService(memory, new FakeSettingsStore { MemoryEnabled = false });

        // Pré-remplissage direct (persistance indépendante de l'activation).
        await memory.SaveMemoryAsync("pref.1", "je préfère le café noir", MemoryType.UserPreference, "preferences");

        var results = await memory.SearchAsync(new MemoryQuery { Category = "preferences" });
        Assert.Single(results);
        Assert.Equal("je préfère le café noir", results[0].Content);
    }

    private sealed class FakeSettingsStore : IMemorySettingsStore
    {
        public bool MemoryEnabled { get; set; }
        public MemorySettings Get() => new() { MemoryEnabled = MemoryEnabled };
        public void Save(MemorySettings settings) { MemoryEnabled = settings.MemoryEnabled; }
    }
}
