using JarvisAI.Application.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public class ModelRouterTests
{
    private static ModelRouter CreateRouter() => new(new ModelRouterOptions(), NullLogger<ModelRouter>.Instance);

    // ─── Simple requests → fast model ────────────────────────────────────

    [Theory]
    [InlineData("Bonjour Jarvis")]
    [InlineData("quelle heure est-il ?")]
    [InlineData("ouvre le navigateur Chrome")]
    [InlineData("ferme notepad")]
    [InlineData("calcule 12 * 8")]
    [InlineData("qui es-tu ?")]
    [InlineData("merci")]
    public void Simple_requests_route_to_fast_model(string message)
    {
        var router = CreateRouter();
        var result = router.Resolve(message);
        Assert.Equal(ModelProfile.Fast, result.Profile);
        Assert.Equal("llama3.1:latest", result.Model);
    }

    [Fact]
    public void Simple_math_expression_routes_to_fast_model()
    {
        var result = CreateRouter().Resolve("combien font 145 + 37 ?");
        Assert.Equal(ModelProfile.Fast, result.Profile);
    }

    // ─── Complex requests → reasoning model ─────────────────────────────

    [Theory]
    [InlineData("écris un script Python qui liste les fichiers")]
    [InlineData("explique-moi la différence entre une classe et une interface")]
    [InlineData("compare React et Vue en détail")]
    [InlineData("analyse ce problème de performance et propose un plan")]
    [InlineData("rédige un plan de projet pour une application de gestion")]
    [InlineData("résous cette équation et montre les étapes")]
    [InlineData("comment fonctionne la récursion en programmation ?")]
    public void Complex_requests_route_to_reasoning_model(string message)
    {
        var router = CreateRouter();
        var result = router.Resolve(message);
        Assert.Equal(ModelProfile.Reasoning, result.Profile);
        Assert.Equal("qwen3:8b", result.Model);
    }

    [Fact]
    public void Code_markers_route_to_code_model()
    {
        var result = CreateRouter().Resolve("SELECT * FROM users WHERE id = 1");
        Assert.Equal(ModelProfile.Code, result.Profile);
    }

    [Fact]
    public void Long_query_routes_to_reasoning_model()
    {
        var longMessage = string.Join(" ", Enumerable.Repeat("description détaillée d'un processus métier complexe", 8));
        var result = CreateRouter().Resolve(longMessage);
        Assert.Equal(ModelProfile.Reasoning, result.Profile);
    }

    [Fact]
    public void Long_conversation_routes_to_reasoning_model()
    {
        var conversation = new AIConversation("System prompt");
        for (int i = 0; i < 7; i++)
            conversation.AddUserMessage($"message {i}");
        var result = CreateRouter().Resolve("continue", conversation);
        Assert.Equal(ModelProfile.Reasoning, result.Profile);
    }

    [Fact]
    public void Short_conversation_keeps_fast_model()
    {
        var conversation = new AIConversation("System prompt");
        conversation.AddUserMessage("salut");
        conversation.AddAssistantMessage("salut !");
        var result = CreateRouter().Resolve("merci", conversation);
        Assert.Equal(ModelProfile.Fast, result.Profile);
    }

    [Fact]
    public void Neutral_short_query_routes_to_fast_model()
    {
        var result = CreateRouter().Resolve("super");
        Assert.Equal(ModelProfile.Fast, result.Profile);
    }

    // ─── Forced modes ────────────────────────────────────────────────────

    [Fact]
    public void Forced_fast_mode_overrides_classification()
    {
        var router = CreateRouter();
        var result = router.Resolve("explique la théorie de la relativité en détail", null, ModelSelectionMode.Fast);
        Assert.Equal(ModelProfile.Fast, result.Profile);
        Assert.Equal("llama3.1:latest", result.Model);
    }

    [Fact]
    public void Forced_powerful_mode_overrides_classification()
    {
        var router = CreateRouter();
        var result = router.Resolve("bonjour", null, ModelSelectionMode.Powerful);
        Assert.Equal(ModelProfile.Reasoning, result.Profile);
        Assert.Equal("qwen3:8b", result.Model);
    }

    // ─── Routing history ─────────────────────────────────────────────────

    [Fact]
    public void Resolve_records_recent_routes_and_last_route()
    {
        var router = CreateRouter();
        router.Resolve("bonjour");
        router.Resolve("écris un script Python");

        var routes = router.RecentRoutes;
        Assert.Equal(2, routes.Count);
        Assert.Equal(ModelProfile.Fast, routes[0].Profile);
        Assert.Equal(ModelProfile.Reasoning, routes[1].Profile);
        Assert.NotNull(router.LastRoute);
        Assert.Equal("qwen3:8b", router.LastRoute!.Model);
    }

    [Fact]
    public void Route_result_has_reason_and_recent_timestamp()
    {
        var router = CreateRouter();
        var before = DateTime.UtcNow.AddSeconds(-1);
        var result = router.Resolve("bonjour");
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
        Assert.True(result.Timestamp >= before);
    }
}
