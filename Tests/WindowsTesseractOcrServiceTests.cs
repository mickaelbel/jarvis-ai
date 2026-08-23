using JarvisAI.Application.Vision;
using JarvisAI.Infrastructure.Vision;
using Microsoft.Extensions.Logging.Abstractions;
using System.Drawing;
using System.Drawing.Imaging;

namespace JarvisAI.Tests;

public sealed class WindowsTesseractOcrServiceTests
{
    private static string TessdataPath => Path.Combine(AppContext.BaseDirectory, "tessdata");

    [Fact]
    public void OcrAvailable_ReflectsTessdataPresence()
    {
        var service = new WindowsTesseractOcrService(NullLogger<WindowsTesseractOcrService>.Instance, TessdataPath);

        if (OperatingSystem.IsWindows())
            Assert.Equal(Directory.Exists(TessdataPath), service.OcrAvailable);
        else
            Assert.False(service.OcrAvailable);
    }

    [Fact]
    public void OcrAvailable_False_WhenTessdataMissing()
    {
        var service = new WindowsTesseractOcrService(NullLogger<WindowsTesseractOcrService>.Instance, @"C:\definitely_missing_tessdata");

        Assert.False(service.OcrAvailable);
    }

    [Fact]
    public async Task ExtractTextAsync_ReturnsNull_WhenTessdataMissing()
    {
        var service = new WindowsTesseractOcrService(NullLogger<WindowsTesseractOcrService>.Instance, @"C:\definitely_missing_tessdata");

        var result = await service.ExtractTextAsync(new byte[] { 1, 2, 3 });

        Assert.Null(result);
    }

    [Fact]
    public async Task ExtractTextAsync_ExtractsTextFromImage()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var service = new WindowsTesseractOcrService(NullLogger<WindowsTesseractOcrService>.Instance, TessdataPath);
        if (!service.OcrAvailable)
            return;

        var png = CreateTextImage("Hello Jarvis 12345");

        var result = await service.ExtractTextAsync(png, "eng");

        Assert.NotNull(result);
        Assert.Contains("Hello", result!.Text, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(result.Words);
        Assert.Contains(result.Words, w => w.Text.Contains("Hello", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Words, w => w.Text.Contains("12345", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExtractTextAsync_ReturnsNull_OnEmptyImage()
    {
        var service = new WindowsTesseractOcrService(NullLogger<WindowsTesseractOcrService>.Instance, TessdataPath);

        var result = await service.ExtractTextAsync(Array.Empty<byte>());

        Assert.Null(result);
    }

    private static byte[] CreateTextImage(string text)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1))
            return Array.Empty<byte>();

        using var bitmap = new Bitmap(500, 120, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.White);
            using var font = new Font("Arial", 32, FontStyle.Bold);
            using var brush = new SolidBrush(Color.Black);
            graphics.DrawString(text, font, brush, 10, 35);
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }
}
