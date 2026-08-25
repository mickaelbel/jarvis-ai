using JarvisAI.Application.AI;
using JarvisAI.Application.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JarvisAI.Tests;

public class ModelChangeToolTests : IDisposable
{
    private readonly string _overridePath =
        Path.Combine(Path.GetTempPath(), $"jarvis-router-{Guid.NewGuid():N}.json");

    private ModelOverrideStore Store() => new(_overridePath);

    public void Dispose()
    {
        try { File.Delete(_overridePath); } catch { }
    }

    [Fact]
    public void Set_persists_and_reload_survives_new_instance()
    {
        Store().Set("qwen3:8b", "llama3.3:70b");
        var rechargé = ModelOverrideStore.Load(_overridePath);
        Assert.Equal("qwen3:8b", rechargé.FastOverride);
        Assert.Equal("llama3.3:70b", rechargé.ReasoningOverride);
    }

    [Fact]
    public void Reset_clears_overrides()
    {
        var store = Store();
        store.Set("x:1b", null);
        store.Reset();
        Assert.Null(Store().FastOverride);
        Assert.Null(Store().ReasoningOverride);
    }

    [Fact]
    public void Blank_values_are_stored_as_null()
    {
        var store = Store();
        store.Set("  ", "");
        Assert.Null(store.FastOverride);
        Assert.Null(store.ReasoningOverride);
    }

    [Fact]
    public void Router_uses_override_models()
    {
        var store = Store();
        store.Set("fast-override:2b", "reasoning-override:70b");
        var router = new ModelRouter(new ModelRouterOptions(), NullLogger<ModelRouter>.Instance, store);

        Assert.Equal("fast-override:2b", router.EffectiveFastModel);
        Assert.Equal("reasoning-override:70b", router.EffectiveReasoningModel);
        Assert.Equal("fast-override:2b", router.Resolve("quelle heure est-il").Model);
        Assert.Equal("reasoning-override:70b", router.Resolve("écris du code pour une classe C# complexe").Model);
    }

    [Fact]
    public void Router_without_override_uses_defaults()
    {
        var options = new ModelRouterOptions(FastModel: "def-fast", ReasoningModel: "def-strong");
        var router = new ModelRouter(options, NullLogger<ModelRouter>.Instance);
        Assert.Equal("def-fast", router.EffectiveFastModel);
        Assert.Equal("def-strong", router.EffectiveReasoningModel);
    }
}

public class ModelChangeToolValidationTests
{
    private static (JarvisAI.Infrastructure.Tools.ModelChangeTool Tool, string Path) Create()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jarvis-router-{Guid.NewGuid():N}.json");
        var store = new ModelOverrideStore(path);
        return (new JarvisAI.Infrastructure.Tools.ModelChangeTool(store, new ModelRouterOptions()), path);
    }

    private static AgentContext Context() => new("test");

    [Theory]
    [InlineData("proposer", "")]
    [InlineData("activer", "rm -rf")]
    [InlineData("installer", "../evil")]
    public async Task Invalid_model_names_are_rejected_before_any_network_call(string action, string modele)
    {
        var (tool, _) = Create();
        var parameters = new Dictionary<string, string> { ["action"] = action };
        if (modele.Length > 0) parameters["modele"] = modele;

        var result = await tool.ExecuteAsync(Context(), parameters, CancellationToken.None);

        Assert.False(result.Success);
        var erreur = result.ErrorMessage ?? "";
        Assert.True(erreur.Contains("invalide") || erreur.Contains("manquant"), $"message inattendu : {erreur}");
    }

    [Fact]
    public async Task Installer_requires_explicit_confirmation()
    {
        var (tool, path) = Create();
        try
        {
            var result = await tool.ExecuteAsync(
                Context(),
                new Dictionary<string, string> { ["action"] = "installer", ["modele"] = "qwen3:8b" },
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains("confirmation", result.ErrorMessage ?? "", StringComparison.OrdinalIgnoreCase);
            // Rien ne doit avoir été persisté par un refus.
            Assert.False(File.Exists(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Unknown_action_returns_clear_error()
    {
        var (tool, path) = Create();
        try
        {
            var result = await tool.ExecuteAsync(
                Context(),
                new Dictionary<string, string> { ["action"] = "supprime_tout" },
                CancellationToken.None);
            Assert.False(result.Success);
            Assert.Contains("Action inconnue", result.ErrorMessage ?? "");
        }
        finally { File.Delete(path); }
    }
}
