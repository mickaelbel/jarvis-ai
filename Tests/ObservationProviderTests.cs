using JarvisAI.Application.ComputerUse;
using JarvisAI.Application.Vision;
using JarvisAI.Application.WebAutomation;
using JarvisAI.Infrastructure.WebAutomation;
using Microsoft.Extensions.Logging.Abstractions;
namespace JarvisAI.Tests;

public sealed class ObservationProviderTests
{
    [Fact]
    public async Task Returns_empty_when_no_sources_available()
    {
        var provider = new ScreenAndPageObservationProvider(null, null, null, NullLogger<ScreenAndPageObservationProvider>.Instance);

        var result = await provider.ObserveAsync("test goal");

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public async Task Includes_screen_ocr_text_when_available()
    {
        var controller = new FakeComputerController(new byte[] { 1, 2, 3 });
        var ocr = new FakeOcr("Bonjour à l'écran");
        var provider = new ScreenAndPageObservationProvider(controller, ocr, null, NullLogger<ScreenAndPageObservationProvider>.Instance);

        var result = await provider.ObserveAsync("test goal");

        Assert.Contains("SCREEN OCR", result);
        Assert.Contains("Bonjour à l'écran", result);
    }

    [Fact]
    public async Task Includes_browser_page_text_when_available()
    {
        var browser = new FakeBrowser("https://example.com", "Contenu de la page web");
        var provider = new ScreenAndPageObservationProvider(null, null, browser, NullLogger<ScreenAndPageObservationProvider>.Instance);

        var result = await provider.ObserveAsync("test goal");

        Assert.Contains("BROWSER PAGE", result);
        Assert.Contains("https://example.com", result);
        Assert.Contains("Contenu de la page web", result);
    }

    [Fact]
    public async Task Combines_screen_and_browser_when_both_available()
    {
        var controller = new FakeComputerController(new byte[] { 1, 2, 3 });
        var ocr = new FakeOcr("Texte écran");
        var browser = new FakeBrowser("https://x.com", "Texte page");
        var provider = new ScreenAndPageObservationProvider(controller, ocr, browser, NullLogger<ScreenAndPageObservationProvider>.Instance);

        var result = await provider.ObserveAsync("test goal");

        Assert.Contains("SCREEN OCR", result);
        Assert.Contains("BROWSER PAGE", result);
        Assert.Contains("Texte écran", result);
        Assert.Contains("Texte page", result);
    }

    [Fact]
    public async Task Survives_controller_failure_and_uses_other_sources()
    {
        var failing = new ThrowingComputerController();
        var ocr = new FakeOcr("Texte encore là");
        var provider = new ScreenAndPageObservationProvider(failing, ocr, null, NullLogger<ScreenAndPageObservationProvider>.Instance);

        var result = await provider.ObserveAsync("test goal");

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public async Task Includes_detected_ui_elements_and_windows_when_computer_use_available()
    {
        var computerUse = new FakeComputerUseService
        {
            Available = true,
            Observation = new UiObservation(1920, 1080, 50, 25, "OK",
                new[] { new UiElement(1, UiElementType.Button, "OK", 100, 200, 40, 20, 120, 210, 0.7) },
                new[] { new WindowInfo(9, "Notepad", true, true, 0, 0, 800, 600) },
                "C:\\temp\\capture.png")
        };
        var provider = new ScreenAndPageObservationProvider(null, null, null, NullLogger<ScreenAndPageObservationProvider>.Instance, computerUse);

        var result = await provider.ObserveAsync("test goal");

        Assert.Contains("SCREEN: 1920x1080, cursor at (50, 25)", result);
        Assert.Contains("SCREEN OCR", result);
        Assert.Contains("UI ELEMENTS", result);
        Assert.Contains("#1 button \"OK\" at (120, 210)", result);
        Assert.Contains("WINDOWS", result);
        Assert.Contains("Notepad", result);
    }

    [Fact]
    public async Task Falls_back_to_legacy_screen_observation_without_computer_use()
    {
        var controller = new FakeComputerController(new byte[] { 1, 2, 3 });
        var ocr = new FakeOcr("Texte écran");
        var provider = new ScreenAndPageObservationProvider(controller, ocr, null, NullLogger<ScreenAndPageObservationProvider>.Instance);

        var result = await provider.ObserveAsync("test goal");

        Assert.Contains("SCREEN: 100x100", result);
        Assert.Contains("Texte écran", result);
        Assert.DoesNotContain("UI ELEMENTS", result);
    }

    private sealed class FakeComputerController : IComputerController
    {
        private readonly byte[] _png;
        public FakeComputerController(byte[] png) => _png = png;
        public bool IsAvailable => true;
        public Task<ScreenCapture?> CaptureScreenAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<ScreenCapture?>(new ScreenCapture(_png, 100, 100, 0, 0));
        public Task<bool> MoveMouseAsync(int x, int y, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> ClickAsync(MouseButton button = MouseButton.Left, int? x = null, int? y = null, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> DoubleClickAsync(MouseButton button = MouseButton.Left, int? x = null, int? y = null, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> ScrollAsync(int deltaY, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> TypeTextAsync(string text, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> PressKeyAsync(string keyCombination, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<WindowInfo>>(Array.Empty<WindowInfo>());
        public Task<bool> FocusWindowAsync(long handle, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<string?> GetClipboardAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<bool> SetClipboardAsync(string text, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class ThrowingComputerController : IComputerController
    {
        public bool IsAvailable => true;
        public Task<ScreenCapture?> CaptureScreenAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("capture failed");
        public Task<bool> MoveMouseAsync(int x, int y, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> ClickAsync(MouseButton button = MouseButton.Left, int? x = null, int? y = null, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> DoubleClickAsync(MouseButton button = MouseButton.Left, int? x = null, int? y = null, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> ScrollAsync(int deltaY, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> TypeTextAsync(string text, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> PressKeyAsync(string keyCombination, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<WindowInfo>>(Array.Empty<WindowInfo>());
        public Task<bool> FocusWindowAsync(long handle, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<string?> GetClipboardAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<bool> SetClipboardAsync(string text, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class FakeOcr : IOcrService
    {
        private readonly string _text;
        public FakeOcr(string text) => _text = text;
        public bool OcrAvailable => true;
        public Task<OcrResult?> ExtractTextAsync(byte[] imageBytes, string language = "fra+eng", CancellationToken cancellationToken = default)
            => Task.FromResult<OcrResult?>(new OcrResult(_text, Array.Empty<OcrWord>()));
    }

    private sealed class FakeBrowser : IWebBrowser
    {
        private readonly string _url;
        private readonly string _text;
        public FakeBrowser(string url, string text) { _url = url; _text = text; }
        public bool IsAvailable => true;
        public Task<bool> LaunchAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> NavigateAsync(string url, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<string?> GetUrlAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(_url);
        public Task<string?> GetTitleAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>("");
        public Task<string> GetTextAsync(CancellationToken cancellationToken = default) => Task.FromResult(_text);
        public Task<WebPageSnapshot?> SnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult<WebPageSnapshot?>(new WebPageSnapshot(_url, "", _text, null));
        public Task<bool> ClickAsync(string selector, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> FillAsync(string selector, string text, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> TypeAsync(string text, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> PressAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> WaitForSelectorAsync(string selector, int timeoutMs, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> CloseAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
