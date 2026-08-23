using JarvisAI.Application.Search;
using JarvisAI.Infrastructure.Search;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public sealed class NominatimProviderTests
{
    private const string Json = """
        [
          {"place_id":1,"name":"Le Petit Restaurant","display_name":"Le Petit Restaurant, 12 Rue Sainte-Catherine, Bordeaux, Gironde, France","type":"restaurant","class":"amenity","lat":"44.8378","lon":"-0.5792","extratags":{"website":"https://petit.example.com"}},
          {"place_id":2,"name":"Grand Hôtel","display_name":"Grand Hôtel, Bordeaux, France","type":"hotel","class":"tourism","lat":"44.8400","lon":"-0.5800","extratags":{}}
        ]
        """;

    [Fact]
    public void Parses_places()
    {
        var results = NominatimLocalProvider.ParseResults(Json, 10);

        Assert.Equal(2, results.Count);
        Assert.Equal("Le Petit Restaurant", results[0].Title);
        Assert.Contains("openstreetmap.org", results[0].Url);
        Assert.Equal(SearchResultType.Local, results[0].Type);
    }

    [Fact]
    public void Extracts_website_from_extratags()
    {
        var results = NominatimLocalProvider.ParseResults(Json, 10);

        Assert.Contains("petit.example.com", results[0].Snippet);
    }

    [Fact]
    public void Computes_distance_from_reference_point()
    {
        var results = NominatimLocalProvider.ParseResults(Json, 10, nearLat: 44.84, nearLon: -0.58);

        Assert.Contains("km", results[0].Snippet);
    }

    [Fact]
    public void Ranks_by_proximity()
    {
        var results = NominatimLocalProvider.ParseResults(Json, 10, nearLat: 44.8378, nearLon: -0.5792);

        Assert.Equal("Le Petit Restaurant", results[0].Title);
    }

    [Fact]
    public async Task Search_hits_nominatim_endpoint()
    {
        var handler = StubHttpHandler.For(Json);
        var provider = new NominatimLocalProvider(new HttpClient(handler), NullLogger<NominatimLocalProvider>.Instance);

        var results = await provider.SearchAsync(new SearchRequest { Query = "restaurant à Bordeaux", Type = SearchResultType.Local });

        Assert.Equal(2, results.Count);
        Assert.Contains("nominatim.openstreetmap.org", handler.Requests[0].RequestUri!.ToString());
    }
}
