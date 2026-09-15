using System.Net;
using JarvisAI.Infrastructure.Voice;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

/// <summary>
/// Régression du bug « toutes les voix sont les mêmes » : quand le serveur
/// Edge TTS (port 17004) est hors ligne, <see cref="EdgeTtsTextToSpeechService"/>
/// doit exposer une liste de voix VIDE (et non une liste codée en dur) pour que
/// l'UI ne propose pas de voix qui seront remplacées par la même voix SAPI.
/// </summary>
public class EdgeTtsRegressionTests
{
    private static EdgeTtsTextToSpeechService CreateEdge(StubHttpHandler handler)
        => new(
            new HttpClient(handler) { BaseAddress = new Uri(VoicePaths.EdgeTtsBase), Timeout = TimeSpan.FromSeconds(5) },
            NullLogger<EdgeTtsTextToSpeechService>.Instance);

    [Fact]
    public void AvailableVoices_is_empty_when_server_down()
    {
        var edge = CreateEdge(StubHttpHandler.Status(HttpStatusCode.ServiceUnavailable));

        Assert.Empty(edge.AvailableVoices);
    }

    [Fact]
    public async Task AvailableVoices_is_empty_when_health_fails()
    {
        var edge = CreateEdge(StubHttpHandler.Status(HttpStatusCode.ServiceUnavailable));

        Assert.False(await edge.IsAvailableAsync());
        Assert.Empty(edge.AvailableVoices);
    }

    [Fact]
    public async Task AvailableVoices_loads_list_from_server_when_up()
    {
        var edge = CreateEdge(StubHttpHandler.RespondByUrl(url =>
            url.EndsWith("/health", StringComparison.OrdinalIgnoreCase)
                ? "{\"status\":\"ok\"}"
                : "{\"voices\":[{\"ShortName\":\"fr-FR-HenriNeural\"},{\"ShortName\":\"fr-FR-DeniseNeural\"}]}"));

        Assert.True(await edge.IsAvailableAsync());
        Assert.Contains("fr-FR-HenriNeural", edge.AvailableVoices);
        Assert.Contains("fr-FR-DeniseNeural", edge.AvailableVoices);
    }
}