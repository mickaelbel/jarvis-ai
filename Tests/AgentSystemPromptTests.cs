using JarvisAI.Application.AI;

namespace JarvisAI.Tests;

/// <summary>
/// Le prompt système est envoyé à chaque tour : il doit rester court (latence du
/// premier token), ne lister chaque outil qu'une fois, tronquer les descriptions
/// et surtout pousser à un usage MINIMUM des outils (ton humain) plutôt qu'à
/// réessayer en boucle.
/// </summary>
public sealed class AgentSystemPromptTests
{
    private static AIToolDefinition Tool(string name, string description = "desc")
        => new(name, description, new Dictionary<string, AIToolProperty>());

    [Fact]
    public void Build_states_role_language_and_date()
    {
        var prompt = AgentSystemPrompt.Build(new[] { Tool("computer_action") });

        Assert.Contains("Jarvis", prompt);
        Assert.Contains("français", prompt);
        Assert.Contains("Date :", prompt);
    }

    [Fact]
    public void Build_pushes_minimal_tool_usage_and_human_tone()
    {
        var prompt = AgentSystemPrompt.Build(new[] { Tool("computer_action") });

        Assert.Contains("pas d'outil", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Un seul outil", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_does_not_encourage_endless_tool_retries()
    {
        var prompt = AgentSystemPrompt.Build(new[] { Tool("computer_action") });

        // L'ancien prompt disait « Si un outil échoue, essaie un AUTRE outil »,
        // ce qui provoquait des boucles d'outils au lieu d'une réponse humaine.
        Assert.DoesNotContain("essaie un AUTRE outil", prompt);
        Assert.Contains("2 tentatives", prompt);
    }

    [Fact]
    public void Build_keeps_local_app_and_creative_guardrails()
    {
        var prompt = AgentSystemPrompt.Build(new[] { Tool("computer_action") });

        Assert.Contains("computer_action", prompt);
        Assert.Contains("Jamais browser", prompt);
        Assert.Contains("telle quelle", prompt);
    }

    [Fact]
    public void Build_lists_each_tool_once()
    {
        var tools = new[]
        {
            Tool("computer_action"),
            Tool("computer_action"),
            Tool("process"),
            Tool("process")
        };

        var prompt = AgentSystemPrompt.Build(tools);

        Assert.Equal(1, CountOccurrences(prompt, "- computer_action :"));
        Assert.Equal(1, CountOccurrences(prompt, "- process :"));
    }

    [Fact]
    public void Build_truncates_long_descriptions()
    {
        var longDescription = new string('x', 400);
        var prompt = AgentSystemPrompt.Build(new[] { Tool("process", longDescription) });

        Assert.DoesNotContain(longDescription, prompt);
        Assert.Contains("…", prompt);
    }

    [Fact]
    public void Build_stays_compact_with_many_verbose_tools()
    {
        var tools = Enumerable.Range(0, 40)
            .Select(i => Tool("tool_" + i, new string('y', 500)))
            .ToArray();

        var prompt = AgentSystemPrompt.Build(tools);

        // Sans troncature : 40 x 500 = 20 000 caractères rien qu'en descriptions.
        Assert.True(prompt.Length < 8000, $"Prompt trop long : {prompt.Length} caractères");
    }

    [Fact]
    public void Build_includes_all_tool_names()
    {
        var prompt = AgentSystemPrompt.Build(new[] { Tool("aaa"), Tool("bbb"), Tool("ccc") });

        Assert.Contains("- aaa :", prompt);
        Assert.Contains("- bbb :", prompt);
        Assert.Contains("- ccc :", prompt);
    }

    [Fact]
    public void Build_ignores_tools_without_name()
    {
        var prompt = AgentSystemPrompt.Build(new[] { Tool("valid"), Tool("   ") });

        Assert.Contains("- valid :", prompt);
        Assert.DoesNotContain("-    :", prompt);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
