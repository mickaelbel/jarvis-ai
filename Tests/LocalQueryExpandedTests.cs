using JarvisAI.Application.Search;

namespace JarvisAI.Tests;

public sealed class LocalQueryExpandedTests
{
    [Theory]
    [InlineData("prépa intégrée", "école d'ingénieurs")]
    [InlineData("prepa integree", "école d'ingénieurs")]
    [InlineData("cpge intégrée", "école d'ingénieurs")]
    [InlineData("école d'ingénieurs à Lyon", "école d'ingénieurs")]
    [InlineData("ecole d'ingenieurs à Lyon", "école d'ingénieurs")]
    [InlineData("école de commerce à Paris", "école de commerce")]
    [InlineData("business school à Nice", "école de commerce")]
    [InlineData("cpge à Paris", "prépa")]
    [InlineData("sti2d à Toulouse", "prépa")]
    [InlineData("bts informatique", "bts")]
    [InlineData("dut informatique", "dut")]
    [InlineData("licence en droit", "licence")]
    [InlineData("master finance", "master")]
    [InlineData("prépa à Paris", "prépa")]
    [InlineData("lycée à Bordeaux", "lycée")]
    [InlineData("collège à Lille", "collège")]
    [InlineData("école primaire à Nantes", "école")]
    [InlineData("université à Marseille", "université")]
    [InlineData("fac de médecine", "université")]
    [InlineData("garage à Paris", "garage")]
    [InlineData("restaurant à Lyon", "restaurant")]
    [InlineData("pizzeria à Nice", "pizzeria")]
    [InlineData("coiffeur à Paris", "coiffeur")]
    [InlineData("pharmacie à Bordeaux", "pharmacie")]
    [InlineData("boulangerie", "boulangerie")]
    [InlineData("boucherie", "boucherie")]
    [InlineData("supermarché", "supermarché")]
    [InlineData("hôtel", "hôtel")]
    [InlineData("cinéma", "cinéma")]
    [InlineData("médecin", "médecin")]
    [InlineData("dentiste", "dentiste")]
    [InlineData("avocat", "avocat")]
    [InlineData("notaire", "notaire")]
    [InlineData("banque", "banque")]
    [InlineData("bibliothèque", "bibliothèque")]
    [InlineData("salle de sport", "salle de sport")]
    [InlineData("gym", "salle de sport")]
    [InlineData("mécanicien", "garage")]
    [InlineData("vétérinaire", "vétérinaire")]
    public void Detects_category(string query, string expectedCategory)
    {
        var parsed = LocalQueryParser.Parse(query);
        Assert.Equal(expectedCategory, parsed.Category);
    }

    [Theory]
    [InlineData("restaurant italien", "italien")]
    [InlineData("pizza", "italien")]
    [InlineData("sushi", "japonais")]
    [InlineData("restaurant chinois", "chinois")]
    [InlineData("restaurant thaï", "thaï")]
    [InlineData("restaurant thai", "thaï")]
    [InlineData("restaurant libanais", "libanais")]
    [InlineData("restaurant marocain", "marocain")]
    [InlineData("restaurant indien", "indien")]
    [InlineData("restaurant mexicain", "mexicain")]
    [InlineData("restaurant coréen", "coréen")]
    [InlineData("restaurant vietnamien", "vietnamien")]
    [InlineData("restaurant américain", "américain")]
    [InlineData("restaurant grec", "grec")]
    [InlineData("restaurant espagnol", "espagnol")]
    [InlineData("restaurant portugais", "portugais")]
    [InlineData("kebab", "kebab")]
    public void Detects_cuisine(string query, string expectedCuisine)
    {
        var parsed = LocalQueryParser.Parse(query);
        Assert.Equal(expectedCuisine, parsed.Cuisine);
    }

    [Fact]
    public void Pizza_implies_restaurant_category()
    {
        var parsed = LocalQueryParser.Parse("pizza");
        Assert.Equal("italien", parsed.Cuisine);
        Assert.Equal("restaurant", parsed.Category);
    }

    [Theory]
    [InlineData("restaurant italien près de Bordeaux", "Bordeaux")]
    [InlineData("garage à côté de Paris", "Paris")]
    [InlineData("pharmacie à Lyon", "Lyon")]
    [InlineData("restaurant ouvert maintenant à Lyon", "Lyon")]
    [InlineData("meilleur restaurant Marseille", "Marseille")]
    [InlineData("école à Toulouse", "Toulouse")]
    [InlineData("restaurant proche de Lille", "Lille")]
    public void Extracts_city(string query, string expectedCity)
    {
        var parsed = LocalQueryParser.Parse(query);
        Assert.Equal(expectedCity, parsed.City);
    }

    [Theory]
    [InlineData("restaurant ouvert maintenant", true)]
    [InlineData("restaurant ouvert à Paris", true)]
    [InlineData("restaurant open now", true)]
    [InlineData("ouverture restaurant", true)]
    [InlineData("restaurant italien", false)]
    public void Detects_open_now(string query, bool expected)
    {
        var parsed = LocalQueryParser.Parse(query);
        Assert.Equal(expected, parsed.OpenNow);
    }

    [Theory]
    [InlineData("meilleur restaurant", true)]
    [InlineData("meilleurs restaurants", true)]
    [InlineData("meilleure école", true)]
    [InlineData("meilleures recettes", true)]
    [InlineData("top restaurant", true)]
    [InlineData("le mieux noté", true)]
    [InlineData("restaurant italien", false)]
    public void Detects_top_rated(string query, bool expected)
    {
        var parsed = LocalQueryParser.Parse(query);
        Assert.Equal(expected, parsed.TopRated);
    }

    [Fact]
    public void Clean_query_removes_noise()
    {
        var parsed = LocalQueryParser.Parse("meilleur restaurant ouvert maintenant à Lyon");
        Assert.Equal("restaurant", parsed.CleanQuery);
    }

    [Fact]
    public void Clean_query_keeps_category_and_cuisine()
    {
        var parsed = LocalQueryParser.Parse("restaurant italien près de Bordeaux");
        Assert.Equal("restaurant italien", parsed.CleanQuery);
    }

    [Fact]
    public void Clean_query_keeps_category_with_city()
    {
        var parsed = LocalQueryParser.Parse("meilleur restaurant à Lyon");
        Assert.Equal("restaurant", parsed.CleanQuery);
    }

    [Fact]
    public void Empty_query_returns_default()
    {
        var parsed = LocalQueryParser.Parse("   ");
        Assert.Null(parsed.Category);
        Assert.Null(parsed.Cuisine);
        Assert.Null(parsed.City);
        Assert.False(parsed.OpenNow);
        Assert.False(parsed.TopRated);
        Assert.Equal("", parsed.CleanQuery);
    }

    [Fact]
    public void City_after_colon_is_extracted()
    {
        var parsed = LocalQueryParser.Parse("restaurant italien: Lyon");
        Assert.Equal("Lyon", parsed.City);
    }
}
