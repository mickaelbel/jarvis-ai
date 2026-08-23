using JarvisAI.Infrastructure.Dev;
using Xunit;

namespace JarvisAI.Tests;

public class DotnetOutputParserTests
{
    [Fact]
    public void ParseTest_SortieFR_TousVerts()
    {
        var output = """
            Determining projects to restore...
            JarvisAI.Tests -> C:\src\Tests\bin\Release\net8.0\JarvisAI.Tests.dll
            Lancement de l'exécution de test, veuillez patienter...
            Un total de 1 fichier de test correspond au modèle spécifié.
            Réussi! - Échec: 0, Réussite: 2194, Ignoré: 2 - Durée totale: 02:31,4
            """;

        var r = DotnetOutputParser.ParseTest(output);

        Assert.True(r.Success);
        Assert.Equal(2194, r.Passed);
        Assert.Equal(0, r.Failed);
        Assert.Empty(r.Failures);
    }

    [Fact]
    public void ParseTest_SortieEN_AvecEchecs()
    {
        var output = """
            Starting test execution, please wait...
              Passed WakeWordMatcherTests.AcceptsPlainWake [12 ms]
              Failed VoiceHubIntegrationTests.WakeThenCommand [2 s]
            Error Message:
               Expected: True but was False
            Stack Trace:
               at VoiceHubIntegrationTests.WakeThenCommand()
              Failed! - failed: 2, passed: 2192, skipped: 0 - Total time: 02:31,4
            """;

        var r = DotnetOutputParser.ParseTest(output);

        Assert.False(r.Success);
        Assert.Equal(2192, r.Passed);
        Assert.Equal(2, r.Failed);
        Assert.NotEmpty(r.Failures);
        Assert.Contains("Expected: True", r.Failures[0].Message);
    }

    [Fact]
    public void ParseTest_SortieFR_AvecFail()
    {
        var output = """
            Lancement de l'exécution de test...
              Réussite RoutineEngineTests.Horaire_Quotidien [5 ms]
              Échoué RoutineEngineTests.Presence_Retour [FAIL]
            Message d'erreur :
               La routine n'a pas été déclenchée
            Arborescence des appels de procédure :
               at RoutineEngineTests.Presence_Retour()
            Échoué ! - échec: 1, réussite: 3 - durée totale: 00:01,2
            """;

        var r = DotnetOutputParser.ParseTest(output);

        Assert.False(r.Success);
        Assert.Equal(3, r.Passed);
        Assert.Equal(1, r.Failed);
        var f = Assert.Single(r.Failures);
        Assert.Equal("RoutineEngineTests.Presence_Retour", f.Name);
        Assert.Contains("n'a pas été déclenchée", f.Message);
    }

    [Theory]
    [InlineData("Réussi! - Échec: 0, Réussite: 42", "réussite", 42)]
    [InlineData("Passed! - failed: 3, passed: 99", "failed", 3)]
    [InlineData("Échoué ! - échec: 7, réussite: 10 - durée", "échec", 7)]
    [InlineData("aucun nombre ici", "réussite", null)]
    public void ExtractNumber_CasDivers(string line, string label, int? expected)
    {
        Assert.Equal(expected, DotnetOutputParser.ExtractNumber(line, label));
    }

    [Fact]
    public void ParseBuild_AvecErreurCS()
    {
        var output = """
            JarvisAI.Infrastructure -> bin\Release\net8.0\JarvisAI.Infrastructure.dll
            src\Infrastructure\Dev\SelfDevTool.cs(97,18): error CS0103: The name 'foo' does not exist in the current context [C:\src\Infrastructure\JarvisAI.Infrastructure.csproj]
            1 avertissement
            Échec de la génération.
            """;

        var r = DotnetOutputParser.ParseBuild(output);

        Assert.False(r.Success);
        var err = Assert.Single(r.Errors);
        Assert.Contains("CS0103", err);
    }

    [Fact]
    public void ParseBuild_Propre()
    {
        var output = "Génération réussie.\n    0 avertissement\nAttente de fin d'opération...";

        var r = DotnetOutputParser.ParseBuild(output);

        Assert.True(r.Success);
        Assert.Empty(r.Errors);
    }
}