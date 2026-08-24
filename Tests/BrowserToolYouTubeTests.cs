using JarvisAI.Infrastructure.Tools;

namespace JarvisAI.Tests;

public class BrowserToolYouTubeTests
{
    [Theory]
    [InlineData("MrBeast", "MrBeast")]
    [InlineData("@MrBeast", "MrBeast")]
    [InlineData("French Hardware", "FrenchHardware")]
    [InlineData("  Léo - TechMaker_ ", "Leo-TechMaker_")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void ToYouTubeHandleSlug_strips_spaces_and_at(string? input, string expected)
    {
        Assert.Equal(expected, BrowserTool.ToYouTubeHandleSlug(input));
    }
}
