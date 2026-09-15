using System.Net;
using JarvisAI.Infrastructure.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

/// <summary>
/// Tests du MultiModalProcessor réel (passe multimodale 4/5) : il doit appeler
/// le modèle de vision Ollama (/api/generate) et renvoyer l'analyse, pas juste
/// base64-encoder l'image comme l'ancien stub.
/// </summary>
public sealed class MultiModalProcessorTests
{
    private static readonly byte[] FakePng =
    {
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52
    };

    private const string OllamaResponse = """{"model":"llava","response":"Un chien roux dans un parc.","done":true}""";

    private static MultiModalProcessor Create(StubHttpHandler handler, string? model = null)
        => new(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434"), Timeout = TimeSpan.FromSeconds(10) },
            NullLogger<MultiModalProcessor>.Instance,
            model);

    [Fact]
    public async Task ProcessAsync_envoie_image_et_prompt_a_ollama()
    {
        var handler = StubHttpHandler.For(OllamaResponse);
        var processor = Create(handler);

        var resultat = await processor.ProcessAsync(FakePng, "Que vois-tu ?");

        Assert.True(resultat.Success);
        Assert.Equal("Un chien roux dans un parc.", resultat.Analysis);
        Assert.Equal("llava", resultat.Model);
        Assert.Equal("PNG", resultat.ImageInfo!.Format);
        Assert.NotEmpty(resultat.Base64Image);
        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Contains("/api/generate", handler.Requests[0].RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task ProcessAsync_retourne_erreur_si_ollama_down()
    {
        var processor = Create(StubHttpHandler.Status(HttpStatusCode.ServiceUnavailable));

        var resultat = await processor.ProcessAsync(FakePng, "Que vois-tu ?");

        Assert.False(resultat.Success);
        Assert.Contains("503", resultat.Error);
        Assert.Contains("llava", resultat.Error);
    }

    [Fact]
    public async Task ProcessAsync_retourne_erreur_si_reponse_vide()
    {
        var processor = Create(StubHttpHandler.For("""{"model":"llava","response":"","done":true}"""));

        var resultat = await processor.ProcessAsync(FakePng);

        Assert.False(resultat.Success);
        Assert.NotNull(resultat.Error);
    }

    [Fact]
    public async Task ProcessAsync_refuse_image_vide()
    {
        var handler = StubHttpHandler.For(OllamaResponse);
        var processor = Create(handler);

        var resultat = await processor.ProcessAsync(Array.Empty<byte>());

        Assert.False(resultat.Success);
        Assert.Empty(handler.Requests); // aucun appel réseau inutile
    }

    [Fact]
    public void IsImageSupported_reconnait_les_photos()
    {
        var processor = Create(StubHttpHandler.For(OllamaResponse));

        Assert.True(processor.IsImageSupported("photo.jpg"));
        Assert.True(processor.IsImageSupported("capture.png"));
        Assert.True(processor.IsImageSupported("image.webp"));
        Assert.False(processor.IsImageSupported("video.mp4"));
        Assert.False(processor.IsImageSupported(""));
    }
}