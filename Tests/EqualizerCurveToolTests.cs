using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Infrastructure.Tools;

namespace JarvisAI.Tests;

public sealed class EqualizerCurveToolTests : IDisposable
{
    private readonly string _dir;
    private readonly EqualizerCurveTool _tool;

    public EqualizerCurveToolTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"jarvis_eq_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _tool = new EqualizerCurveTool();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private async Task<ToolResult> RunAsync(string fileName, string content)
    {
        var file = Path.Combine(_dir, fileName);
        File.WriteAllText(file, content);
        return await _tool.ExecuteAsync(new AgentContext("test"), new Dictionary<string, string> { ["path"] = file });
    }

    [Fact]
    public async Task Bass_curve_is_detected_as_bass_boost()
    {
        var result = await RunAsync("bass.txt",
            "FilterCurve:f0=\"10\" f1=\"20\" f2=\"40\" f3=\"80\" f4=\"160\" f5=\"320\" f6=\"640\" f7=\"1280\" f8=\"2560\" f9=\"5120\" FilterLength=\"8191\" " +
            "v0=\"2.0\" v1=\"5.0\" v2=\"8.0\" v3=\"6.0\" v4=\"3.0\" v5=\"1.0\" v6=\"0.5\" v7=\"0.2\" v8=\"0.0\" v9=\"0.0\"");

        Assert.True(result.Success);
        Assert.Contains("BOOST DES BASSES", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("10 bandes", result.Output);
        Assert.Contains("8191", result.Output);
        Assert.Contains("PAS sur les aiguës", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Treble_curve_is_detected_as_treble_boost()
    {
        var result = await RunAsync("treble.txt",
            "FilterCurve:f0=\"10\" f1=\"20\" f2=\"40\" f3=\"80\" f4=\"160\" f5=\"320\" f6=\"640\" f7=\"1280\" f8=\"2560\" f9=\"5120\" FilterLength=\"4096\" " +
            "v0=\"0.0\" v1=\"0.0\" v2=\"0.0\" v3=\"0.1\" v4=\"0.5\" v5=\"2.0\" v6=\"4.0\" v7=\"7.0\" v8=\"9.0\" v9=\"8.0\"");

        Assert.True(result.Success);
        Assert.Contains("BOOST DES AIGU", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reports_peak_gain_and_its_frequency()
    {
        var result = await RunAsync("peak.txt",
            "FilterCurve:f0=\"10\" f1=\"30\" f2=\"60\" FilterLength=\"64\" " +
            "v0=\"1.0\" v1=\"4.5\" v2=\"2.0\"");

        Assert.True(result.Success);
        Assert.Contains("4.5", result.Output);
        Assert.Contains("30", result.Output);
    }

    [Fact]
    public async Task Missing_file_fails()
    {
        var result = await _tool.ExecuteAsync(new AgentContext("test"),
            new Dictionary<string, string> { ["path"] = Path.Combine(_dir, "nope.txt") });
        Assert.False(result.Success);
        Assert.Contains("not found", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Non_filtercurve_file_fails()
    {
        var result = await RunAsync("notes.txt", "Ceci est juste un texte quelconque.");
        Assert.False(result.Success);
        Assert.Contains("FilterCurve", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Resolves_french_folder_in_path()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var fileName = "jarvis_eq_" + Guid.NewGuid().ToString("N") + ".txt";
        File.WriteAllText(Path.Combine(desktop, fileName),
            "FilterCurve:f0=\"10\" f1=\"20\" FilterLength=\"128\" v0=\"0.0\" v1=\"0.0\"");

        try
        {
            var result = await _tool.ExecuteAsync(new AgentContext("test"),
                new Dictionary<string, string> { ["path"] = "Bureau\\" + fileName });
            Assert.True(result.Success);
        }
        finally
        {
            try { File.Delete(Path.Combine(desktop, fileName)); } catch { }
        }
    }
}
