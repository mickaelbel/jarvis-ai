using JarvisAI.Application.Agents;
using JarvisAI.Application.ComputerUse;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace JarvisAI.Tests;

public sealed class ComputerUseToolTests
{
    private readonly FakeComputerUseService _computerUse = new();
    private readonly ComputerUseTool _tool;
    private readonly AgentContext _context = new("test command");

    public ComputerUseToolTests()
    {
        _tool = new ComputerUseTool(_computerUse, NullLogger<ComputerUseTool>.Instance);
    }

    private static UiElement Button(int id, string label, int x, int y, int w = 60, int h = 20)
        => new(id, UiElementType.Button, label, x, y, w, h, x + w / 2, y + h / 2, 0.7);

    [Fact]
    public async Task Observe_returns_structured_observation()
    {
        _computerUse.Observation = new UiObservation(1920, 1080, 10, 20, "Bonjour",
            new[] { Button(1, "OK", 100, 200) },
            new[] { new WindowInfo(5, "Notepad", true, true, 0, 0, 800, 600) },
            "C:\\temp\\capture.png");

        var result = await _tool.ExecuteAsync(_context, Params("action", "observe"));

        Assert.True(result.Success);
        using var doc = JsonDocument.Parse(result.Output);
        var root = doc.RootElement;
        Assert.Equal("Bonjour", root.GetProperty("ocr_text").GetString());
        Assert.Equal(1, root.GetProperty("elements").GetArrayLength());
        Assert.Equal("Notepad", root.GetProperty("windows")[0].GetProperty("Title").GetString());
        Assert.Equal("C:\\temp\\capture.png", root.GetProperty("image_path").GetString());
    }

    [Fact]
    public async Task Observe_fails_when_unavailable()
    {
        _computerUse.Available = false;

        var result = await _tool.ExecuteAsync(_context, Params("action", "observe"));

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Click_element_success()
    {
        _computerUse.ClickResult = new UiActionResult(true, Button(1, "OK", 100, 200), "Clicked 'OK'", 130, 210);

        var result = await _tool.ExecuteAsync(_context, Params("action", "click_element", "label", "OK"));

        Assert.True(result.Success);
        Assert.Equal("OK", _computerUse.LastLabel);
        Assert.Equal(1, _computerUse.ClickCalls);
        Assert.Contains("OK", result.Output);
    }

    [Fact]
    public async Task Click_element_passes_button()
    {
        _computerUse.ClickResult = new UiActionResult(true, Button(1, "Propriétés", 100, 200), "clicked", 130, 210);

        var result = await _tool.ExecuteAsync(_context, Params("action", "click_element", "label", "Propriétés", "button", "right"));

        Assert.True(result.Success);
        Assert.Equal(MouseButton.Right, _computerUse.LastButton);
    }

    [Fact]
    public async Task Click_element_failure_returns_error()
    {
        _computerUse.ClickResult = new UiActionResult(false, null, "No UI element found matching 'X'", 0, 0);

        var result = await _tool.ExecuteAsync(_context, Params("action", "click_element", "label", "X"));

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Click_element_requires_label()
    {
        var result = await _tool.ExecuteAsync(_context, Params("action", "click_element"));

        Assert.False(result.Success);
        Assert.Equal(0, _computerUse.ClickCalls);
    }

    [Fact]
    public async Task Double_click_element_success()
    {
        _computerUse.DoubleClickResult = new UiActionResult(true, Button(1, "Fichier", 0, 0), "double-clicked", 30, 10);

        var result = await _tool.ExecuteAsync(_context, Params("action", "double_click_element", "label", "Fichier"));

        Assert.True(result.Success);
        Assert.Equal(1, _computerUse.DoubleClickCalls);
    }

    [Fact]
    public async Task Type_into_success()
    {
        _computerUse.TypeResult = new UiActionResult(true, Button(1, "input_Email", 0, 0), "typed", 30, 10);

        var result = await _tool.ExecuteAsync(_context, Params("action", "type_into", "label", "input_Email", "text", "bonjour@x.fr"));

        Assert.True(result.Success);
        Assert.Equal("input_Email", _computerUse.LastLabel);
        Assert.Equal("bonjour@x.fr", _computerUse.LastText);
    }

    [Fact]
    public async Task Type_into_requires_text()
    {
        var result = await _tool.ExecuteAsync(_context, Params("action", "type_into", "label", "input_Email"));

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Find_element_success()
    {
        _computerUse.Elements = new[] { Button(3, "Envoyer", 10, 20) };

        var result = await _tool.ExecuteAsync(_context, Params("action", "find_element", "label", "envoyer"));

        Assert.True(result.Success);
        using var doc = JsonDocument.Parse(result.Output);
        Assert.Equal("Envoyer", doc.RootElement.GetProperty("label").GetString());
    }

    [Fact]
    public async Task Find_element_unmatched_fails()
    {
        _computerUse.Elements = new[] { Button(3, "OK", 10, 20) };

        var result = await _tool.ExecuteAsync(_context, Params("action", "find_element", "label", "inconnu"));

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Unknown_action_fails()
    {
        var result = await _tool.ExecuteAsync(_context, Params("action", "jump"));

        Assert.False(result.Success);
    }

    [Fact]
    public void Risk_level_is_high()
    {
        Assert.Equal(SecurityRiskLevel.High, _tool.RiskLevel);
    }

    private static IReadOnlyDictionary<string, string> Params(params string[] keyValues)
    {
        var dict = new Dictionary<string, string>();
        for (var i = 0; i < keyValues.Length; i += 2)
            dict[keyValues[i]] = keyValues[i + 1];
        return dict;
    }
}
