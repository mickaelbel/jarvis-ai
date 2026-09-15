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

    private static EdgeTtsTextToSpeechService CreateDualEdge(StubHttpHandler primary, StubHttpHandler secondary)
        => new(
            new[]
            {
                new HttpClient(primary) { BaseAddress = new Uri(VoicePaths.EdgeTtsBase), Timeout = TimeSpan.FromSeconds(5) },
                new HttpClient(secondary) { BaseAddress = new Uri(VoicePaths.EdgeTtsBase2), Timeout = TimeSpan.FromSeconds(5) }
            },
            NullLogger<EdgeTtsTextToSpeechService>.Instance);

    private static readonly string SoundHealth = "{\"status\":\"ok\"}";
    private static readonly string SoundVoices = "{\"voices\":[{\"ShortName\":\"fr-FR-HenriNeural\"},{\"ShortName\":\"fr-FR-DeniseNeural\"}]}";
    private static readonly string WavStub = "RIFF-test-wav";

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
                ? SoundHealth
                : SoundVoices));

        Assert.True(await edge.IsAvailableAsync());
        Assert.Contains("fr-FR-HenriNeural", edge.AvailableVoices);
        Assert.Contains("fr-FR-DeniseNeural", edge.AvailableVoices);
    }

    // ── Régression du stale cache : quand la synthèse échoue (serveur tombé), le
    // cache des voix doit être vidé pour ne plus proposer de voix « fantômes »
    // remplacées silencieusement par la même voix SAPI.
    [Fact]
    public async Task AvailableVoices_is_cleared_when_server_goes_down_after_load()
    {
        // Serveur qui répond (health+voices) puis « tombe » dès qu'on frappe /synthesize.
        var down = false;
        var edge = CreateEdge(new StubHttpHandler(req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                down = true;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }
            if (down)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(req.RequestUri!.ToString().EndsWith("/voices", StringComparison.OrdinalIgnoreCase) ? SoundVoices : SoundHealth)
            });
        }));

        Assert.True(await edge.IsAvailableAsync());
        Assert.NotEmpty(edge.AvailableVoices);

        await Assert.ThrowsAsync<InvalidOperationException>(() => edge.SynthesizeWavAsync("bonjour"));

        // Cache vidé : plus aucune voix « fantôme » ne doit survivre.
        Assert.Empty(edge.AvailableVoices);
        Assert.False(await edge.IsAvailableAsync());
        Assert.Empty(edge.AvailableVoices);
    }

    // ── Failover : si le principal (17004) est down, bascule sur le secondaire
    // (17005) ; ActiveBase / IsPrimaryActive reflètent l'état réel.
    [Fact]
    public async Task IsAvailable_fails_over_to_secondary_and_exposes_state()
    {
        var edge = CreateDualEdge(
            StubHttpHandler.Status(HttpStatusCode.ServiceUnavailable),
            StubHttpHandler.RespondByUrl(url => url.EndsWith("/health", StringComparison.OrdinalIgnoreCase) ? SoundHealth : SoundVoices));

        Assert.True(await edge.IsAvailableAsync());

        Assert.Equal(VoicePaths.EdgeTtsBase2, edge.ActiveBase);
        Assert.False(edge.IsPrimaryActive);
        Assert.NotEmpty(edge.AvailableVoices);
    }

    [Fact]
    public async Task Synthesize_succeeds_on_secondary_after_failover()
    {
        var edge = CreateDualEdge(
            StubHttpHandler.Status(HttpStatusCode.ServiceUnavailable),
            StubHttpHandler.RespondByUrl(url =>
                url.EndsWith("/health", StringComparison.OrdinalIgnoreCase) ? SoundHealth
                : url.EndsWith("/voices", StringComparison.OrdinalIgnoreCase) ? SoundVoices
                : WavStub));

        Assert.True(await edge.IsAvailableAsync());
        Assert.False(edge.IsPrimaryActive);

        var wav = await edge.SynthesizeWavAsync("bonjour");

        Assert.NotEmpty(wav);
        Assert.Equal(VoicePaths.EdgeTtsBase2, edge.ActiveBase);
    }

    [Fact]
    public async Task IsAvailable_prefers_primary_when_both_respond()
    {
        var edge = CreateDualEdge(
            StubHttpHandler.RespondByUrl(url => url.EndsWith("/health", StringComparison.OrdinalIgnoreCase) ? SoundHealth : SoundVoices),
            StubHttpHandler.Status(HttpStatusCode.ServiceUnavailable));

        Assert.True(await edge.IsAvailableAsync());

        Assert.Equal(VoicePaths.EdgeTtsBase, edge.ActiveBase);
        Assert.True(edge.IsPrimaryActive);
    }
}