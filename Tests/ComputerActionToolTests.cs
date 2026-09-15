using JarvisAI.Application.Agents;
using JarvisAI.Application.ComputerUse;
using JarvisAI.Infrastructure.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public sealed class ComputerActionToolTests
{
    private readonly FakeComputerUseService _computerUse = new();
    private readonly FakeComputerController _controller = new();
    private readonly ComputerActionTool _tool;
    private readonly AgentContext _context = new("test command");

    public ComputerActionToolTests()
    {
        _controller.Screen = new ScreenCapture(new byte[] { 1, 2, 3 }, 1920, 1080, 0, 0);
        _computerUse.Observation = new UiObservation(1920, 1080, 0, 0, "Ecran", Array.Empty<UiElement>(),
            Array.Empty<WindowInfo>(), "C:\\temp\\capture.png");
        _tool = new ComputerActionTool(_controller, _computerUse, NullLogger<ComputerActionTool>.Instance);
    }

    [Fact]
    public async Task Supprimer_presses_delete_then_enter()
    {
        var result = await _tool.ExecuteAsync(_context, Params("instruction", "supprime le cube"));

        Assert.True(result.Success);
        Assert.Contains("Supprim", result.Output);
        Assert.Equal("enter", _controller.LastKeys);
    }

    [Fact]
    public async Task Supprimer_file_uses_delete_key()
    {
        var result = await _tool.ExecuteAsync(_context, Params("instruction", "supprimer la camera"));

        Assert.True(result.Success);
        Assert.Contains("Supprim", result.Output);
        Assert.Equal("enter", _controller.LastKeys);
    }

    [Fact]
    public async Task Ouvre_app_launches_and_confirms_window()
    {
        _controller.Windows = new[] { new WindowInfo(9, "Blender", true, true, 0, 0, 800, 600) };

        var result = await _tool.ExecuteAsync(_context, Params("instruction", "ouvre blender"));

        Assert.True(result.Success);
        Assert.Contains("ouvert", result.Output);
        Assert.Equal(9, _controller.LastHandle);
    }

    [Fact]
    public async Task Unknown_instruction_returns_failure()
    {
        var result = await _tool.ExecuteAsync(_context, Params("instruction", "fais tourner le satellite"));

        Assert.False(result.Success);
        Assert.DoesNotContain("Action exécutée", result.ErrorMessage ?? string.Empty);
    }

    [Fact]
    public async Task Open_then_delete_sequence()
    {
        _controller.Windows = new[] { new WindowInfo(5, "Blender 4.0", true, true, 0, 0, 1200, 800) };

        var result = await _tool.ExecuteAsync(_context, Params("instruction", "ouvre blender et supprime le cube la light et la camera"));

        Assert.True(result.Success);
        Assert.Contains("ouvert", result.Output);
        Assert.Equal("enter", _controller.LastKeys);
    }

    private static IReadOnlyDictionary<string, string> Params(params string[] keyValues)
    {
        var dict = new Dictionary<string, string>();
        for (var i = 0; i < keyValues.Length; i += 2)
            dict[keyValues[i]] = keyValues[i + 1];
        return dict;
    }
}