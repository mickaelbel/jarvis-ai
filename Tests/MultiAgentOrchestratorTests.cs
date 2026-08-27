using JarvisAI.Application.Agents;

namespace JarvisAI.Tests;

public sealed class MultiAgentOrchestratorTests
{
    [Fact]
    public void ParseSubGoals_extracts_parallel_tasks()
    {
        var json = "{\"tasks\":[{\"tab\":\"carbone\",\"goal\":\"Cherche une coque carbone s26 ultra pas cher\"}," +
                   "{\"tab\":\"aramid\",\"goal\":\"Cherche une coque aramid s26 ultra pas cher\"}]}";

        var goals = MultiAgentOrchestrator.ParseSubGoals(json);

        Assert.Equal(2, goals.Count);
        Assert.Equal("carbone", goals[0].Tab);
        Assert.Contains("carbone", goals[0].Goal);
        Assert.Equal("aramid", goals[1].Tab);
        Assert.Contains("aramid", goals[1].Goal);
    }

    [Fact]
    public void ParseSubGoals_handles_markdown_fences_and_leading_text()
    {
        var json = "Voici le plan :\n```json\n{\"tasks\":[{\"tab\":\"alpha\",\"goal\":\"Tache alpha\"}," +
                   "{\"tab\":\"beta\",\"goal\":\"Tache beta\"}]}\n```";

        var goals = MultiAgentOrchestrator.ParseSubGoals(json);

        Assert.Equal(2, goals.Count);
        Assert.Equal("alpha", goals[0].Tab);
        Assert.Equal("beta", goals[1].Tab);
    }

    [Fact]
    public void ParseSubGoals_invalid_content_returns_empty()
    {
        Assert.Empty(MultiAgentOrchestrator.ParseSubGoals("pas de json"));
        Assert.Empty(MultiAgentOrchestrator.ParseSubGoals("{\"foo\":\"bar\"}"));
        Assert.Empty(MultiAgentOrchestrator.ParseSubGoals(""));
    }

    [Fact]
    public void ParseSubGoals_ignores_goalless_tasks_and_sanitizes_tab_names()
    {
        var json = "{\"tasks\":[{\"tab\":\"\",\"goal\":\"Valide\"}," +
                   "{\"tab\":\"avec Espaces!\",\"goal\":\"Two\"}," +
                   "{\"tab\":\"x\",\"goal\":\"   \"}]}";

        var goals = MultiAgentOrchestrator.ParseSubGoals(json);

        Assert.Equal(2, goals.Count);
        Assert.Equal("task-1", goals[0].Tab);
        Assert.DoesNotContain(" ", goals[1].Tab);
    }

    [Fact]
    public void ParseDecision_parallel_true_with_tasks()
    {
        var json = "{\"parallel\":true,\"tasks\":[{\"tab\":\"carbone\",\"goal\":\"Cherche carbone\"}," +
                   "{\"tab\":\"aramid\",\"goal\":\"Cherche aramid\"}]}";

        var decision = MultiAgentOrchestrator.ParseDecision(json);

        Assert.True(decision.Parallel);
        Assert.Equal(2, decision.Tasks.Count);
    }

    [Fact]
    public void ParseDecision_parallel_false_ignores_tasks()
    {
        var json = "{\"parallel\":false,\"tasks\":[{\"tab\":\"carbone\",\"goal\":\"Cherche carbone\"}]}";

        var decision = MultiAgentOrchestrator.ParseDecision(json);

        Assert.False(decision.Parallel);
        Assert.Empty(decision.Tasks);
    }

    [Fact]
    public void ParseDecision_invalid_is_sequential_by_default()
    {
        Assert.False(MultiAgentOrchestrator.ParseDecision("pas de json").Parallel);
        Assert.False(MultiAgentOrchestrator.ParseDecision("").Parallel);
    }

    [Theory]
    [InlineData("calcule 2+2")]
    [InlineData("traduis ce texte en anglais")]
    [InlineData("résume ce paragraphe")]
    [InlineData("explique-moi la relativité")]
    public void LooksSingular_detects_simple_requests(string goal)
    {
        Assert.True(MultiAgentOrchestrator.LooksSingular(goal));
    }

    [Theory]
    [InlineData("trouve des coques s26 ultra carbone et aramid")]
    [InlineData("compare Google vs Amazon pour des écouteurs")]
    [InlineData("cherche plusieurs variantes de robes")]
    [InlineData("meilleures options en carbone et en aramid")]
    public void LooksParallel_detects_multi_variant_requests(string goal)
    {
        Assert.True(MultiAgentOrchestrator.LooksParallel(goal));
    }

    [Fact]
    public void Heuristics_do_not_conflict_on_neutral_goal()
    {
        Assert.False(MultiAgentOrchestrator.LooksSingular("trouve des coques carbone et aramid"));
        Assert.False(MultiAgentOrchestrator.LooksParallel("donne-moi la recette du gâteau"));
    }
}
