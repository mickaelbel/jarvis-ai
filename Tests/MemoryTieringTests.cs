using JarvisAI.Application.Agents;
using JarvisAI.Application.Memory;
using JarvisAI.Infrastructure.Events;
using JarvisAI.Infrastructure.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public class MemoryTieringTests
{
    private static (AutomaticMemoryService service, IMemoryService memory) Create()
    {
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var memory = new MemoryService(new InMemoryMemoryStore(), eventBus, NullLogger<MemoryService>.Instance);
        return (new AutomaticMemoryService(memory, new AutomaticMemoryOptions(), NullLogger<AutomaticMemoryService>.Instance), memory);
    }

    // ─── IsDurable ───────────────────────────────────────────────────────────

    [Fact]
    public void IsDurable_true_for_explicit_retiens()
    {
        var (service, _) = Create();
        Assert.True(service.IsDurable("souviens-toi que je préfère la ligne 9", MemoryType.Knowledge, "fait"));
        Assert.True(service.IsDurable("retiens mon rendez-vous le 12", MemoryType.Knowledge, "fait"));
    }

    [Fact]
    public void IsDurable_true_for_durable_categories()
    {
        var (service, _) = Create();
        Assert.True(service.IsDurable("ma sœur s'appelle Marie", MemoryType.Fact, "personne"));
        Assert.True(service.IsDurable("préférence: café noir", MemoryType.UserPreference, "preferences"));
        Assert.True(service.IsDurable("lancer un site vitrine", MemoryType.Fact, "projet"));
    }

    [Fact]
    public void IsDurable_false_for_trivial_or_even_important_short_term()
    {
        var (service, _) = Create();
        Assert.False(service.IsDurable("il fait beau aujourd'hui", MemoryType.Knowledge, "fait"));
        Assert.False(service.IsDurable("juste une observation banale", MemoryType.Knowledge, "agent_observation"));
    }

    // ─── DecideStorage ───────────────────────────────────────────────────────

    [Fact]
    public void DecideStorage_durable_returns_long_term_without_expiry()
    {
        var (service, _) = Create();
        var (tier, ttl) = service.DecideStorage(5, "retiens que je préfère le thé", MemoryType.UserPreference, "preferences");
        Assert.Equal(MemoryTier.LongTerm, tier);
        Assert.Null(ttl);
    }

    [Fact]
    public void DecideStorage_non_durable_returns_short_term_with_expiry()
    {
        var (service, _) = Create();
        var (tier, ttl) = service.DecideStorage(5, "le rapport a avancé", MemoryType.Knowledge, "agent_observation");
        Assert.Equal(MemoryTier.ShortTerm, tier);
        Assert.NotNull(ttl);
    }

    [Fact]
    public void DecideStorage_high_importance_promotes_long_term()
    {
        var (service, _) = Create();
        var (tier, ttl) = service.DecideStorage(9, "Important: décision majeure du projet", MemoryType.Knowledge, "agent_observation");
        Assert.Equal(MemoryTier.LongTerm, tier);
        Assert.Null(ttl);
    }

    // ─── Expiration adaptative ──────────────────────────────────────────────

    [Fact]
    public void DecideExpiration_is_adaptive_by_importance()
    {
        var (service, _) = Create();
        var low = service.DecideExpiration(1, MemoryTier.ShortTerm);
        var high = service.DecideExpiration(6, MemoryTier.ShortTerm);
        Assert.NotNull(low);
        Assert.NotNull(high);
        Assert.True(high.Value > low.Value, "les contenus plus importants doivent vivre plus longtemps");
    }

    // ─── Anti-bruit ──────────────────────────────────────────────────────────

    [Fact]
    public void ShouldSave_rejects_noise_even_with_importance()
    {
        var (service, _) = Create();
        Assert.False(service.ShouldSave("Merci beaucoup", 4));
        Assert.False(service.ShouldSave("ok", 5));
    }

    // ─── Routage du MemoryTool ───────────────────────────────────────────────

    private static (MemoryTool tool, IMemoryService memory) CreateTool()
    {
        var (service, memory) = Create();
        var tool = new MemoryTool(memory, service, NullLogger<MemoryTool>.Instance);
        return (tool, memory);
    }

    [Fact]
    public async Task MemoryTool_routes_durable_content_to_long_term()
    {
        var (tool, memory) = CreateTool();
        var res = await tool.ExecuteAsync(new JarvisAI.Application.Agents.AgentContext("test"),
            new Dictionary<string, string> { ["action"] = "save", ["content"] = "retiens que je préfère le café noir", ["category"] = "preferences" });

        Assert.True(res.Success);
        var saved = (await memory.SearchAsync(new MemoryQuery { Category = "preferences" })).FirstOrDefault();
        Assert.NotNull(saved);
        Assert.Equal(MemoryTier.LongTerm, saved.Tier);
    }

    [Fact]
    public async Task MemoryTool_routes_trivial_content_to_short_term_with_expiry()
    {
        var (tool, memory) = CreateTool();
        var res = await tool.ExecuteAsync(new JarvisAI.Application.Agents.AgentContext("test"),
            new Dictionary<string, string> { ["action"] = "save", ["content"] = "petite note du jour sans importance durable", ["category"] = "fait" });

        Assert.True(res.Success);
        var saved = (await memory.SearchAsync(new MemoryQuery { TextSearch = "petite note" })).FirstOrDefault();
        Assert.NotNull(saved);
        Assert.Equal(MemoryTier.ShortTerm, saved.Tier);
        Assert.NotNull(saved.ExpiresAt);
    }

    [Fact]
    public async Task MemoryTool_routes_person_to_long_term_permanently()
    {
        var (tool, memory) = CreateTool();
        var res = await tool.ExecuteAsync(new JarvisAI.Application.Agents.AgentContext("test"),
            new Dictionary<string, string> { ["action"] = "save", ["content"] = "mon frère s'appelle Paul", ["category"] = "personne" });

        Assert.True(res.Success);
        var saved = (await memory.SearchAsync(new MemoryQuery { Category = "personne" })).FirstOrDefault();
        Assert.NotNull(saved);
        Assert.Equal(MemoryTier.LongTerm, saved.Tier);
        Assert.Null(saved.ExpiresAt);
    }
}
