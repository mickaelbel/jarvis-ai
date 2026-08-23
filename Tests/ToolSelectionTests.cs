using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;

namespace JarvisAI.Tests;

public class ToolSelectionTests
{
    [Fact]
    public void Select_empty_tools_returns_empty()
    {
        var service = new ToolSelectionService();
        var result = service.Select(Array.Empty<ITool>(), "anything");
        Assert.Empty(result);
    }

    [Fact]
    public void Select_null_tools_returns_empty()
    {
        var service = new ToolSelectionService();
        var result = service.Select(null!, "anything");
        Assert.Empty(result);
    }

    [Fact]
    public void Select_returns_all_tools_when_few()
    {
        var service = new ToolSelectionService();
        var tools = new ITool[]
        {
            new TestTool("alpha", "first tool"),
            new TestTool("beta", "second tool")
        };
        var result = service.Select(tools, "do something");
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Select_includes_always_include_tools()
    {
        var service = new ToolSelectionService();
        var tools = new ITool[]
        {
            new TestTool("system_info", "system information"),
            new TestTool("date_time", "current time"),
            new TestTool("memory", "memory search"),
            new TestTool("unrelated", "nothing matching")
        };
        var result = service.Select(tools, "hello world");
        Assert.Contains(result, t => t.Name == "system_info");
        Assert.Contains(result, t => t.Name == "date_time");
        Assert.Contains(result, t => t.Name == "memory");
    }

    [Fact]
    public void Select_scores_name_token_highest()
    {
        var service = new ToolSelectionService();
        var tools = new ITool[]
        {
            new TestTool("terminal", "run commands"),
            new TestTool("other", "does nothing")
        };
        var scores = service.SelectWithScores(tools, "open terminal");
        Assert.True(scores.Scores["terminal"] > scores.Scores["other"]);
    }

    [Fact]
    public void Select_filters_high_risk_tools_without_execution_intent()
    {
        var service = new ToolSelectionService();
        var tools = new ITool[]
        {
            new TestTool("terminal", "run shell commands", "system", SecurityRiskLevel.High),
            new TestTool("computer", "control the computer", "system", SecurityRiskLevel.High),
            new TestTool("web_search", "search the web", "web", SecurityRiskLevel.Low)
        };
        var result = service.Select(tools, "what is the weather?");
        Assert.DoesNotContain(result, t => t.Name == "terminal");
        Assert.DoesNotContain(result, t => t.Name == "computer");
    }

    [Fact]
    public void Select_includes_high_risk_tools_with_execution_intent()
    {
        var service = new ToolSelectionService();
        var tools = new ITool[]
        {
            new TestTool("terminal", "run shell commands", "system", SecurityRiskLevel.High)
        };
        var result = service.Select(tools, "exécute la commande dir");
        Assert.Contains(result, t => t.Name == "terminal");
    }

    [Fact]
    public void Select_limits_results_to_default_capacity()
    {
        var service = new ToolSelectionService();
        var tools = Enumerable.Range(0, 50)
            .Select(i => (ITool)new TestTool($"tool_{i}", "generic description here"))
            .ToArray();
        var result = service.Select(tools, "some goal that matches nothing specific");
        Assert.True(result.Count <= 12);
    }

    [Fact]
    public void Select_orders_by_score_descending()
    {
        var service = new ToolSelectionService();
        var tools = new ITool[]
        {
            new TestTool("date_time", "get the time"),
            new TestTool("alarm", "set an alarm"),
            new TestTool("timer", "start a timer")
        };
        var result = service.Select(tools, "give me the time");
        Assert.Equal("date_time", result[0].Name);
    }

    [Fact]
    public void SelectWithScores_returns_scores_dictionary()
    {
        var service = new ToolSelectionService();
        var tools = new ITool[] { new TestTool("date_time", "the time") };
        var result = service.SelectWithScores(tools, "time");
        Assert.Single(result.Scores);
        Assert.True(result.Scores.ContainsKey("date_time"));
    }

    [Fact]
    public void Select_score_from_description_match()
    {
        var service = new ToolSelectionService();
        var tools = new ITool[]
        {
            new TestTool("helper", "list files from the file system"),
            new TestTool("helper2", "does nothing related")
        };
        var result = service.SelectWithScores(tools, "list files");
        Assert.True(result.Scores["helper"] > result.Scores["helper2"]);
    }

    [Fact]
    public void Select_score_from_goal_containing_name()
    {
        var service = new ToolSelectionService();
        var tools = new ITool[] { new TestTool("mailer", "send stuff") };
        var result = service.SelectWithScores(tools, "please mailer this to bob");
        Assert.True(result.Scores["mailer"] >= 2);
    }

    [Fact]
    public void Select_score_from_category_match()
    {
        var service = new ToolSelectionService();
        var tools = new ITool[] { new TestTool("fetch", "get remote content", "web") };
        var result = service.SelectWithScores(tools, "search the web for info");
        Assert.True(result.Scores["fetch"] >= 2);
    }

    [Fact]
    public void Tokenize_ignores_punctuation()
    {
        var service = new ToolSelectionService();
        var tools = new ITool[] { new TestTool("terminal", "shell") };
        var result = service.Select(tools, "executer: terminal!");
        Assert.Contains(result, t => t.Name == "terminal");
    }

    [Fact]
    public void Tokenize_ignores_short_tokens()
    {
        var service = new ToolSelectionService();
        var tools = new ITool[] { new TestTool("abc", "description") };
        var result = service.Select(tools, "a b c");
        Assert.DoesNotContain(result, t => t.Name == "abc");
    }

    [Fact]
    public void Select_is_case_insensitive()
    {
        var service = new ToolSelectionService();
        var tools = new ITool[] { new TestTool("TERMINAL", "shell") };
        var result = service.Select(tools, "EXECUTE terminal");
        Assert.Contains(result, t => t.Name == "TERMINAL");
    }
}
