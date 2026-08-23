using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace JarvisAI.Application.AI;

public sealed class ModelRouter : IModelRouter
{
    private const int MaxRecentRoutes = 50;
    private readonly ModelRouterOptions _options;
    private readonly ILogger<ModelRouter> _logger;
    private readonly ConcurrentQueue<ModelRouteResult> _recent = new();

    public ModelRouterOptions Options => _options;
    public IReadOnlyList<ModelRouteResult> RecentRoutes => _recent.ToArray();
    public ModelRouteResult? LastRoute { get; private set; }

    public ModelRouter(ModelRouterOptions options, ILogger<ModelRouter> logger)
    {
        _options = options;
        _logger = logger;
    }

    public ModelRouteResult Resolve(string? userMessage, AIConversation? conversation = null, ModelSelectionMode mode = ModelSelectionMode.Auto)
    {
        var text = (userMessage ?? string.Empty).Trim();

        if (mode == ModelSelectionMode.Fast)
            return Record(ResolveFast("forced-fast"));

        if (mode == ModelSelectionMode.Powerful)
            return Record(ResolveReasoning("forced-powerful"));

        var classifier = Classifier.Classify(text, conversation, _options.LongConversationThreshold);

        var result = classifier.IsComplex
            ? ResolveReasoning(classifier.Reason)
            : ResolveFast(classifier.Reason);

        return Record(result);
    }

    private ModelRouteResult ResolveFast(string reason)
        => new(_options.FastModel, ModelProfile.Fast, reason, DateTime.UtcNow);

    private ModelRouteResult ResolveReasoning(string reason)
        => new(_options.ReasoningModel, ModelProfile.Reasoning, reason, DateTime.UtcNow);

    private ModelRouteResult Record(ModelRouteResult result)
    {
        _recent.Enqueue(result);
        while (_recent.Count > MaxRecentRoutes)
            _recent.TryDequeue(out _);
        LastRoute = result;
        _logger.LogInformation("[ModelRouter] Request routed to {Model} (profile={Profile}, reason={Reason})",
            result.Model, result.Profile, result.Reason);
        return result;
    }
}

internal static class Classifier
{
    public sealed record Result(bool IsComplex, string Reason);

    private static readonly string[] ComplexKeywords =
    {
        "code", "script", "programme", "programmer", "programmation", "fonction", "classe",
        "methode", "api", "endpoint", "bug", "debug", "compile", "compiler", "refactor",
        "refactoriser", "deploy", "deploiement", "architecture", "algorithme", "algorithm",
        "exception", "syntaxe", "regex", "sql", "requete", "json", "variable",
        "explique", "expliquer", "explication", "explain", "pourquoi", "why",
        "comment ca marche", "how does", "analyse", "analyser", "analysis", "analyze",
        "comparer", "compare", "comparaison", "difference", "différences", "vs",
        "avantages", "inconvenients", "pros and cons", "raisonne", "raisonnement", "reason",
        "reflechis", "reflect", "think step by step", "deduis", "derive", "prouve", "proof",
        "demontrer", "demonstration", "theoreme", "equation", "formule", "math",
        "resoudre", "solve this", "diagnostique", "diagnostic", "troubleshoot",
        "plan", "planifier", "planning", "strategie", "strategy", "etapes", "steps",
        "roadmap", "organise", "organiser", "projet", "processus", "architecture logicielle",
        "resume", "resumer", "summarize", "synthese", "synthetiser", "redige", "rediger",
        "ecris un", "write a", "essai", "paragraphe", "rapport", "report", "dissertation",
        "proposition", "proposal", "concevoir", "design a", "solution a", "probleme",
        "optimiser", "optimize", "review", "revoir", "plusieurs etapes", "multi-step",
        "recapitule", "recapituler", "enchainement", "ensuite", "alors", "simultanement",
        "tache complexe", "tâche complexe", "compose", "combinaison", "couplage",
        "en meme temps", "a la fois", "in parallel", "parallel", "batch", "liste de",
        "chaine", "pipeline", "workflow", "et si", "si alors", "conditions",
    };

    private static readonly Regex SequenceRegex = new(
        @"(\b(puis|ensuite|alors)\b.*\b(puis|ensuite|alors)\b|\b(d'abord|dabord|premierement|premièrement)\b.*\b(ensuite|puis|enfin)\b|\b(1\)|2\)|3\)|1\.|2\.|3\.)\s)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ActionChainRegex = new(
        @"\b(ouvre|ouvrir|lance|lancer|telecharge|télécharge|telecharger|télécharger|installe|installer|cree|cree|créer|supprime|supprimer|deplace|déplacer|renomme|renommer|envoie|envoyer|recherche|chercher)\b.*\b(et|puis|ensuite)\b.*\b(ouvre|ouvrir|lance|lancer|telecharge|télécharge|installer|cree|créer|supprime|supprimer|deplace|déplacer|envoie|envoyer)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] SimpleKeywords =
    {
        "bonjour", "salut", "bonsoir", "coucou", "hello", "hi", "hey", "merci", "thanks",
        "bye", "au revoir", "bonne nuit", "oui", "non", "yes", "no", "ok", "daccord",
        "welcome", "bon retour",
        "heure", "heures", "time", "date", "quel jour", "aujourd'hui", "today", "what time",
        "what day", "midi", "minuit", "chrono", "minuteur", "timer",
        "ouvre", "ouvrir", "lance", "lancer", "start", "open", "launch", "ferme", "fermer",
        "close", "arrete", "stop", "kill", "quitter", "navigateur", "chrome", "edge",
        "firefox", "notepad", "bloc-notes", "explorateur", "calculatrice", "musique",
        "volume", "mute", "son", "wifi", "bluetooth", "veille", "verrouille", "lock",
        "ecran", "screenshot", "capture", "sommeil", "sleep", "ouvrir le", "ouvrir la",
        "qui es-tu", "qui tu es", "que peux-tu faire", "tu peux faire quoi", "who are you",
        "what can you do", "help", "aide", "dis bonjour", "salue",
        "calcule", "compute", "additionne", "soustrais", "multiplie",
    };

    private static readonly string[] CodeMarkers =
    {
        "```", "def ", "function(", "=>", "import ", "using ", "const ", "let ", "return ",
        "public ", "private ", "class ", "void ", "int ", "string ", "bool ", "var ",
        "SELECT ", "INSERT ", "curl ", "npm ", "dotnet ", "git ", "powershell", "cmd ",
    };

    private static readonly Regex SimpleMathRegex = new(
        @"\b\d{1,10}\s*[+\-*/^%]\s*\d{1,10}\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static Result Classify(string text, AIConversation? conversation, int longConversationThreshold)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new Result(false, "empty-query");

        var normalized = Normalize(text);
        var isLongText = normalized.Length > 120;

        if (ContainsAny(normalized, ComplexKeywords))
            return new Result(true, "complex-keywords");

        if (SequenceRegex.IsMatch(normalized))
            return new Result(true, "multi-step-sequence");

        if (ActionChainRegex.IsMatch(normalized))
            return new Result(true, "action-chain");

        if (ContainsAny(normalized, CodeMarkers))
            return new Result(true, "code-markers");

        if (SimpleMathRegex.IsMatch(normalized))
            return new Result(false, "simple-math");

        if (ContainsAny(normalized, SimpleKeywords))
            return new Result(false, "simple-keywords");

        if (conversation is not null && conversation.Messages.Count >= longConversationThreshold)
            return new Result(true, "long-conversation");

        if (isLongText)
            return new Result(true, "long-query");

        return new Result(false, "short-query");
    }

    private static bool ContainsAny(string text, IReadOnlyList<string> keywords)
    {
        foreach (var keyword in keywords)
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string Normalize(string text)
    {
        var lowered = text.ToLowerInvariant();
        var formD = lowered.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(formD.Length);
        foreach (var c in formD)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(c);
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
