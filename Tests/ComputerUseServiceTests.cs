using JarvisAI.Application.ComputerUse;
using JarvisAI.Application.Vision;
using JarvisAI.Infrastructure.ComputerUse;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public sealed class ComputerUseServiceTests
{
    private readonly FakeController _controller;
    private readonly FakeOcr _ocr;
    private readonly ComputerUseService _service;

    public ComputerUseServiceTests()
    {
        _controller = new FakeController();
        _ocr = new FakeOcr();
        var detector = new OcrUiElementDetector(_ocr, NullLogger<OcrUiElementDetector>.Instance);
        _service = new ComputerUseService(_controller, _ocr, detector, NullLogger<ComputerUseService>.Instance);
    }

    private void SeedScreen(string text, params OcrWord[] words)
    {
        _ocr.Result = new OcrResult(text, words);
        _controller.Screen = new ScreenCapture(new byte[] { 1, 2, 3 }, 1920, 1080, 500, 300);
    }

    [Fact]
    public async Task ObserveAsync_returns_full_observation()
    {
        SeedScreen("Bonjour", new OcrWord("OK", 100, 200, 40, 20));
        _controller.Windows = new[]
        {
            new WindowInfo(1, "Notepad", true, true, 0, 0, 800, 600)
        };

        var observation = await _service.ObserveAsync();

        Assert.NotNull(observation);
        Assert.Equal(1920, observation!.ScreenWidth);
        Assert.Equal(1080, observation.ScreenHeight);
        Assert.Equal(500, observation.CursorX);
        Assert.Equal(300, observation.CursorY);
        Assert.Equal("Bonjour", observation.OcrText);
        Assert.Single(observation.Elements);
        Assert.Single(observation.Windows);
        Assert.NotNull(observation.ImagePath);
    }

    [Fact]
    public async Task ObserveAsync_returns_null_when_unavailable()
    {
        _controller.Available = false;

        var observation = await _service.ObserveAsync();

        Assert.Null(observation);
    }

    [Fact]
    public async Task FindElementAsync_matches_by_label()
    {
        SeedScreen("form", new OcrWord("Envoyer", 100, 200, 60, 20));

        var element = await _service.FindElementAsync("Envoyer");

        Assert.NotNull(element);
        Assert.Equal("Envoyer", element!.Label);
    }

    [Fact]
    public async Task FindElementAsync_returns_null_when_unmatched()
    {
        SeedScreen("form", new OcrWord("OK", 100, 200, 40, 20));

        var element = await _service.FindElementAsync("Téléporter");

        Assert.Null(element);
    }

    [Fact]
    public async Task ClickElementAsync_clicks_element_center()
    {
        SeedScreen("form", new OcrWord("Envoyer", 100, 200, 60, 20));

        var result = await _service.ClickElementAsync("Envoyer", MouseButton.Left);

        Assert.True(result.Success);
        Assert.Equal(130, _controller.LastX);
        Assert.Equal(210, _controller.LastY);
        Assert.Equal(MouseButton.Left, _controller.LastButton);
        Assert.Equal(130, result.X);
        Assert.Equal(210, result.Y);
    }

    [Fact]
    public async Task ClickElementAsync_right_button_passes_button()
    {
        SeedScreen("form", new OcrWord("Propriétés", 100, 200, 80, 20));

        var result = await _service.ClickElementAsync("Propriétés", MouseButton.Right);

        Assert.True(result.Success);
        Assert.Equal(MouseButton.Right, _controller.LastButton);
    }

    [Fact]
    public async Task ClickElementAsync_fails_when_no_match()
    {
        SeedScreen("form", new OcrWord("OK", 100, 200, 40, 20));

        var result = await _service.ClickElementAsync("Inconnu");

        Assert.False(result.Success);
        Assert.Null(result.Element);
        Assert.Equal(1, _controller.CaptureCalls);
        Assert.Null(_controller.LastX);
    }

    [Fact]
    public async Task DoubleClickElementAsync_double_clicks_center()
    {
        SeedScreen("form", new OcrWord("Fichier", 100, 200, 50, 20));

        var result = await _service.DoubleClickElementAsync("Fichier");

        Assert.True(result.Success);
        Assert.True(_controller.LastDoubleClick);
        Assert.Equal(125, _controller.LastX);
        Assert.Equal(210, _controller.LastY);
    }

    [Fact]
    public async Task TypeIntoElementAsync_clicks_then_types()
    {
        SeedScreen("form", new OcrWord("input_Email", 100, 200, 120, 24));

        var result = await _service.TypeIntoElementAsync("input_Email", "bonjour@test.fr");

        Assert.True(result.Success);
        Assert.Equal("bonjour@test.fr", _controller.LastText);
        Assert.Equal(160, _controller.LastX);
        Assert.Equal(212, _controller.LastY);
    }

    [Fact]
    public async Task TypeIntoElementAsync_fails_when_no_match()
    {
        SeedScreen("form", new OcrWord("OK", 100, 200, 40, 20));

        var result = await _service.TypeIntoElementAsync("input_xyz", "texte");

        Assert.False(result.Success);
        Assert.Null(_controller.LastText);
    }

    [Fact]
    public void IsAvailable_reflects_controller()
    {
        Assert.True(_service.IsAvailable);
        _controller.Available = false;
        Assert.False(_service.IsAvailable);
    }

    private sealed class FakeController : IComputerController
    {
        public bool Available { get; set; } = true;
        public ScreenCapture? Screen { get; set; }
        public int CaptureCalls { get; private set; }
        public MouseButton LastButton { get; private set; }
        public int? LastX { get; private set; }
        public int? LastY { get; private set; }
        public string? LastText { get; private set; }
        public bool LastDoubleClick { get; private set; }
        public IReadOnlyList<WindowInfo> Windows { get; set; } = Array.Empty<WindowInfo>();

        public bool IsAvailable => Available;

        public Task<ScreenCapture?> CaptureScreenAsync(CancellationToken cancellationToken = default)
        {
            CaptureCalls++;
            return Task.FromResult(Available ? Screen : null);
        }

        public Task<bool> MoveMouseAsync(int x, int y, CancellationToken cancellationToken = default)
        {
            LastX = x;
            LastY = y;
            return Task.FromResult(true);
        }

        public Task<bool> ClickAsync(MouseButton button = MouseButton.Left, int? x = null, int? y = null, CancellationToken cancellationToken = default)
        {
            LastButton = button;
            LastX = x;
            LastY = y;
            return Task.FromResult(Available);
        }

        public Task<bool> DoubleClickAsync(MouseButton button = MouseButton.Left, int? x = null, int? y = null, CancellationToken cancellationToken = default)
        {
            LastButton = button;
            LastX = x;
            LastY = y;
            LastDoubleClick = true;
            return Task.FromResult(Available);
        }

        public Task<bool> ScrollAsync(int deltaY, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> TypeTextAsync(string text, CancellationToken cancellationToken = default)
        {
            LastText = text;
            return Task.FromResult(Available);
        }

        public Task<bool> PressKeyAsync(string keyCombination, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Windows);
        public Task<bool> FocusWindowAsync(long handle, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<string?> GetClipboardAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<bool> SetClipboardAsync(string text, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
