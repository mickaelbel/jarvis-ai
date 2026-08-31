using JarvisAI.Infrastructure.Tools;
using Xunit;
using Xunit.Abstractions;

namespace JarvisAI.Tests;

public class MemoryToolCondenseTests
{
    private readonly ITestOutputHelper _output;

    public MemoryToolCondenseTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData(
        "Je voudrais que tu ne sortes jamais une GT3 ou GT2 en tant que la plus belle voiture du monde et la plus belle voiture est pas la f8 tributo",
        "GT3 GT2 f8 tributo")]
    [InlineData(
        "J'aimerais que tu te souviennes que j'aime le café sans sucre",
        "aime café sans sucre")]
    [InlineData(
        "Merci de mémoriser que mon prénom est Marie et je suis ton assistante",
        "prénom Marie assistante")]
    [InlineData(
        "Est-ce que tu peux retenir que le projet site web est en cours de développement",
        "projet site web cours développement")]
    [InlineData(
        "Il faut que tu te rappelles que je préfère les voitures italiennes aux allemandes",
        "préfère voitures italiennes allemandes")]
    [InlineData(
        "Peux-tu mémoriser que Paul est mon frère et qu'il habite à Paris",
        "Paul frère habite Paris")]
    public void CondenseContent_ShouldReduceToKeywords(string input, string expectedContains)
    {
        // Act
        var result = MemoryTool.CondenseContent(input);

        // Debug output
        _output.WriteLine($"Input:    '{input}'");
        _output.WriteLine($"Result:   '{result}'");
        _output.WriteLine($"Expected: '{expectedContains}'");

        // Assert - le résultat devrait être plus court
        Assert.NotEmpty(result);
        Assert.True(result.Length < input.Length, $"Result '{result}' should be shorter than input '{input}'");

        // Vérifier que les mots-clés principaux sont présents
        var expectedWords = expectedContains.Split(' ');
        foreach (var word in expectedWords)
        {
            Assert.Contains(word.ToLower(), result.ToLower());
        }
    }

    [Theory]
    [InlineData("aime café sans sucre", "aime café sans sucre")]
    [InlineData("Paul frère Paris", "Paul frère Paris")]
    [InlineData("projet site web", "projet site web")]
    public void CondenseContent_ShouldNotModifyAlreadyShortContent(string input, string expected)
    {
        // Act
        var result = MemoryTool.CondenseContent(input);

        // Debug output
        _output.WriteLine($"Input:    '{input}'");
        _output.WriteLine($"Result:   '{result}'");
        _output.WriteLine($"Expected: '{expected}'");

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Je voudrais te demander de ouvrir Spotify", "ouvrir Spotify")]
    [InlineData("S'il te plaît, lance Chrome", "lance Chrome")]
    [InlineData("Merci de chercher le fichier rapport.pdf", "chercher fichier rapport.pdf")]
    public void CondenseContent_ShouldRemovePolitePhrases(string input, string expected)
    {
        // Act
        var result = MemoryTool.CondenseContent(input);

        // Debug output
        _output.WriteLine($"Input:    '{input}'");
        _output.WriteLine($"Result:   '{result}'");
        _output.WriteLine($"Expected: '{expected}'");

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void DeriveKey_ShouldCreateShortKey()
    {
        // Act
        var key = MemoryTool.DeriveKey("aime café sans sucre");

        // Debug output
        _output.WriteLine($"Key: '{key}'");

        // Assert
        Assert.NotEmpty(key);
        Assert.True(key.Length <= 48, $"Key '{key}' should be max 48 chars");
    }
}
