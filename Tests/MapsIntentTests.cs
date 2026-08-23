using JarvisAI.Application.Agents;
using JarvisAI.Application.Search;
using JarvisAI.Infrastructure.Search;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

/// <summary>
/// P19.4 — Recherche géographique / locale : classification des requêtes locales
/// (catégorie + ville sans marqueur "près de"), GPS coords sur les résultats
/// Nominatim, intention de cartographie (adresse / itinéraire) et ouverture d'un
/// seul onglet Google Maps par requête.
/// </summary>
public sealed class MapsIntentTests
{
    // ──────────────────────────────────────────────────────────────────────
    // MapsIntentParser
    // ──────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("itinéraire vers la gare de Bordeaux depuis Mérignac", "la gare de Bordeaux", "Mérignac")]
    [InlineData("trajet vers Annecy", "Annecy", null)]
    [InlineData("route from Paris to Lyon", "Paris", "Lyon")]
    [InlineData("aller à l'aéroport", "l'aéroport", null)]
    [InlineData("comment aller à la faculté depuis la gare", "la faculté", "la gare")]
    public void Parser_detects_directions(string query, string expectedDest, string? expectedOrigin)
    {
        var intent = MapsIntentParser.Parse(query);
        Assert.Equal(MapsIntentKind.Directions, intent.Kind);
        Assert.Equal(expectedDest, intent.Destination);
        Assert.Equal(expectedOrigin, intent.Origin);
    }

    [Theory]
    [InlineData("ouvre la carte de Toulouse")]
    [InlineData("adresse 12 rue de la paix Paris")]
    [InlineData("restaurant japonais près de moi")]
    public void Parser_defaults_to_place_search(string query)
    {
        var intent = MapsIntentParser.Parse(query);
        Assert.Equal(MapsIntentKind.Place, intent.Kind);
        Assert.Null(intent.Destination);
        Assert.Equal(query, intent.SearchText);
    }

    [Fact]
    public void Parser_empty_returns_none()
    {
        Assert.Equal(MapsIntentKind.None, MapsIntentParser.Parse("").Kind);
        Assert.Equal(MapsIntentKind.None, MapsIntentParser.Parse("   ").Kind);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Classification locale étendue (QueryInterpreter)
    // ──────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("prépas STI2D Bordeaux", SearchResultType.Local)]
    [InlineData("lycée proche Annecy", SearchResultType.Local)]
    [InlineData("pizzeria Montpellier", SearchResultType.Local)]
    [InlineData("coiffeur près de chez moi", SearchResultType.Local)]
    [InlineData("restaurant japonais près de moi", SearchResultType.Local)]
    [InlineData("pharmacie ouverte maintenant", SearchResultType.Local)]
    public void Local_queries_routed_to_local_type(string query, SearchResultType expected)
    {
        Assert.Equal(expected, QueryInterpreter.DetectType(query));
        var suggested = QueryInterpreter.SuggestProviders(new SearchRequest { Query = query, Type = expected });
        Assert.Contains("nominatim", suggested);
    }

    // ──────────────────────────────────────────────────────────────────────
    // LocalQueryParser : "près de moi" + ville portée
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Near_location_flag_when_proximity_to_self()
    {
        var parsed = LocalQueryParser.Parse("restaurant japonais près de moi");
        Assert.True(parsed.NearLocation);
        Assert.Equal("restaurant", parsed.Category);
        Assert.Equal("japonais", parsed.Cuisine);
        Assert.Null(parsed.City);
    }

    [Fact]
    public void City_extracted_without_near_marker()
    {
        var parsed = LocalQueryParser.Parse("prépas STI2D Bordeaux");
        Assert.Equal("prépa", parsed.Category);
        Assert.Equal("Bordeaux", parsed.City);
        Assert.Contains("STI2D", parsed.CleanQuery);
    }

    [Fact]
    public void Moi_never_treated_as_city()
    {
        var parsed = LocalQueryParser.Parse("pharmacie autour de moi");
        Assert.Null(parsed.City);
        Assert.True(parsed.NearLocation);
        Assert.Equal("pharmacie", parsed.Category);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Coordonnées portées par les résultats Nominatim
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Nominatim_results_carry_coordinates()
    {
        var json = """[{"display_name":"Boulangerie, rue, Paris","name":"Boulangerie","type":"shop","class":"amenity","lat":"48.853","lon":"2.349"}]""";
        var results = NominatimLocalProvider.ParseResults(json, 5);
        Assert.Single(results);
        Assert.Equal(48.853, results[0].Latitude);
        Assert.Equal(2.349, results[0].Longitude);
        Assert.Equal(SearchResultType.Local, results[0].Type);
    }

    [Fact]
    public void Nominatim_results_without_distance_when_no_reference_point()
    {
        var json = """[{"display_name":"Café, Paris","name":"Café du Centre","type":"cafe","class":"amenity","lat":"48.86","lon":"2.35"}]""";
        var results = NominatimLocalProvider.ParseResults(json, 5);
        Assert.Single(results);
        Assert.DoesNotContain("km", results[0].Snippet);
    }

    // ──────────────────────────────────────────────────────────────────────
    // WebSearchTool.open_maps : 1 requête = 1 onglet Google Maps
    // ──────────────────────────────────────────────────────────────────────

    private static (WebSearchTool tool, List<string> opened) CreateMapsTool()
    {
        var verifier = new FakeLinkVerifier();
        var service = new WebSearchService(
            Array.Empty<IWebSearchProvider>(),
            verifier,
            new SearchCache(),
            new OfficialSiteDetector(),
            new FakeSiteDetector(),
            NullLogger<WebSearchService>.Instance);
        var opened = new List<string>();
        var tool = new WebSearchTool(service, NullLogger<WebSearchTool>.Instance, opened.Add);
        return (tool, opened);
    }

    [Fact]
    public async Task Open_maps_place_opens_single_google_maps_tab()
    {
        var (tool, opened) = CreateMapsTool();
        var result = await tool.ExecuteAsync(
            new AgentContext("x"),
            new Dictionary<string, string> { ["action"] = "open_maps", ["query"] = "ouvre la carte de Toulouse" });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Single(opened);
        Assert.StartsWith("https://www.google.com/maps/search/?api=1&query=", opened[0]);
        Assert.Contains(Uri.EscapeDataString("ouvre la carte de Toulouse"), opened[0]);
    }

    [Fact]
    public async Task Open_maps_directions_builds_dir_url_with_destination_and_origin()
    {
        var (tool, opened) = CreateMapsTool();
        var result = await tool.ExecuteAsync(
            new AgentContext("x"),
            new Dictionary<string, string> { ["action"] = "open_maps", ["query"] = "itinéraire vers la gare de Bordeaux depuis Mérignac" });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Single(opened);
        var url = opened[0];
        Assert.StartsWith("https://www.google.com/maps/dir/?api=1&destination=", url);
        Assert.Contains("destination=" + Uri.EscapeDataString("la gare de Bordeaux"), url);
        Assert.Contains("origin=" + Uri.EscapeDataString("Mérignac"), url);
    }

    [Fact]
    public async Task Open_maps_with_coordinates_uses_lat_lon_query()
    {
        var (tool, opened) = CreateMapsTool();
        var result = await tool.ExecuteAsync(
            new AgentContext("x"),
            new Dictionary<string, string>
            {
                ["action"] = "open_maps",
                ["query"] = "où est-ce",
                ["lat"] = "48.853",
                ["lon"] = "2.349"
            });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Single(opened);
        Assert.Contains("query=48.853,2.349", opened[0]);
    }

    [Fact]
    public async Task Open_maps_one_tab_per_request_no_loop()
    {
        var (tool, opened) = CreateMapsTool();
        for (var i = 0; i < 5; i++)
        {
            var before = opened.Count;
            var result = await tool.ExecuteAsync(
                new AgentContext("x"),
                new Dictionary<string, string> { ["action"] = "open_maps", ["query"] = "ouvre la carte du centre-ville" });
            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(before + 1, opened.Count);
        }
    }

    [Fact]
    public async Task Open_maps_requires_query()
    {
        var (tool, opened) = CreateMapsTool();
        var result = await tool.ExecuteAsync(
            new AgentContext("x"),
            new Dictionary<string, string> { ["action"] = "open_maps" });
        Assert.False(result.Success);
        Assert.Empty(opened);
    }
}
