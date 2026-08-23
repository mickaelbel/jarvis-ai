using JarvisAI.Application.ComputerUse;
using JarvisAI.Application.Security;
using JarvisAI.Application.Vision;

namespace JarvisAI.LiveValidation;

public enum Outcome
{
    Pass,
    Fail,
    Skip
}

public sealed record Check(string Name, Outcome Outcome, string Detail)
{
    public override string ToString() => $"[{Outcome.ToString().ToUpperInvariant(),-4}] {Name} - {Detail}";
}

public sealed class AutoAcceptConfirmationService : IUserConfirmationService
{
    public Task<ConfirmationResult> RequestConfirmationAsync(ConfirmationRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(ConfirmationResult.Accepted(ConfirmationMethod.Automatic, TimeSpan.Zero, "auto-accepted by live validation"));
}

public static class Support
{
    public static async Task<bool> WaitUntilAsync(Func<bool> condition, int attempts = 50, int delayMs = 200, string? what = null)
    {
        for (var i = 0; i < attempts; i++)
        {
            if (condition())
                return true;
            await Task.Delay(delayMs);
        }
        return condition();
    }

    public static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, int attempts = 50, int delayMs = 200)
    {
        for (var i = 0; i < attempts; i++)
        {
            if (await condition())
                return true;
            await Task.Delay(delayMs);
        }
        return await condition();
    }

    public static async Task<ScreenCapture?> CaptureAsync(IComputerController controller)
    {
        for (var i = 0; i < 10; i++)
        {
            var capture = await controller.CaptureScreenAsync();
            if (capture is not null)
                return capture;
            await Task.Delay(300);
        }
        return null;
    }

    public static async Task<OcrResult?> OcrAsync(IOcrService ocr, ScreenCapture capture)
    {
        var result = await ocr.ExtractTextAsync(capture.PngBytes);
        return result is { Words.Count: > 0 } ? result : null;
    }

    public static bool IsPng(byte[] bytes)
        => bytes is { Length: >= 8 }
           && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
           && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A;

    public static string OcrText(OcrResult? ocr) => ocr?.Text?.Trim() ?? string.Empty;

    public static int? FirstLineNumber(string ocrText)
    {
        var match = System.Text.RegularExpressions.Regex.Match(ocrText, @"LINE\s+(\d{3})");
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }

    public static (int X, int Y) TextAreaCenter(WindowRect rect)
        => (rect.X + rect.Width / 2, rect.Y + rect.Height / 2 - 30);
}
