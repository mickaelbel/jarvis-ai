using JarvisAI.Application.Agents;
using JarvisAI.Application.ComputerUse;
using JarvisAI.Application.Tools;
using JarvisAI.Application.Vision;
using JarvisAI.Infrastructure.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace JarvisAI.Tests;

public sealed class VisionToolTests
{
    private readonly FakeController _controller = new();
    private readonly FakeOcr _ocr = new();
    private readonly FakeVision _vision = new();
    private readonly VisionTool _tool;
    private readonly AgentContext _context = new("test command");

    public VisionToolTests()
    {
        _tool = new VisionTool(_controller, _ocr, _vision, NullLogger<VisionTool>.Instance);
    }

    [Fact]
    public async Task VisionTool_screen_ocr_returns_text_and_words()
    {
        _controller.Screen = new ScreenCapture(new byte[] { 1, 2, 3 }, 800, 600, 0, 0);
        _ocr.Result = new OcrResult("Bonjour tout le monde", new[]
        {
            new OcrWord("Bonjour", 10, 20, 100, 30)
        });

        var result = await _tool.ExecuteAsync(_context, Params("action", "screen_ocr"));

        Assert.True(result.Success);
        using var doc = JsonDocument.Parse(result.Output);
        Assert.Equal("Bonjour tout le monde", doc.RootElement.GetProperty("text").GetString());
        Assert.Equal("Bonjour", doc.RootElement.GetProperty("words")[0].GetProperty("Text").GetString());
        Assert.Equal(10, doc.RootElement.GetProperty("words")[0].GetProperty("X").GetInt32());
        Assert.Equal(1, _controller.CaptureCalls);
    }

    [Fact]
    public async Task VisionTool_screen_describe_returns_description()
    {
        _controller.Screen = new ScreenCapture(new byte[] { 1, 2, 3 }, 800, 600, 0, 0);
        _vision.Description = new ImageDescription("Une fenêtre de navigateur est ouverte", true, null);

        var result = await _tool.ExecuteAsync(_context, Params("action", "screen_describe"));

        Assert.True(result.Success);
        Assert.Contains("navigateur", result.Output);
    }

    [Fact]
    public async Task VisionTool_image_ocr_reads_file()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"jarvis_test_{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(temp, new byte[] { 1, 2, 3 });
        _ocr.Result = new OcrResult("Texte du fichier", Array.Empty<OcrWord>());

        var result = await _tool.ExecuteAsync(_context, Params("action", "image_ocr", "path", temp));

        File.Delete(temp);
        Assert.True(result.Success);
        Assert.Contains("Texte du fichier", result.Output);
    }

    [Fact]
    public async Task VisionTool_image_describe_reads_file()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"jarvis_test_{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(temp, new byte[] { 1, 2, 3 });
        _vision.Description = new ImageDescription("Une image de test", true, null);

        var result = await _tool.ExecuteAsync(_context, Params("action", "image_describe", "path", temp));

        File.Delete(temp);
        Assert.True(result.Success);
        Assert.Contains("image de test", result.Output);
    }

    [Fact]
    public async Task VisionTool_missing_path_fails()
    {
        var result = await _tool.ExecuteAsync(_context, Params("action", "image_ocr", "path", @"C:\missing_file.png"));

        Assert.False(result.Success);
    }

    [Fact]
    public async Task VisionTool_screen_ocr_fails_when_controller_unavailable()
    {
        _controller.Available = false;

        var result = await _tool.ExecuteAsync(_context, Params("action", "screen_ocr"));

        Assert.False(result.Success);
    }

    [Fact]
    public async Task VisionTool_unknown_action_fails()
    {
        var result = await _tool.ExecuteAsync(_context, Params("action", "hallucinate"));

        Assert.False(result.Success);
    }

    private static IReadOnlyDictionary<string, string> Params(params string[] keyValues)
    {
        var dict = new Dictionary<string, string>();
        for (var i = 0; i < keyValues.Length; i += 2)
            dict[keyValues[i]] = keyValues[i + 1];
        return dict;
    }

    private sealed class FakeController : IComputerController
    {
        public bool Available { get; set; } = true;
        public ScreenCapture? Screen { get; set; }
        public int CaptureCalls { get; private set; }

        public bool IsAvailable => Available;

        public Task<ScreenCapture?> CaptureScreenAsync(CancellationToken cancellationToken = default)
        {
            CaptureCalls++;
            return Task.FromResult(Available ? Screen : null);
        }

        public Task<bool> MoveMouseAsync(int x, int y, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> ClickAsync(MouseButton button = MouseButton.Left, int? x = null, int? y = null, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> DoubleClickAsync(MouseButton button = MouseButton.Left, int? x = null, int? y = null, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> ScrollAsync(int deltaY, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> TypeTextAsync(string text, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> PressKeyAsync(string keyCombination, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<WindowInfo>>(Array.Empty<WindowInfo>());
        public Task<bool> FocusWindowAsync(long handle, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<string?> GetClipboardAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<bool> SetClipboardAsync(string text, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class FakeOcr : IOcrService
    {
        public bool Available { get; set; } = true;
        public OcrResult? Result { get; set; }
        public string? LastLanguage { get; private set; }

        public bool OcrAvailable => Available;

        public Task<OcrResult?> ExtractTextAsync(byte[] imageBytes, string language = "fra+eng", CancellationToken cancellationToken = default)
        {
            LastLanguage = language;
            return Task.FromResult(Available ? Result : null);
        }
    }

    private sealed class FakeVision : IVisionService
    {
        public ImageDescription Description { get; set; } = new(string.Empty, true, null);

        public Task<ImageDescription> DescribeImageAsync(byte[] imageBytes, string? prompt = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Description);
        }
    }
}
