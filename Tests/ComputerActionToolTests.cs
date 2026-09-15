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

    [Fact]
    public async Task Dessine_fusee_dessine_des_traits_de_souris()
    {
        _controller.Windows = new[] { new WindowInfo(7, "Peinture - Paint", true, true, 0, 0, 1200, 800) };

        var result = await _tool.ExecuteAsync(_context, Params("instruction", "dessine une fusée"));

        Assert.True(result.Success);
        Assert.True(_controller.DragCalls.Count > 10, $"Expected ≥10 strokes for rocket, got {_controller.DragCalls.Count}");
        Assert.Contains("Dessiné", result.Output);
    }

    [Fact]
    public async Task Dessine_maison_dessine_des_traits()
    {
        _controller.Windows = new[] { new WindowInfo(7, "Peinture - Paint", true, true, 0, 0, 1200, 800) };

        var result = await _tool.ExecuteAsync(_context, Params("instruction", "dessine une maison"));

        Assert.True(result.Success);
        Assert.True(_controller.DragCalls.Count >= 6, $"Expected ≥6 strokes for house, got {_controller.DragCalls.Count}");
        Assert.Contains("Dessiné", result.Output);
    }

    [Fact]
    public async Task Dessine_sans_app_de_dessin_ouverte_renvoie_une_erreur_honnete()
    {
        var result = await _tool.ExecuteAsync(_context, Params("instruction", "dessine une fusée"));

        Assert.False(result.Success);
        Assert.Contains("aucune application de dessin", result.ErrorMessage ?? string.Empty);
        Assert.True(_controller.DragCalls.Count == 0, "ne doit rien dessiner si aucun canevas n'est ouvert");
    }

    [Fact]
    public async Task Ouvre_paint_et_dessine_fusee_en_un_seul_appel()
    {
        _controller.Windows = new[] { new WindowInfo(7, "Peinture - Paint", true, true, 0, 0, 1200, 800) };

        var result = await _tool.ExecuteAsync(_context, Params("instruction", "ouvre paint et dessine une fusée"));

        Assert.True(result.Success);
        Assert.Contains("ouvert", result.Output);
        Assert.Contains("Dessiné", result.Output);
        Assert.True(result.Output.IndexOf("ouvert", StringComparison.OrdinalIgnoreCase) <
                    result.Output.IndexOf("Dessiné", StringComparison.OrdinalIgnoreCase),
            "l'application doit s'ouvrir avant le dessin");
        Assert.True(_controller.DragCalls.Count > 10, $"Expected rocket strokes, got {_controller.DragCalls.Count}");
    }

    private static IReadOnlyDictionary<string, string> Params(params string[] keyValues)
    {
        var dict = new Dictionary<string, string>();
        for (var i = 0; i < keyValues.Length; i += 2)
            dict[keyValues[i]] = keyValues[i + 1];
        return dict;
    }
}
