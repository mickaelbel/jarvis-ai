using JarvisAI.Application.Agents;
using JarvisAI.Application.ComputerUse;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace JarvisAI.Tests;

public sealed class UiElementToolTests
{
    private readonly FakeComputerUseService _computerUse = new();
    private readonly UiElementTool _tool;
    private readonly AgentContext _context = new("test command");

    public UiElementToolTests()
    {
        _tool = new UiElementTool(_computerUse, NullLogger<UiElementTool>.Instance);
    }

    [Fact]
    public async Task Detect_returns_elements_and_screen_info()
    {
        _computerUse.Observation = new UiObservation(1920, 1080, 100, 50, "form",
            new[] { new UiElement(1, UiElementType.Button, "OK", 100, 200, 40, 20, 120, 210, 0.7) },
            Array.Empty<WindowInfo>(), null);

        var result = await _tool.ExecuteAsync(_context, Params("action", "detect"));

        Assert.True(result.Success);
        using var doc = JsonDocument.Parse(result.Output);
        var root = doc.RootElement;
        Assert.Equal(1920, root.GetProperty("screen").GetProperty("ScreenWidth").GetInt32());
        Assert.Equal(1, root.GetProperty("count").GetInt32());
        Assert.Equal("OK", root.GetProperty("elements")[0].GetProperty("label").GetString());
        Assert.Equal("button", root.GetProperty("elements")[0].GetProperty("type").GetString());
        Assert.Equal(120, root.GetProperty("elements")[0].GetProperty("centerX").GetInt32());
    }

    [Fact]
    public async Task Find_returns_matching_element()
    {
        _computerUse.Elements = new[] { new UiElement(2, UiElementType.Button, "Envoyer", 10, 20, 60, 20, 40, 30, 0.7) };

        var result = await _tool.ExecuteAsync(_context, Params("action", "find", "label", "envoyer"));

        Assert.True(result.Success);
        using var doc = JsonDocument.Parse(result.Output);
        Assert.True(doc.RootElement.GetProperty("found").GetBoolean());
        Assert.Equal("Envoyer", doc.RootElement.GetProperty("element").GetProperty("label").GetString());
    }

    [Fact]
    public async Task Find_unmatched_returns_found_false()
    {
        _computerUse.Elements = new[] { new UiElement(1, UiElementType.Button, "OK", 0, 0, 40, 20, 20, 10, 0.7) };

        var result = await _tool.ExecuteAsync(_context, Params("action", "find", "label", "téléporter"));

        Assert.True(result.Success);
        using var doc = JsonDocument.Parse(result.Output);
        Assert.False(doc.RootElement.GetProperty("found").GetBoolean());
    }

    [Fact]
    public async Task Find_requires_label()
    {
        var result = await _tool.ExecuteAsync(_context, Params("action", "find"));

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Detect_fails_when_unavailable()
    {
        _computerUse.Available = false;

        var result = await _tool.ExecuteAsync(_context, Params("action", "detect"));

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Detect_fails_without_observation()
    {
        _computerUse.Observation = null;

        var result = await _tool.ExecuteAsync(_context, Params("action", "detect"));

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Unknown_action_fails()
    {
        var result = await _tool.ExecuteAsync(_context, Params("action", "inspect"));

        Assert.False(result.Success);
    }

    [Fact]
    public void Risk_level_is_low_read_only()
    {
        Assert.Equal(SecurityRiskLevel.Low, _tool.RiskLevel);
    }

    private static IReadOnlyDictionary<string, string> Params(params string[] keyValues)
    {
        var dict = new Dictionary<string, string>();
        for (var i = 0; i < keyValues.Length; i += 2)
            dict[keyValues[i]] = keyValues[i + 1];
        return dict;
    }
}
