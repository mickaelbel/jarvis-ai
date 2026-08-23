using JarvisAI.Infrastructure.Goals;

namespace JarvisAI.Tests;

public class GoalRunnerTests
{
    [Fact]
    public void ParsePlan_LitUnJsonStrict()
    {
        var raw = @"{""fini"": false, ""etape"": ""Vérifier la météo"", ""outil"": ""meteo"", ""arguments"": {""ville"": ""Paris""}, ""message"": ""Je regarde le ciel.""}}";
        var plan = GoalRunner.ParsePlan(raw);

        Assert.NotNull(plan);
        Assert.False(plan!.Fini);
        Assert.Equal("Vérifier la météo", plan.Etape);
        Assert.Equal("meteo", plan.Outil);
        Assert.Equal("Paris", plan.Arguments["ville"]);
        Assert.Equal("Je regarde le ciel.", plan.Message);
    }

    [Fact]
    public void ParsePlan_TolereLesBlocsMarkdown()
    {
        var raw = "Voici mon plan :\n```json\n{\"fini\": true, \"etape\": \"objectif atteint\", \"outil\": \"\", \"arguments\": {}, \"message\": \"\"}\n```";
        var plan = GoalRunner.ParsePlan(raw);

        Assert.NotNull(plan);
        Assert.True(plan!.Fini);
    }

    [Fact]
    public void ParsePlan_RetourneNullSurDuTexteSansJson()
    {
        Assert.Null(GoalRunner.ParsePlan("Je ne peux pas avancer maintenant, désolé."));
        Assert.Null(GoalRunner.ParsePlan("{\"etape\": \"\"}"));  // étape vide et pas fini -> inexploitable
    }
}
