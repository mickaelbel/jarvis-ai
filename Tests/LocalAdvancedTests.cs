using JarvisAI.Application.Search;
using JarvisAI.Infrastructure.Search;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public sealed class LocalQueryParserTests
{
    [Fact]
    public void Parses_category_cuisine_and_city()
    {
        var parsed = LocalQueryParser.Parse("restaurant italien près de Bordeaux");

        Assert.Equal("restaurant", parsed.Category);
        Assert.Equal("italien", parsed.Cuisine);
        Assert.Equal("Bordeaux", parsed.City);
        Assert.False(parsed.OpenNow);
        Assert.Equal("restaurant italien", parsed.CleanQuery);
    }

    [Fact]
    public void Detects_open_now_and_top_rated()
    {
        var parsed = LocalQueryParser.Parse("meilleur restaurant ouvert maintenant à Lyon");

        Assert.True(parsed.OpenNow);
        Assert.True(parsed.TopRated);
        Assert.Equal("Lyon", parsed.City);
        Assert.Equal("restaurant", parsed.CleanQuery);
    }

    [Fact]
    public void Parses_city_after_proximity_marker()
    {
        var parsed = LocalQueryParser.Parse("garage à côté de Paris");

        Assert.Equal("garage", parsed.Category);
        Assert.Equal("Paris", parsed.City);
    }

    [Fact]
    public void Parses_category_only()
    {
        var parsed = LocalQueryParser.Parse("pharmacie");

        Assert.Equal("pharmacie", parsed.Category);
        Assert.Null(parsed.City);
    }

    [Fact]
    public void Maps_cuisine_keywords()
    {
        var parsed = LocalQueryParser.Parse("sushi à Paris");

        Assert.Equal("japonais", parsed.Cuisine);
    }

    [Fact]
    public void Parses_school_category()
    {
        var parsed = LocalQueryParser.Parse("prépa à Marseille");

        Assert.Equal("prépa", parsed.Category);
        Assert.Equal("Marseille", parsed.City);
    }

    [Fact]
    public void Extracts_capitalized_city_from_tail()
    {
        var parsed = LocalQueryParser.Parse("restaurant Nice");

        Assert.Equal("Nice", parsed.City);
        Assert.Equal("restaurant", parsed.CleanQuery);
    }

    [Fact]
    public void Empty_query_returns_empty_parse()
    {
        var parsed = LocalQueryParser.Parse("");

        Assert.Null(parsed.Category);
        Assert.Null(parsed.City);
    }
}

public sealed class OpeningHoursTests
{
    [Fact]
    public void Returns_true_when_open_on_matching_day()
    {
        var mondayNoon = new DateTimeOffset(2026, 1, 5, 12, 0, 0, TimeSpan.Zero); // Monday
        Assert.True(OpeningHours.IsOpenNow("Mo-Fr 09:00-18:00", mondayNoon));
    }

    [Fact]
    public void Returns_false_when_closed_on_weekend()
    {
        var saturdayNoon = new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero); // Saturday
        Assert.False(OpeningHours.IsOpenNow("Mo-Fr 09:00-18:00", saturdayNoon));
    }

    [Fact]
    public void Returns_true_for_24_7()
    {
        Assert.True(OpeningHours.IsOpenNow("24/7"));
    }

    [Fact]
    public void Returns_false_for_off_day()
    {
        Assert.False(OpeningHours.IsOpenNow("off"));
    }

    [Fact]
    public void Returns_false_outside_working_hours()
    {
        var mondayEight = new DateTimeOffset(2026, 1, 5, 8, 0, 0, TimeSpan.Zero);
        Assert.False(OpeningHours.IsOpenNow("Mo-Fr 09:00-18:00", mondayEight));
    }

    [Fact]
    public void Handles_day_envelope_over_weekend()
    {
        var sundayMorning = new DateTimeOffset(2026, 1, 11, 10, 0, 0, TimeSpan.Zero); // Sunday
        Assert.True(OpeningHours.IsOpenNow("Mo-Su 08:00-20:00", sundayMorning));
    }

    [Fact]
    public void Null_for_missing_hours()
    {
        Assert.Null(OpeningHours.IsOpenNow(null));
    }

    [Fact]
    public void Null_for_unparsable_hours()
    {
        Assert.Null(OpeningHours.IsOpenNow("hours unknown"));
    }

    [Fact]
    public void Multi_segment_hours_are_handled()
    {
        var sundayNoon = new DateTimeOffset(2026, 1, 11, 12, 0, 0, TimeSpan.Zero);
        Assert.True(OpeningHours.IsOpenNow("Mo-Fr 09:00-18:00; Sa-Su 10:00-14:00", sundayNoon));
    }
}

public sealed class NominatimAdvancedTests
{
    private const string PlacesJson = """
        [
          {"place_id":1,"name":"Diner ouvert","display_name":"Diner ouvert, Bordeaux","type":"restaurant","class":"amenity","lat":"44.8378","lon":"-0.5792","extratags":{"website":"https://diner.example.com","opening_hours":"24/7"}},
          {"place_id":2,"name":"Diner ferme","display_name":"Diner ferme, Bordeaux","type":"restaurant","class":"amenity","lat":"44.8400","lon":"-0.5800","extratags":{"opening_hours":"off"}}
        ]
        """;

    private const string GeoJson = """
        [{"place_id":10,"name":"Bordeaux","display_name":"Bordeaux, Gironde, France","lat":"44.8378","lon":"-0.5792"}]
        """;

    [Fact]
    public void Open_now_only_keeps_open_places()
    {
        var results = NominatimLocalProvider.ParseResults(PlacesJson, 10, openNowOnly: true);

        var single = Assert.Single(results);
        Assert.Equal("Diner ouvert", single.Title);
        Assert.True(single.OpenNow);
    }

    [Fact]
    public void Open_now_flag_is_populated()
    {
        var results = NominatimLocalProvider.ParseResults(PlacesJson, 10);

        var open = results.Single(r => r.Title == "Diner ouvert");
        Assert.True(open.OpenNow);
        Assert.Contains("Ouvert", open.Snippet);
    }

    [Fact]
    public void Close_proximity_scores_higher_than_far()
    {
        var near = NominatimLocalProvider.ComputeExtraScore(1.0, "https://x.example.com", true);
        var far = NominatimLocalProvider.ComputeExtraScore(50.0, null, null);

        Assert.True(near > far);
        Assert.Equal(0.0, far);
    }

    [Fact]
    public void Website_and_open_hours_add_score()
    {
        var baseScore = NominatimLocalProvider.ComputeExtraScore(null, null, null);
        var enhanced = NominatimLocalProvider.ComputeExtraScore(null, "https://x.example.com", true);

        Assert.True(enhanced > baseScore);
    }

    [Fact]
    public async Task Geocode_returns_city_coordinates()
    {
        var handler = StubHttpHandler.For(GeoJson);
        var provider = new NominatimLocalProvider(new HttpClient(handler), NullLogger<NominatimLocalProvider>.Instance);

        var geo = await provider.GeocodeAsync("Bordeaux");

        Assert.NotNull(geo);
        Assert.InRange(geo!.Value.Latitude, 44.0, 45.0);
        Assert.Contains("limit=1", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task Search_geocodes_city_then_queries_places()
    {
        var handler = StubHttpHandler.RespondByUrl(url => url.Contains("q=Bordeaux") ? GeoJson : PlacesJson);
        var provider = new NominatimLocalProvider(new HttpClient(handler), NullLogger<NominatimLocalProvider>.Instance);

        var results = await provider.SearchAsync(new SearchRequest { Query = "restaurant près de Bordeaux", Type = SearchResultType.Local });

        Assert.Equal(2, results.Count);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("q=restaurant", handler.Requests[1].RequestUri!.ToString());
    }

    [Fact]
    public async Task Search_with_explicit_coordinates_skips_geocoding()
    {
        var handler = StubHttpHandler.For(PlacesJson);
        var provider = new NominatimLocalProvider(new HttpClient(handler), NullLogger<NominatimLocalProvider>.Instance);

        await provider.SearchAsync(new SearchRequest
        {
            Query = "restaurant",
            Type = SearchResultType.Local,
            NearLatitude = 44.84,
            NearLongitude = -0.58
        });

        Assert.Single(handler.Requests);
    }
}
