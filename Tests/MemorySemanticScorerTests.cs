using JarvisAI.Application.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.RegularExpressions;
using Xunit;

namespace JarvisAI.Tests;

public sealed class MemorySemanticScorerTests
{
    private static MemorySemanticScorer CreateScorer(IEmbeddingService? embeddings = null)
    {
        return new MemorySemanticScorer(NullLogger.Instance, embeddings);
    }

    private static MemoryEntry Entry(string key, string content, string category = "", float importance = 0.5f)
    {
        return new MemoryEntry
        {
            Key = key,
            Content = content,
            Category = category,
            Importance = importance,
            CreatedAt = DateTime.UtcNow
        };
    }

    [Fact]
    public void CosineSimilarity_IdenticalVectors_ReturnsOne()
    {
        var a = new[] { 1f, 2f, 3f };
        var b = new[] { 1f, 2f, 3f };

        var result = MemorySemanticScorer.CosineSimilarity(a, b);

        Assert.Equal(1f, result, precision: 4);
    }

    [Fact]
    public void CosineSimilarity_OrthogonalVectors_ReturnsZero()
    {
        var a = new[] { 1f, 0f, 0f };
        var b = new[] { 0f, 1f, 0f };

        var result = MemorySemanticScorer.CosineSimilarity(a, b);

        Assert.Equal(0f, result, precision: 4);
    }

    [Fact]
    public void CosineSimilarity_DifferentLengths_ReturnsZero()
    {
        Assert.Equal(0f, MemorySemanticScorer.CosineSimilarity(new[] { 1f }, new[] { 1f, 2f }), precision: 4);
    }

    [Fact]
    public void LexicalScore_ExactMatch_ScoresAboveNonMatch()
    {
        var matching = Entry("k1", "l'utilisateur préfère le café au thé noir");
        var unrelated = Entry("k2", "la météo est ensoleillée aujourd'hui");

        var matchScore = MemorySemanticScorer.LexicalScore("café préférence", matching);
        var unrelatedScore = MemorySemanticScorer.LexicalScore("café préférence", unrelated);

        Assert.True(matchScore > unrelatedScore);
    }

    [Fact]
    public async Task ScoreAsync_WithoutEmbeddings_UsesLexicalFallback()
    {
        var scorer = CreateScorer(embeddings: null);
        var entries = new[]
        {
            Entry("k1", "server uses postgresql database"),
            Entry("k2", "user likes hiking on weekends"),
            Entry("k3", "database connection is slow on server")
        };

        var results = await scorer.ScoreAsync("postgresql database server", entries, top: 2);

        Assert.Equal(2, results.Count);
        Assert.Equal("k1", results[0].Entry.Key);
        Assert.True(results[0].Score > 0);
    }

    [Fact]
    public async Task ScoreAsync_WithEmbeddings_PrefersSemantic()
    {
        var embeddings = new FakeEmbeddingService();
        var scorer = CreateScorer(embeddings);
        var entries = new[]
        {
            Entry("k1", "config file for the application"),
            Entry("k2", "recipe for chocolate cake")
        };

        var results = await scorer.ScoreAsync("config file application", entries, top: 1);

        Assert.Single(results);
        Assert.Equal("k1", results[0].Entry.Key);
        Assert.Equal(3, embeddings.GenerateCalls);
    }

    [Fact]
    public async Task ScoreAsync_WhenEmbeddingUnavailable_FallsBackToLexical()
    {
        var embeddings = new UnavailableEmbeddingService();
        var scorer = CreateScorer(embeddings);
        var entries = new[] { Entry("k1", "meeting about the migration project") };

        var results = await scorer.ScoreAsync("migration meeting", entries, top: 5);

        Assert.Single(results);
        Assert.Equal("k1", results[0].Entry.Key);
    }

    [Fact]
    public async Task ScoreAsync_CachesEntryEmbeddings()
    {
        var embeddings = new FakeEmbeddingService();
        var scorer = CreateScorer(embeddings);
        var entries = new[] { Entry("k1", "shared embedding content") };

        await scorer.ScoreAsync("query one", entries);
        await scorer.ScoreAsync("query two", entries);

        Assert.Equal(3, embeddings.GenerateCalls);
    }

    private sealed class FakeEmbeddingService : IEmbeddingService
    {
        public string? ModelName => "fake";
        public int GenerateCalls { get; private set; }

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<float[]?> GenerateAsync(string text, CancellationToken cancellationToken = default)
        {
            GenerateCalls++;
            var result = new float[16];
            foreach (Match match in Regex.Matches(text.ToLowerInvariant(), "[a-z]+"))
            {
                result[Math.Abs(match.Value.GetHashCode()) % 16] += 1f;
            }
            return Task.FromResult<float[]?>(result);
        }
    }

    private sealed class UnavailableEmbeddingService : IEmbeddingService
    {
        public string? ModelName => "unavailable";

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(false);
        }

        public Task<float[]?> GenerateAsync(string text, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<float[]?>(null);
        }
    }
}
