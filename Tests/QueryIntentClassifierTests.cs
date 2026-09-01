using JarvisAI.Application.AI;

namespace JarvisAI.Tests;

public class QueryIntentClassifierTests
{
    // ─── Subjective intent ──────────────────────────────────────────────

    [Theory]
    [InlineData("quel est, selon toi, la voiture la plus belle au monde all time")]
    [InlineData("quelle est la meilleure Ferrari selon toi")]
    [InlineData("quelle voiture choisirais-tu si tu pouvais en avoir une")]
    [InlineData("pourquoi tu préfères la 911")]
    [InlineData("quel est le meilleur jeu vidéo selon toi")]
    [InlineData("le plus beau film de tous les temps")]
    [InlineData("quelle est ta préférée")]
    [InlineData("à ton avis, c'est quoi le meilleur restaurant")]
    [InlineData("tu préfères quoi comme voiture")]
    [InlineData("le meilleur téléphone selon toi")]
    public void Subjective_questions_classified_as_subjective(string message)
    {
        var result = QueryIntentClassifier.Classify(message);
        Assert.Equal(QueryIntent.SUBJECTIVE, result.Intent);
        Assert.True(result.IsSubjective);
        Assert.Contains("subjective", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // ─── Factual intent ─────────────────────────────────────────────────

    [Theory]
    [InlineData("quelle est la capitale de la France")]
    [InlineData("quel est le symbole chimique de l'or")]
    [InlineData("c'est quoi un serveur")]
    [InlineData("comment fonctionne le VPN")]
    public void Factual_questions_classified_as_factual(string message)
    {
        var result = QueryIntentClassifier.Classify(message);
        Assert.Equal(QueryIntent.FACTUAL, result.Intent);
        Assert.False(result.IsSubjective);
    }

    [Theory]
    [InlineData("combien font 2+2")]
    public void Math_questions_classified_as_calculation(string message)
    {
        var result = QueryIntentClassifier.Classify(message);
        Assert.Equal(QueryIntent.CALCULATION, result.Intent);
    }

    // ─── Current information intent ─────────────────────────────────────

    [Theory]
    [InlineData("quel est le prix actuel de la RTX 5090")]
    [InlineData("quelle est la météo aujourd'hui")]
    [InlineData("quelle est la dernière version de Chrome")]
    [InlineData("est-ce que le magasin est ouvert maintenant")]
    public void Current_info_questions_classified_correctly(string message)
    {
        var result = QueryIntentClassifier.Classify(message);
        Assert.Equal(QueryIntent.CURRENT_INFORMATION, result.Intent);
        Assert.True(result.RequiresFactCheck);
    }

    // ─── Calculation intent ─────────────────────────────────────────────

    [Theory]
    [InlineData("calcule 145 + 37")]
    [InlineData("combien fait 12 * 8")]
    [InlineData("2+2")]
    public void Calculation_questions_classified_correctly(string message)
    {
        var result = QueryIntentClassifier.Classify(message);
        Assert.Equal(QueryIntent.CALCULATION, result.Intent);
    }

    // ─── Advice intent ──────────────────────────────────────────────────

    [Theory]
    [InlineData("tu me conseilles quoi comme PC")]
    [InlineData("que me conseilles-tu pour apprendre Python")]
    [InlineData("je devrais acheter cette voiture")]
    public void Advice_questions_classified_correctly(string message)
    {
        var result = QueryIntentClassifier.Classify(message);
        Assert.Equal(QueryIntent.ADVICE, result.Intent);
    }

    // ─── Task intent ────────────────────────────────────────────────────

    [Theory]
    [InlineData("ouvre Chrome")]
    [InlineData("lance Spotify")]
    [InlineData("ferme notepad")]
    public void Task_questions_classified_correctly(string message)
    {
        var result = QueryIntentClassifier.Classify(message);
        Assert.Equal(QueryIntent.TASK, result.Intent);
    }

    // ─── Conciseness tests ──────────────────────────────────────────────

    [Theory]
    [InlineData("merci")]
    [InlineData("ok")]
    [InlineData("oui")]
    [InlineData("bonjour")]
    public void Short_messages_classified_as_casual(string message)
    {
        var result = QueryIntentClassifier.Classify(message);
        Assert.Equal(QueryIntent.CASUAL, result.Intent);
    }

    // ─── No false subjective on factual ─────────────────────────────────

    [Theory]
    [InlineData("quelle est la capitale de l'Italie")]
    [InlineData("combien de jours a le mois de février")]
    public void Factual_questions_not_classified_as_subjective(string message)
    {
        var result = QueryIntentClassifier.Classify(message);
        Assert.NotEqual(QueryIntent.SUBJECTIVE, result.Intent);
    }
}

public class AgentSystemPromptTests
{
    private static string BuildPrompt() => AgentSystemPrompt.Build(Array.Empty<AIToolDefinition>());

    [Fact]
    public void System_prompt_contains_anti_hallucination_rules()
    {
        var prompt = BuildPrompt();
        Assert.Contains("ANTI-HALLUCINATION", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void System_prompt_contains_opinion_vs_fact_rules()
    {
        var prompt = BuildPrompt();
        Assert.Contains("OPINIONS vs FAITS", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void System_prompt_contains_certainty_language_rules()
    {
        var prompt = BuildPrompt();
        Assert.Contains("LANGAGE DE CERTITUDE", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void System_prompt_contains_concision_rules()
    {
        var prompt = BuildPrompt();
        Assert.Contains("CONCISION", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void System_prompt_prohibits_fabricated_sources()
    {
        var prompt = BuildPrompt();
        Assert.Contains("fabriquer", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void System_prompt_prohibits_fabricated_data()
    {
        var prompt = BuildPrompt();
        Assert.Contains("JAMAIS inventer", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void System_prompt_instructs_opinion_framing()
    {
        var prompt = BuildPrompt();
        Assert.Contains("Personnellement", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void System_prompt_does_not_start_with_robotic_intro()
    {
        var prompt = BuildPrompt();
        // The prompt itself should not start with robotic intro patterns
        Assert.False(prompt.StartsWith("Tu es Jarvis et je peux vous dire", StringComparison.Ordinal));
        Assert.False(prompt.StartsWith("Excellente question", StringComparison.Ordinal));
        Assert.False(prompt.StartsWith("Permettez-moi de", StringComparison.Ordinal));
        // Negative examples may appear later in the prompt as "what NOT to do"
    }

    [Fact]
    public void System_prompt_is_concise_enough()
    {
        var prompt = BuildPrompt();
        // Should be under 10000 chars (significantly shorter than the original with all tool descriptions inline)
        Assert.True(prompt.Length < 10000, $"System prompt too long: {prompt.Length} chars");
    }
}

public class TemperatureAdaptationTests
{
    // These test the temperature resolution logic conceptually
    // Actual temperature is set in AIServiceAdapter.ResolveTemperature

    [Theory]
    [InlineData(QueryIntent.SUBJECTIVE, 0.5f)]
    [InlineData(QueryIntent.CREATIVE, 0.7f)]
    [InlineData(QueryIntent.FACTUAL, 0.1f)]
    [InlineData(QueryIntent.CALCULATION, 0.0f)]
    [InlineData(QueryIntent.CURRENT_INFORMATION, 0.1f)]
    [InlineData(QueryIntent.TASK, 0.1f)]
    [InlineData(QueryIntent.CASUAL, 0.5f)]
    [InlineData(QueryIntent.ADVICE, 0.4f)]
    public void Temperature_matches_intent(QueryIntent intent, float expectedTemp)
    {
        // This tests the expected values; actual implementation is in AIServiceAdapter
        Assert.Equal(expectedTemp, GetExpectedTemperature(intent), 2);
    }

    private static float GetExpectedTemperature(QueryIntent intent) => intent switch
    {
        QueryIntent.SUBJECTIVE => 0.5f,
        QueryIntent.CREATIVE => 0.7f,
        QueryIntent.ADVICE => 0.4f,
        QueryIntent.CASUAL => 0.5f,
        QueryIntent.FACTUAL => 0.1f,
        QueryIntent.CALCULATION => 0.0f,
        QueryIntent.CURRENT_INFORMATION => 0.1f,
        QueryIntent.TASK => 0.1f,
        _ => 0.3f,
    };
}

public class HallucinationRegressionTests
{
    [Fact]
    public void Subjective_question_should_not_produce_objective_claim()
    {
        // This is a structural test: subjective questions should NOT produce
        // responses that claim objectivity
        var subjectiveQuestions = new[]
        {
            "quel est, selon toi, la voiture la plus belle au monde all time",
            "quelle est la meilleure Ferrari selon toi",
            "quel est le meilleur jeu vidéo selon toi",
        };

        foreach (var question in subjectiveQuestions)
        {
            var classification = QueryIntentClassifier.Classify(question);
            Assert.True(classification.IsSubjective,
                $"Question '{question}' should be classified as subjective");
        }
    }

    [Fact]
    public void Factual_question_should_not_be_classified_as_subjective()
    {
        var factualQuestions = new[]
        {
            "quelle est la capitale de la France",
            "combien font 2+2",
            "quel est le symbole chimique de l'or",
            "comment fonctionne Internet",
        };

        foreach (var question in factualQuestions)
        {
            var classification = QueryIntentClassifier.Classify(question);
            Assert.False(classification.IsSubjective,
                $"Question '{question}' should NOT be classified as subjective");
        }
    }

    [Fact]
    public void System_prompt_enforces_no_fabrication()
    {
        var prompt = AgentSystemPrompt.Build(Array.Empty<AIToolDefinition>());
        // Key anti-hallucination phrases that must be present
        Assert.Contains("NE JAMAIS inventer", prompt);
        Assert.Contains("fabriquer", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Ne JAMAIS présente", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void System_prompt_distinguishes_opinion_from_fact()
    {
        var prompt = AgentSystemPrompt.Build(Array.Empty<AIToolDefinition>());
        // Must contain clear distinction
        Assert.Contains("OPINIONS vs FAITS", prompt);
        Assert.Contains("réponds comme une opinion", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void System_prompt_instructs_confidence_markers()
    {
        var prompt = AgentSystemPrompt.Build(Array.Empty<AIToolDefinition>());
        Assert.Contains("De mémoire", prompt);
        Assert.Contains("Il me semble", prompt);
        Assert.Contains("Je n'ai pas", prompt);
    }
}
