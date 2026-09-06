using JarvisAI.Application.AI;
using JarvisAI.Application.Agents;
using JarvisAI.Application.Memory;
using JarvisAI.Application.Observability;
using JarvisAI.Application.Tools;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace JarvisAI.Application.Security;

public sealed class AIServiceAdapter : IAIService
{
    private readonly AIService _inner;
    private readonly IAIProvider _provider;
    private readonly IModelRouter _router;
    private readonly IToolRegistry _toolRegistry;
    private readonly IToolExecutor _toolExecutor;
    private readonly ILogger<AIServiceAdapter> _logger;
    private readonly ITaskExecutionHistory? _taskHistory;
    private readonly JarvisAI.Application.Debug.IDebugToolFeed? _debugFeed;
    private readonly AIOptions _options;
    private readonly IToolSelectionService? _toolSelection;
    private readonly IResponseDedupGuard? _dedupGuard;
    private readonly IToolUsageTracker? _toolUsage;
    private readonly IResponseCache? _responseCache;
    private readonly IMemoryService? _memoryService;
    private readonly JarvisAI.Application.Budget.IBudgetTracker? _budget;
    private readonly JarvisAI.Application.Personality.PersonalityStore? _personality;
    private readonly IServiceProvider _serviceProvider;

    public AIServiceAdapter(
        AIService inner,
        IAIProvider provider,
        IModelRouter router,
        IToolRegistry toolRegistry,
        IToolExecutor toolExecutor,
        ILogger<AIServiceAdapter> logger,
        IServiceProvider serviceProvider,
        ITaskExecutionHistory? taskHistory = null,
        JarvisAI.Application.Debug.IDebugToolFeed? debugFeed = null,
        AIOptions? options = null,
        IToolSelectionService? toolSelection = null,
        IResponseDedupGuard? dedupGuard = null,
        IToolUsageTracker? toolUsage = null,
        IResponseCache? responseCache = null,
        IMemoryService? memoryService = null,
        JarvisAI.Application.Budget.IBudgetTracker? budget = null,
        JarvisAI.Application.Personality.PersonalityStore? personality = null)
    {
        _inner = inner;
        _provider = provider;
        _router = router;
        _toolRegistry = toolRegistry;
        _toolExecutor = toolExecutor;
        _logger = logger;
        _serviceProvider = serviceProvider;
        _taskHistory = taskHistory;
        _debugFeed = debugFeed;
        _options = options ?? new AIOptions();
        _toolSelection = toolSelection;
        _dedupGuard = dedupGuard;
        _toolUsage = toolUsage;
        _responseCache = responseCache;
        _memoryService = memoryService;
        _budget = budget;
        _personality = personality;
    }

    private async Task<string> BuildSystemPromptWithMemoryAsync(IReadOnlyList<AIToolDefinition> tools, CancellationToken cancellationToken)
    {
        var prompt = AgentSystemPrompt.Build(tools);

        var personalityGuidelines = _personality?.CurrentGuidelines;
        if (!string.IsNullOrWhiteSpace(personalityGuidelines))
            prompt += $"\n\nSTYLE DE PERSONNALITÉ (applique-le à chaque réponse) :\n{personalityGuidelines}";

        if (_memoryService is null) return prompt;

        try
        {
            var sections = new StringBuilder();
            foreach (var (category, title) in new[] { ("préférence", "Préférences"), ("personne", "Proches"), ("projet", "Projets en cours"), ("fait", "Faits") })
            {
                var entries = await _memoryService.GetContextAsync(category, 20, cancellationToken);
                if (entries.Count == 0) continue;
                sections.Append(title).Append(" : ");
                sections.AppendLine(string.Join(" ; ", entries.Select(e => $"{e.Key} = {e.Content}")));
            }

            if (sections.Length > 0)
            {
                _logger.LogDebug("[AGENT] Mémoire long terme injectée dans le system prompt");
                return prompt + "\n\nCE QUE TU SAIS DÉJÀ SUR L'UTILISATEUR (ne le récite pas spontanément, sers-t'en pour personnaliser tes réponses) :\n" + sections;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AGENT] Impossible de charger la mémoire structurée");
        }
        return prompt;
    }

    private string ResolveModel(string? explicitModel, ModelRouteResult route)
    {
        var chosen = string.IsNullOrEmpty(explicitModel) ? route.Model : explicitModel;
        var installed = _provider.KnownModels;

        if (installed.Count > 0 && !installed.Contains(chosen, StringComparer.OrdinalIgnoreCase))
        {
            var fallback = installed.FirstOrDefault(m =>
                !m.Contains("reason", StringComparison.OrdinalIgnoreCase) &&
                !m.Contains("r1", StringComparison.OrdinalIgnoreCase) &&
                !m.Contains("thinking", StringComparison.OrdinalIgnoreCase));
            _logger.LogWarning("[AGENT] Model '{Model}' non installé; repli sur '{Fallback}' (modèles installés: {Installed})",
                chosen, fallback ?? string.Join(", ", installed), string.Join(", ", installed));
            chosen = fallback ?? installed[0];
        }
        return chosen;
    }

    private static readonly string[] TrivialPatterns = new[]
    {
        "salut", "bonjour", "bonsoir", "coucou", "yo", "cc", "bjr", "bsr",
        "merci", "merci beaucoup", "thx", "tx",
        "ça va", "ca va", "comment ça va", "comment ca va", "quoi de neuf",
        "au revoir", "aurevoir", "bye", "ciao", "à plus", "a plus", "see you"
    };

    private static bool IsTrivialMessage(string message)
    {
        var lower = message.ToLowerInvariant().Trim();
        if (lower.Length > 30) return false;
        foreach (var p in TrivialPatterns)
        {
            if (lower == p)
                return true;
        }
        return false;
    }

    private static bool IsClassificationJson(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        var trimmed = content.Trim();
        if (!trimmed.StartsWith('{') || !trimmed.EndsWith('}')) return false;
        return trimmed.Contains("\"category\"") && (trimmed.Contains("\"multi_step\"") || trimmed.Contains("\"reason\""));
    }

    public async Task<AIResponse> ChatAsync(string userMessage, AIConversation? conversation = null, string? model = null, ModelSelectionMode mode = ModelSelectionMode.Powerful, CancellationToken cancellationToken = default)
    {
        // Fast-path trivial : réponse immédiate sans LLM/outils (évite "Exécution d'un outil..." et "Jarvis écrit..." bloqué)
        if (IsTrivialMessage(userMessage))
        {
            _logger.LogInformation("[AGENT] Fast-path trivial (hard-coded, no LLM): '{Msg}'", userMessage);
            var friendly = "Salut ! Comment puis-je t'aider aujourd'hui ?";
            _taskHistory?.StartRecording(userMessage, Guid.NewGuid());
            _taskHistory?.AddStep(TaskExecutionStep.Thought($"Processing request: {TruncateText(userMessage, 120)}"));
            _taskHistory?.AddStep(TaskExecutionStep.Final(friendly, 0));
            _taskHistory?.Complete(friendly, true);
            return AIResponse.Text(friendly);
        }
        var route = _router.Resolve(userMessage, conversation, mode);
        var effectiveModel = ResolveModel(model, route);
        _logger.LogInformation("[AGENT] Routing (non-streaming): mode={Mode}, model={Model}, profile={Profile}, reason={Reason}",
            mode, effectiveModel, route.Profile, route.Reason);

        var cached = _responseCache?.TryGet(userMessage, effectiveModel);
        if (cached is not null)
        {
            _logger.LogInformation("[AGENT] Cache hit (non-streaming): \"{Preview}\"", TruncateText(cached.Content, 80));
            return AIResponse.Text(cached.Content, effectiveModel);
        }

        if (!await _provider.IsAvailableAsync(cancellationToken))
        {
            _logger.LogWarning("[AGENT] Aucun fournisseur IA disponible; réponse de repli");
            return AIResponse.Failed("Aucun moteur IA n'est actuellement disponible (Ollama arrêté ?). Démarre Ollama puis réessaie.");
        }

        var result = await _inner.ChatAsync(userMessage, conversation, effectiveModel, cancellationToken);
        if (result.Success && !string.IsNullOrWhiteSpace(result.Content) && !result.HasToolCalls)
            _responseCache?.Set(userMessage, effectiveModel, result.Content);
        return result;
    }

    /// <summary>
    private static readonly (string[] Patterns, string Key)[] ChromeFollowUpActions = new[]
    {
        (new[] { "met en pause", "mise en pause", "pause", "pause la vidéo", "pause le vidéo", "met en pause la vidéo", "met en pause le vidéo", "arrête la vidéo", "arrête le vidéo", "arrête", "stop", "stoppe" }, "Space"),
        (new[] { "met en play", "met en play la vidéo", "met en play le vidéo", "remet en lecture", "reprend", "reprends", "play", "relance", "joue" }, "Space"),
        (new[] { "plein écran", "plein écràn", "fullscreen", "grand écran" }, "F"),
        (new[] { "muet", "mute", "son coupé", "coupe le son", "couper le son", "pas de son" }, "M"),
        (new[] { "remet le son", "son remis", "démuter", "demute" }, "M"),
        (new[] { "suivant", "prochaine vidéo", "passe à la suivante", "vidéo suivante" }, "Shift+N"),
        (new[] { "précédent", "retourne en arrière", "rejoue" }, "Shift+P"),
        (new[] { "accélère", "vitesse plus rapide", "plus vite" }, ">"),
        (new[] { "ralentit", "vitesse plus lente", "moins vite" }, "<"),
    };

    private static (string Pattern, string Key)? DetectChromeFollowUp(string userMessage)
    {
        var lower = userMessage.ToLowerInvariant().Trim();
        foreach (var (patterns, key) in ChromeFollowUpActions)
        {
            foreach (var pattern in patterns)
            {
                if (lower == pattern || lower.StartsWith(pattern + " ") || lower.EndsWith(" " + pattern)
                    || lower.Contains(" " + pattern + " ") || lower.Contains(pattern + "s "))
                    return (pattern, key);
            }
        }
        return null;
    }

    private static readonly string[] WebNavPrefixes = new[]
    {
        "va sur ", "vas sur ", "rends-toi sur ", "connecte-toi sur ",
        "ouvre ", "navigue vers ", "navigue sur ", "va vers ",
        "accède à ", "accede a ", "visite ",
        "montre moi ", "montre-moi ", "affiche ", "regarde sur "
    };

    private static readonly string[] WebNavSuffixes = new[]
    {
        " et cherche ", " et search ", " et trouve ", " et regarde ",
        " et affiche ", " et montre ", " et ajoute "
    };

    private static string? DetectWebNavigation(string userMessage)
    {
        var lower = userMessage.ToLowerInvariant().Trim();

        foreach (var prefix in WebNavPrefixes)
        {
            if (!lower.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            var afterPrefix = userMessage[prefix.Length..].Trim();

            // Strip suffixes like "et cherche..."
            var entity = afterPrefix;
            foreach (var suffix in WebNavSuffixes)
            {
                var idx = entity.IndexOf(suffix, StringComparison.OrdinalIgnoreCase);
                if (idx > 0)
                    entity = entity[..idx].Trim();
            }

            if (string.IsNullOrWhiteSpace(entity))
                continue;

            // If it already looks like a URL, use it directly
            if (entity.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                entity.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return entity;

            // If it contains a dot (like "amazon.fr", "google.com"), treat as domain
            if (entity.Contains('.') && !entity.Contains(' '))
                return $"https://{entity}";

            // Single word without dot = likely a desktop app name (blender, spotify, notepad...)
            // Let the LLM handle it with the process tool
            if (!entity.Contains(' ') && !entity.Contains('.'))
                return null;

            // Multi-word without dot = construct a Google search URL
            return $"https://www.google.com/search?q={Uri.EscapeDataString(entity)}";
        }

        // Detect "cherche X sur Y" (search X on Y) → navigate to Y's homepage, agent will use the search bar
        var searchOnMatch = System.Text.RegularExpressions.Regex.Match(lower,
            @"(?:cherche|trouve|regarde|affiche)\s+(.+?)\s+sur\s+(amazon|cdiscount|fnac|leboncoin|youtube|google)");
        if (searchOnMatch.Success)
        {
            var site = searchOnMatch.Groups[2].Value.Trim();
            return site switch
            {
                "amazon" => "https://www.amazon.fr",
                "cdiscount" => "https://www.cdiscount.com",
                "fnac" => "https://www.fnac.com",
                "youtube" => "https://www.youtube.com",
                "google" => "https://www.google.com",
                _ => $"https://www.{site}.com"
            };
        }

        // Bare web search without target site ("cherche les prix des RTX 4080") →
        // open Google results DIRECTLY, exactly like browser_open(recherche=...)
        // in the reference Python repo (never let the model invent URLs).
        var bareSearch = System.Text.RegularExpressions.Regex.Match(lower,
            @"^(?:est-ce que tu peux |peux-tu |tu peux )?(?:chercher?|rechercher?|trouver?|regarder)\s+(.+)$");
        if (bareSearch.Success)
        {
            var rawQuery = CleanSearchQuery(userMessage[bareSearch.Groups[1].Index..]);
            var looksLocal = new[] { "fichier", "dossier", "dans mes", "dans le", "dans la", "sur mon pc", "sur mon ordinateur", "mon disque", "mes documents", "ma mémoire", "mémoire long" };
            if (!string.IsNullOrWhiteSpace(rawQuery) &&
                !looksLocal.Any(rawQuery.ToLowerInvariant().Contains))
            {
                return $"https://www.google.com/search?q={Uri.EscapeDataString(rawQuery)}";
            }
        }

        return null;
    }

    private sealed record SiteSearchRequest(string Homepage, string Query);

    /// <summary>
    /// Détecte « cherche X sur amazon/cdiscount/... » pour le fast-path
    /// déterministe (navigate + site_search en un bloc, sans round LLM).
    /// </summary>
    private static SiteSearchRequest? DetectSiteSearch(string userMessage)
    {
        var lower = userMessage.ToLowerInvariant().Trim();
        var match = System.Text.RegularExpressions.Regex.Match(lower,
            @"(?:cherche|trouve|regarde|affiche|montre)(?:[- ]moi)?(?:\s+\w+)*?\s+(.+?)\s+sur\s+(amazon|cdiscount|fnac|leboncoin)");
        if (!match.Success) return null;

        var query = CleanSearchQuery(userMessage[match.Groups[1].Index..].Trim());
        if (string.IsNullOrWhiteSpace(query)) return null;

        var site = match.Groups[2].Value.Trim();
        return new SiteSearchRequest(site switch
        {
            "amazon" => "https://www.amazon.fr",
            "cdiscount" => "https://www.cdiscount.com",
            "fnac" => "https://www.fnac.com",
            "leboncoin" => "https://www.leboncoin.fr",
            _ => $"https://www.{site}.com"
        }, query);
    }

    private static string? ExtractSearchQuery(string userMessage)
    {
        var lower = userMessage.ToLowerInvariant().Trim();
        var searchOnMatch = System.Text.RegularExpressions.Regex.Match(lower,
            @"(?:cherche|trouve|regarde|affiche)\s+(.+?)\s+sur\s+(amazon|cdiscount|fnac|leboncoin|youtube|google)");
        return searchOnMatch.Success ? CleanSearchQuery(userMessage[searchOnMatch.Groups[1].Index..].Trim()) : null;
    }

    /// <summary>
    /// Retire les articles et mots vides en tête de requête (« un clavier
    /// mécanique » → « clavier mécanique ») : les moteurs de recherche des sites
    /// donnent de meilleurs résultats sans déterminants.
    /// </summary>
    private static string CleanSearchQuery(string query)
    {
        var trimmed = query.Trim().TrimEnd('.', '!', '?', ',').Trim();
        var cleaned = System.Text.RegularExpressions.Regex.Replace(
            trimmed,
            @"^(?:un|une|le|la|les|l'|des|du|de la|d'|moi un|me un)\s+",
            "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        cleaned = cleaned.Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? trimmed : cleaned;
    }

    public async IAsyncEnumerable<string> StreamChatAsync(string userMessage, AIConversation? conversation = null, string? model = null, ModelSelectionMode mode = ModelSelectionMode.Powerful, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Fast-path trivial : réponse immédiate sans LLM/outils/verif (corrige "Jarvis écrit..." bloqué)
        if (IsTrivialMessage(userMessage))
        {
            _logger.LogInformation("[AGENT] Fast-path trivial (hard-coded, no LLM): '{Msg}'", userMessage);
            var tid = Guid.NewGuid();
            _taskHistory?.StartRecording(userMessage, tid);
            _taskHistory?.AddStep(TaskExecutionStep.Thought($"Processing request: {TruncateText(userMessage, 120)}"));
            var friendly = "Salut ! Comment puis-je t'aider aujourd'hui ?";
            _taskHistory?.AddStep(TaskExecutionStep.Final(friendly, 0));
            _taskHistory?.Complete(friendly, true);
            yield return friendly;
            yield break;
        }
        conversation ??= new AIConversation(await BuildSystemPromptWithMemoryAsync(BuildToolDefinitions(userMessage), cancellationToken));
        conversation.AddUserMessage(userMessage);

        var correlationId = Guid.NewGuid();
        _taskHistory?.StartRecording(userMessage, correlationId);
        _taskHistory?.AddStep(TaskExecutionStep.Thought($"Processing request: {TruncateText(userMessage, 120)}"));

        // Check if computer_action is available — if so, skip all browser heuristics
        // and let the model use computer_action for local app interactions
        var hasComputerAction = _toolRegistry.GetAll().Any(t => t.Name == "computer_action");

        // ComfyUI install heuristic
        if (userMessage.Contains("installe comfui", StringComparison.OrdinalIgnoreCase) ||
            userMessage.Contains("install comfui", StringComparison.OrdinalIgnoreCase) ||
            userMessage.Contains("installe comfyui", StringComparison.OrdinalIgnoreCase) ||
            userMessage.Contains("install comfyui", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("[AGENT] ComfyUI install requested");
            _taskHistory?.AddStep(TaskExecutionStep.Thought("Installation ComfyUI demandée"));
            _taskHistory?.AddStep(TaskExecutionStep.Final("Démarrage de l'installation ComfyUI...", 0));
            _taskHistory?.Complete("Démarrage de l'installation ComfyUI...", true);

            // Trigger ComfyUI setup
            var comfySetup = _serviceProvider.GetService(typeof(JarvisAI.Application.Abstractions.IComfyUISetup)) as JarvisAI.Application.Abstractions.IComfyUISetup;
            comfySetup?.StartSetupIfNeeded();

            yield return "📦 Installation de ComfyUI lancée en arrière-plan.\n\n" +
                         "⏳ Téléchargement de ComfyUI + modèle RealVisXL (~2 Go)\n" +
                         "💾 Emplacement : %LOCALAPPDATA%\\JarvisAI\\ComfyUI\n\n" +
                         "Tu pourras générer des images une fois l'installation terminée. " +
                         "Je te préviens quand c'est prêt !";
            yield break;
        }

        // Pre-processing heuristic: intercept common Chrome follow-up actions
        // ONLY when computer_action is NOT available (otherwise, let the model handle it)
        if (!hasComputerAction && DetectChromeFollowUp(userMessage) is { } followUp)
        {
            _logger.LogInformation("[AGENT] Pre-processing heuristic: detected Chrome follow-up '{Pattern}' → send_keys key={Key}", followUp.Pattern, followUp.Key);
            _taskHistory?.AddStep(TaskExecutionStep.Thought($"Heuristique : action Chrome détectée → send_keys key={followUp.Key}"));

            var toolContext = new AgentContext(
                "browser",
                source: "heuristic",
                new Dictionary<string, object>
                {
                    ["toolCallId"] = Guid.NewGuid().ToString("N")[..12],
                    ["arguments"] = new Dictionary<string, string> { ["action"] = "send_keys", ["key"] = followUp.Key }
                });

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var toolResult = await ExecuteToolSafeAsync(_toolExecutor, "browser", toolContext, cancellationToken);
            sw.Stop();

            var resultText = toolResult.Success ? toolResult.Output : $"Erreur : {toolResult.ErrorMessage}";
            _logger.LogInformation("[AGENT] Heuristic result ({Duration}ms): {Result}", sw.ElapsedMilliseconds, TruncateText(resultText, 100));
            _taskHistory?.AddStep(TaskExecutionStep.Tool("browser", $"send_keys key={followUp.Key}", sw.ElapsedMilliseconds, toolResult.Success, TruncateText(resultText, 200)));

            conversation.AddAssistantMessage($"[Action exécutée via heuristique : browser send_keys key={followUp.Key}]");
            _taskHistory?.AddStep(TaskExecutionStep.Final(resultText, 0));
            _taskHistory?.Complete(resultText, true);
            yield return resultText;
            yield break;
        }

        // FAST-PATH « cherche X sur SITE » : pipeline déterministe navigate +
        // site_search en un bloc (zéro round LLM pour la recherche), puis une
        // seule ronde de synthèse sur le modèle rapide. Façon repo Python.
        if (DetectSiteSearch(userMessage) is { } siteSearch)
        {
            _logger.LogInformation("[AGENT] Fast-path site_search : {Query} sur {Url}", siteSearch.Query, siteSearch.Homepage);
            _taskHistory?.AddStep(TaskExecutionStep.Thought($"Fast-path : recherche « {siteSearch.Query} » directement sur le site"));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var toolContext = new AgentContext(
                "browser",
                source: "heuristic",
                new Dictionary<string, object>
                {
                    ["toolCallId"] = Guid.NewGuid().ToString("N")[..12],
                    ["arguments"] = new Dictionary<string, string>
                    {
                        ["action"] = "site_search",
                        ["url"] = siteSearch.Homepage,
                        ["text"] = siteSearch.Query
                    }
                });
            var toolResult = await ExecuteToolSafeAsync(_toolExecutor, "browser", toolContext, cancellationToken);
            sw.Stop();

            var resultText = toolResult.Success ? toolResult.Output : $"Erreur : {toolResult.ErrorMessage}";
            _taskHistory?.AddStep(TaskExecutionStep.Tool("browser", $"site_search \"{siteSearch.Query}\"", sw.ElapsedMilliseconds, toolResult.Success, TruncateText(resultText, 300)));

            // Une seule ronde LLM (modèle rapide) pour reformuler les résultats.
            conversation.AddAssistantMessage($"[Recherche effectuée automatiquement via browser site_search]");
            conversation.AddUserMessage(
                $"La recherche « {siteSearch.Query} » a déjà été effectuée sur le site. Résultats bruts :\n\n" +
                $"{TruncateText(resultText, 4000)}\n\n" +
                "Présente ces résultats clairement à l'utilisateur (top 5 max, prix et notes inclus). " +
                "Ne refais PAS la recherche. N'invente RIEN qui ne soit pas dans les résultats.");
            var fastModelRoute = _router.Resolve(userMessage, conversation, ModelSelectionMode.Fast);
            var fastModel = _budget?.ApplyFallback(ResolveModel(null, fastModelRoute)) ?? ResolveModel(null, fastModelRoute);

            var request = new AIRequest(
                systemPrompt: conversation.SystemPrompt,
                messages: conversation.ToRequestMessages(),
                tools: Array.Empty<AIToolDefinition>(),
                model: fastModel,
                temperature: 0.2f);
            var summary = new StringBuilder();
            await foreach (var chunk in _provider.StreamChatAsync(request, cancellationToken))
            {
                summary.Append(chunk.Token);
                yield return chunk.Token ?? "";
            }
            var finalSummary = summary.ToString();
            _taskHistory?.AddStep(TaskExecutionStep.Final(finalSummary, 0));
            _taskHistory?.Complete(finalSummary, true);
            yield break;
        }

        // Pre-processing: detect "va sur...", "ouvre...", "navigue vers..." → force browser navigate
        // ONLY when computer_action is NOT available
        if (!hasComputerAction && DetectWebNavigation(userMessage) is { } navUrl)
        {
            _logger.LogInformation("[AGENT] Pre-processing: detected navigation to {Url}", navUrl);
            _taskHistory?.AddStep(TaskExecutionStep.Thought($"Heuristique : navigation web détectée → browser navigate url={navUrl}"));

            var toolContext = new AgentContext(
                "browser",
                source: "heuristic",
                new Dictionary<string, object>
                {
                    ["toolCallId"] = Guid.NewGuid().ToString("N")[..12],
                    ["arguments"] = new Dictionary<string, string> { ["action"] = "navigate", ["url"] = navUrl }
                });

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var toolResult = await ExecuteToolSafeAsync(_toolExecutor, "browser", toolContext, cancellationToken);
            sw.Stop();

            var resultText = toolResult.Success ? toolResult.Output : $"Erreur : {toolResult.ErrorMessage}";
            _taskHistory?.AddStep(TaskExecutionStep.Tool("browser", $"navigate url={navUrl}", sw.ElapsedMilliseconds, toolResult.Success, TruncateText(resultText, 200)));

            // Add navigation result to conversation, then let agent continue
            conversation.AddAssistantMessage($"[Navigation effectuée : {navUrl}]");
            var searchQuery = ExtractSearchQuery(userMessage);
            var isGoogleResults = navUrl.Contains("google.com/search", StringComparison.OrdinalIgnoreCase);
            var continuation = isGoogleResults
                ? $"La page de résultats Google est ouverte. Voici son contenu et la liste des éléments numérotés :\n\n{TruncateText(resultText, 4000)}\n\n" +
                  $"Repère dans la liste le lien du meilleur résultat (les liens des résultats ont souvent une URL sous forme /url?q= ou sont dans un <a> avec un titre), " +
                  $"puis browser action=click_index index=N pour l'ouvrir. " +
                  $"Si la tâche est répondue par les extraits de résultats directement (météo, calcul, définition), réponds sans cliquer."
                : searchQuery is null
                ? $"La page est maintenant ouverte. Voici son contenu et la liste des éléments numérotés :\n\n{TruncateText(resultText, 4000)}\n\n" +
                  $"Continue la tâche demandée par l'utilisateur : {userMessage}. " +
                  "Pour cliquer un lien ou un bouton : browser action=click_index index=N (N = numéro affiché). " +
                  "Pour remplir une barre de recherche : browser action=fill_index index=N text=\"...\" puis browser action=press key=Enter."
                : $"La page est maintenant ouverte. Voici son contenu et la liste des éléments numérotés :\n\n{TruncateText(resultText, 4000)}\n\n" +
                  $"Cherche « {searchQuery} » sur ce site : repère dans la liste l'input de recherche (input/search ou placeholder « Rechercher »), " +
                  $"puis browser action=fill_index index=N text=\"{searchQuery}\", puis browser action=press key=Enter. " +
                  $"Si aucun champ de recherche n'apparaît, fais browser action=view pour rafraîchir la liste. " +
                  $"INTERDIT de construire ou de naviguer vers une URL contenant des paramètres (?k=, /s?, search_query=, qid=...).";
            conversation.AddUserMessage(continuation);
            // Fall through to normal LLM flow — agent will see the page and continue
        }

        var route = _router.Resolve(userMessage, conversation, mode);
        var effectiveModel = _budget?.ApplyFallback(ResolveModel(model, route)) ?? ResolveModel(model, route);
        _logger.LogInformation("[AGENT] Routing: mode={Mode}, model={Model}, profile={Profile}, reason={Reason}",
            mode, effectiveModel, route.Profile, route.Reason);

        var cached = _responseCache?.TryGet(userMessage, effectiveModel);
        if (cached is not null)
        {
            _logger.LogInformation("[AGENT] Cache hit: \"{Preview}\"", TruncateText(cached.Content, 80));
            _taskHistory?.AddStep(TaskExecutionStep.Final(cached.Content, 0));
            _taskHistory?.Complete(cached.Content, true);
            yield return cached.Content;
            yield break;
        }

        if (!await _provider.IsAvailableAsync(cancellationToken))
        {
            const string fallback = "[Erreur : aucun moteur IA disponible (Ollama arrêté ?). Démarre Ollama puis réessaie.]";
            _logger.LogWarning("[AGENT] Aucun fournisseur IA disponible; réponse de repli");
            _taskHistory?.AddStep(TaskExecutionStep.Error("Aucun fournisseur IA disponible"));
            _taskHistory?.Complete(null, false, "Aucun fournisseur IA disponible");
            yield return fallback;
            yield break;
        }

        var supportsTools = ModelCapabilities.SupportsTools(effectiveModel);
        var toolDefinitions = supportsTools ? BuildToolDefinitions(userMessage) : Array.Empty<AIToolDefinition>();
        var toolNames = string.Join(", ", toolDefinitions.Select(t => t.Name));
        _logger.LogInformation("[AGENT] User request: \"{Message}\"", userMessage);
        _logger.LogInformation("[AGENT] Tools available ({Count}): {Tools}", toolDefinitions.Count, toolNames);
        if (!supportsTools)
            _logger.LogWarning("[AGENT] Model '{Model}' does not support tools; tool loop disabled", effectiveModel);

        var plan = await TryGeneratePlanAsync(userMessage, conversation, cancellationToken);
        if (plan is not null)
        {
            conversation.AddMessage(AIMessage.System(
                "PLAN INTERNE (exécute-le via les outils puis réponds normalement, sans répéter le plan verbatim) :\n" + plan));
            _taskHistory?.AddStep(TaskExecutionStep.Thought($"Plan généré: {TruncateText(plan, 150)}"));
        }

        var rounds = 0;
        const int maxRounds = 15;
        const int maxConsecutiveRefusals = 2;
        var consecutiveRefusals = 0;
        var toolCallsExecuted = 0;
        var verificationDone = false;
        var dedupRetried = false;
        var toolCallCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var createdToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var loopRecoveries = 0;
        const int maxLoopRecoveries = 2;
        var transientRetryCount = 0;
        const string loopRecoveryMessage =
            "ALERTE ANTI-BOUCLE : tu répètes un appel d'outil identique. ARRÊTE-TOI. " +
            "Si un outil a déjà réussi (le résultat contient « ACTION TERMINÉE » ou « Ouvert dans le navigateur »), TA TÂCHE EST TERMINÉE : donne ta réponse finale MAINTENANT. " +
            "Si l'outil a échoué, change-radicalement de stratégie : " +
            "rafraîchir la page → browser action=view ; " +
            "chercher sur un site → browser action=fill_index index=N text='...' (N = numéro de la barre de recherche) puis press Enter ; " +
            "cliquer un lien → browser action=click_index index=N. " +
            "Tu n'as PAS de web_search. Tu dois TOUT faire via le navigateur, par NUMÉROS d'éléments. " +
            "N'invente JAMAIS de noms d'outils (pas de 'fetch_page' comme outil, pas de 'open' comme action). " +
            "Ne fais PAS plus de 3 appels d'outil au total pour une tâche simple.";

        while (rounds < maxRounds)
        {
            rounds++;
            _logger.LogInformation("[AGENT] Round {Round}/{MaxRounds}", rounds, maxRounds);

            var request = new AIRequest(
                systemPrompt: conversation.SystemPrompt,
                messages: conversation.ToRequestMessages(),
                tools: toolDefinitions,
                model: effectiveModel,
                temperature: 0.2f);

            var content = new StringBuilder();
            IReadOnlyList<AIToolCall>? toolCalls = null;
            string? streamError = null;
            int roundPromptTokens = 0, roundCompletionTokens = 0;

            await foreach (var chunk in StreamProviderSafelyAsync(_provider, request, cancellationToken))
            {
                if (chunk.Error is not null)
                {
                    streamError = chunk.Error;
                    break;
                }
                if (chunk.ToolCalls is { Count: > 0 } && toolCalls is null)
                    toolCalls = chunk.ToolCalls;
                if (chunk.Token is not null)
                {
                    content.Append(chunk.Token);
                    yield return chunk.Token;
                }
                if (chunk.PromptTokens > 0) roundPromptTokens = chunk.PromptTokens;
                if (chunk.CompletionTokens > 0) roundCompletionTokens += chunk.CompletionTokens;
            }

            if (_budget is not null && (roundPromptTokens > 0 || roundCompletionTokens > 0))
            {
                try { await _budget.RecordAsync(effectiveModel, roundPromptTokens, roundCompletionTokens, cancellationToken); }
                catch (Exception ex) { _logger.LogWarning(ex, "[AGENT] Budget tracking échoué"); }
            }

            if (streamError is not null)
            {
                _logger.LogError("[AGENT] LLM stream error: {Error}", streamError);
                _taskHistory?.AddStep(TaskExecutionStep.Error($"LLM stream error: {streamError}"));
                _taskHistory?.Complete(null, false, streamError);
                yield return $"[Error: {streamError}]";
                yield break;
            }

            var responseContent = content.ToString();

            // Si le modèle renvoie du JSON de classification au lieu d'une réponse,
            // on re-prompt pour obtenir une vraie réponse.
            if (IsClassificationJson(responseContent))
            {
                _logger.LogWarning("[AGENT] Classification JSON détectée dans la réponse; re-prompt");
                conversation.AddAssistantMessage(responseContent);
                conversation.AddMessage(AIMessage.System(
                    "Tu as renvoyé du JSON de classification au lieu de répondre à l'utilisateur. " +
                    "Ne renvoie JAMAIS de JSON. Donne une réponse NATURELLE et DIRECTE en français."));
                continue;
            }

            if (supportsTools && toolCalls is { Count: > 0 })
            {
                if (IsToolLooping(toolCallCounts, toolCalls))
                {
                    if (loopRecoveries < maxLoopRecoveries)
                    {
                        loopRecoveries++;
                        _logger.LogWarning("[AGENT] Anti-boucle : même tool call répété; correction demandée (tentative {LoopRecoveries}/{Max})", loopRecoveries, maxLoopRecoveries);
                        _taskHistory?.AddStep(TaskExecutionStep.Thought($"Anti-boucle : répétition d'un tool call détectée; correction demandée ({loopRecoveries}/{maxLoopRecoveries})"));
                        conversation.AddAssistantWithToolCalls(responseContent, toolCalls);
                        conversation.AddMessage(AIMessage.System(loopRecoveryMessage));
                        continue;
                    }
                    _logger.LogWarning("[AGENT] Anti-boucle : le même tool call est répété; interruption de la boucle");
                    _taskHistory?.AddStep(TaskExecutionStep.Error("Anti-boucle : tool call répété, interruption"));
                    conversation.AddAssistantWithToolCalls(responseContent, toolCalls);
                    _taskHistory?.Complete(responseContent, true);
                    if (!string.IsNullOrWhiteSpace(responseContent))
                        yield return responseContent;
                    yield break;
                }

                if (IsRecreatingTool(toolCalls, createdToolNames))
                {
                    _logger.LogInformation("[AGENT] Anti-boucle : recréation d'un outil déjà créé; correction demandée");
                    _taskHistory?.AddStep(TaskExecutionStep.Thought("Anti-boucle : recréation d'un outil existant évitée"));
                    conversation.AddAssistantWithToolCalls(responseContent, toolCalls);
                    conversation.AddMessage(AIMessage.System(
                        "L'OUTIL QUE TU VIENS DE CRÉER EXISTE DÉJÀ (tu l'as créé plus tôt dans cette même conversation). " +
                        "Ne le recrée PAS et ne crée pas de nouvelle version. " +
                        "Appelle-le directement maintenant avec ses paramètres réels, ou donne ta réponse finale."));
                    continue;
                }

                _logger.LogInformation("[AGENT] LLM requested {Count} tool call(s)", toolCalls.Count);
                conversation.AddAssistantWithToolCalls(responseContent, toolCalls);

                // Phrase d'attente : si un outil lent est demandé, on l'annonce tout de suite.
                var waitingTool = toolCalls
                    .Select(tc => _toolRegistry.GetByName(tc.Name))
                    .FirstOrDefault(t => t?.WaitingPhrase is not null);
                if (waitingTool is not null)
                {
                    yield return waitingTool.WaitingPhrase + "\n\n";
                }

                var toolResultContents = new List<string>();
                foreach (var toolCall in toolCalls)
                {
                    var argsDesc = string.Join(", ", toolCall.Arguments.Select(kv => $"{kv.Key}={kv.Value}"));
                    _logger.LogInformation("[AGENT] Executing tool: {Name}({Args})", toolCall.Name, argsDesc);

                    var toolContext = new AgentContext(
                        toolCall.Name,
                        source: "ai_service",
                        new Dictionary<string, object>
                        {
                            ["toolCallId"] = toolCall.Id,
                            ["arguments"] = toolCall.Arguments
                        });

                    toolCallsExecuted++;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var toolResult = await ExecuteToolSafeAsync(_toolExecutor, toolCall.Name, toolContext, cancellationToken);
                    sw.Stop();

                    var duration = sw.ElapsedMilliseconds;
                    var resultContent = toolResult.Success
                        ? toolResult.Output
                        : $"Error: {toolResult.ErrorMessage}";

                    _debugFeed?.Record(new JarvisAI.Application.Debug.DebugToolCall
                    {
                        Timestamp = DateTimeOffset.UtcNow,
                        ToolName = toolCall.Name,
                        Arguments = toolCall.Arguments,
                        Output = toolResult.Output ?? string.Empty,
                        Success = toolResult.Success,
                        Error = toolResult.ErrorMessage,
                        DurationMs = duration
                    });

                    _taskHistory?.AddStep(TaskExecutionStep.Tool(
                        toolCall.Name,
                        argsDesc,
                        duration,
                        toolResult.Success,
                        toolResult.Success ? TruncateText(toolResult.Output ?? "", 200) : toolResult.ErrorMessage ?? "Erreur inconnue"));

                    _logger.LogInformation("[AGENT] Tool {Name} result ({Duration}ms): {Success}",
                        toolCall.Name, duration, toolResult.Success ? "OK" : "FAILED");

                    if (!toolResult.Success)
                        _logger.LogWarning("[AGENT] Tool {Name} error: {Error}", toolCall.Name, toolResult.ErrorMessage);

                    toolResultContents.Add(resultContent);
                    conversation.AddToolResult(toolCall.Id, toolCall.Name, resultContent);

                    if (!toolResult.Success && IsTransientToolError(toolResult.ErrorMessage)
                        && transientRetryCount < 2)
                    {
                        transientRetryCount++;
                        _logger.LogWarning("[AGENT] Transient tool error — retry {Count}/2", transientRetryCount);
                        conversation.AddMessage(AIMessage.System(
                            $"L'outil {toolCall.Name} a échoué temporairement (retry {transientRetryCount}/2). Réémet le même tool call."));
                    }
                }

                var hasSuccessfulOpen = toolResultContents.Any(r =>
                    r.Contains("ACTION TERMINÉE", StringComparison.OrdinalIgnoreCase) ||
                    r.Contains("Ouvert dans le navigateur", StringComparison.OrdinalIgnoreCase));
                if (hasSuccessfulOpen)
                {
                    conversation.AddMessage(AIMessage.System(
                        "UN OUTIL A RÉUSSI ET A OUVERT/LANCÉ QUELQUE CHOSE. TA TÂCHE EST TERMINÉE. " +
                        "N'appelle AUCUN autre outil. Donne ta réponse finale MAINTENANT en décrivant ce qui a été fait."));
                    continue;
                }

                if (toolResultContents.Any(r => r.StartsWith("Error:")) && rounds > 3)
                {
                    var newPlan = await TryGeneratePlanAsync(
                        $"Previous plan partially failed. Original: {userMessage}. Errors so far.",
                        conversation, cancellationToken);
                    if (newPlan is not null)
                        conversation.AddMessage(AIMessage.System("NOUVEAU PLAN (adapte-toi) :\n" + newPlan));
                }

                continue;
            }

            if (string.IsNullOrWhiteSpace(responseContent))
            {
                _logger.LogWarning("[AGENT] Empty LLM response (round {Round}); re-prompting", rounds);
                conversation.AddMessage(AIMessage.System("Ta précédente réponse était vide. Donne une réponse finale décrivant ce qui a été fait, ou émets un appel d'outil pour continuer la tâche."));
                yield return IAIService.StreamRestartMarker;
                continue;
            }

            if (supportsTools && IsRefusalResponse(responseContent))
            {
                consecutiveRefusals++;
                if (consecutiveRefusals >= maxConsecutiveRefusals)
                {
                    _logger.LogWarning("[AGENT] {Count} consecutive refusals; returning last response as final", consecutiveRefusals);
                    conversation.AddAssistantMessage(responseContent);
                    _taskHistory?.AddStep(TaskExecutionStep.Error($"{consecutiveRefusals} consecutive refusals; returning last response as final"));
                    _taskHistory?.Complete(responseContent, true);
                    yield break;
                }
                _logger.LogWarning("[AGENT] Refusal detected: \"{Preview}\"", TruncateText(responseContent, 100));
                conversation.AddAssistantMessage(responseContent);
                conversation.AddMessage(AIMessage.System("TA RÉPONSE PRÉCÉDENTE ÉTAIT INCORRECTE. Tu as seulement promis de faire l'action sans APPELER aucun outil. Tu DOIS émettre un vrai appel de fonction en utilisant les outils disponibles et leurs noms de paramètres exacts. Ne te contente pas de dire que tu vas le faire, ne décris pas l'action en prose, n'utilise pas de placeholders. Attends le résultat de l'outil, puis donne la réponse finale."));
                yield return IAIService.StreamRestartMarker;
                continue;
            }

            consecutiveRefusals = 0;

            var textToolCalls = supportsTools
                ? TryParseTextToolCalls(responseContent, toolDefinitions)
                : new List<(string Name, Dictionary<string, string> Args)>();
            if (textToolCalls.Count > 0)
            {
                var calls = textToolCalls
                    .Select(c => new AIToolCall(Guid.NewGuid().ToString("N")[..12], c.Name, c.Args))
                    .ToList();

                if (IsToolLooping(toolCallCounts, calls))
                {
                    if (loopRecoveries < maxLoopRecoveries)
                    {
                        loopRecoveries++;
                        _logger.LogWarning("[AGENT] Anti-boucle : même tool call répété; correction demandée (tentative {LoopRecoveries}/{Max})", loopRecoveries, maxLoopRecoveries);
                        _taskHistory?.AddStep(TaskExecutionStep.Thought($"Anti-boucle : répétition d'un tool call détectée; correction demandée ({loopRecoveries}/{maxLoopRecoveries})"));
                        conversation.AddAssistantWithToolCalls(responseContent, calls);
                        conversation.AddMessage(AIMessage.System(loopRecoveryMessage));
                        continue;
                    }
                    _logger.LogWarning("[AGENT] Anti-boucle : le même tool call est répété; interruption de la boucle");
                    _taskHistory?.AddStep(TaskExecutionStep.Error("Anti-boucle : tool call répété, interruption"));
                    _taskHistory?.Complete(responseContent, true);
                    conversation.AddAssistantWithToolCalls(responseContent, calls);
                    yield break;
                }

                if (IsRecreatingTool(calls, createdToolNames))
                {
                    _logger.LogInformation("[AGENT] Anti-boucle : recréation d'un outil déjà créé; correction demandée");
                    _taskHistory?.AddStep(TaskExecutionStep.Thought("Anti-boucle : recréation d'un outil existant évitée"));
                    conversation.AddAssistantWithToolCalls(responseContent, calls);
                    conversation.AddMessage(AIMessage.System(
                        "L'OUTIL QUE TU VIENS DE CRÉER EXISTE DÉJÀ (tu l'as créé plus tôt dans cette même conversation). " +
                        "Ne le recrée PAS et ne crée pas de nouvelle version. " +
                        "Appelle-le directement maintenant avec ses paramètres réels, ou donne ta réponse finale."));
                    continue;
                }

                conversation.AddAssistantWithToolCalls(responseContent, calls);

                var textToolResultContents = new List<string>();
                foreach (var toolCall in calls)
                {
                    _logger.LogInformation("[AGENT] Text-based tool call detected: {Name}", toolCall.Name);

                    var toolContext = new AgentContext(
                        toolCall.Name,
                        source: "ai_service",
                        new Dictionary<string, object>
                        {
                            ["toolCallId"] = toolCall.Id,
                            ["arguments"] = toolCall.Arguments
                        });

                    toolCallsExecuted++;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var toolResult = await ExecuteToolSafeAsync(_toolExecutor, toolCall.Name, toolContext, cancellationToken);
                    sw.Stop();

                    var duration = sw.ElapsedMilliseconds;
                    var resultContent = toolResult.Success
                        ? toolResult.Output
                        : $"Error: {toolResult.ErrorMessage}";

                    _debugFeed?.Record(new JarvisAI.Application.Debug.DebugToolCall
                    {
                        Timestamp = DateTimeOffset.UtcNow,
                        ToolName = toolCall.Name,
                        Arguments = toolCall.Arguments,
                        Output = toolResult.Output ?? string.Empty,
                        Success = toolResult.Success,
                        Error = toolResult.ErrorMessage,
                        DurationMs = duration
                    });

                    _taskHistory?.AddStep(TaskExecutionStep.Tool(
                        toolCall.Name,
                        string.Join(", ", toolCall.Arguments.Select(kv => $"{kv.Key}={kv.Value}")),
                        duration,
                        toolResult.Success,
                        toolResult.Success ? TruncateText(toolResult.Output ?? "", 200) : toolResult.ErrorMessage ?? "Erreur inconnue"));

                    _logger.LogInformation("[AGENT] Text tool {Name} result ({Duration}ms): {Success}",
                        toolCall.Name, duration, toolResult.Success ? "OK" : "FAILED");

                    if (!toolResult.Success)
                        _logger.LogWarning("[AGENT] Text tool {Name} error: {Error}", toolCall.Name, toolResult.ErrorMessage);

                    textToolResultContents.Add(resultContent);
                    conversation.AddToolResult(toolCall.Id, toolCall.Name, resultContent);

                    // Si le tool échoue à cause d'un paramètre manquant, corriger le LLM directement.
                    if (!toolResult.Success && toolResult.ErrorMessage is { } errMsg
                        && (errMsg.Contains("requis", StringComparison.OrdinalIgnoreCase)
                            || errMsg.Contains("paramètre", StringComparison.OrdinalIgnoreCase)))
                    {
                        var paramNames = string.Join(", ", toolCall.Arguments.Keys);
                        conversation.AddMessage(AIMessage.System(
                            $"L'outil {toolCall.Name} a échoué : {errMsg}. " +
                            $"Arguments reçus : [{paramNames}]. " +
                            $"Réémis le tool call avec les BONS noms de paramètres. " +
                            $"Ne demande PAS de confirmation, n'explique pas, émets directement le tool call."));
                    }
                }

                var hasSuccessfulOpenText = textToolResultContents.Any(r =>
                    r.Contains("ACTION TERMINÉE", StringComparison.OrdinalIgnoreCase) ||
                    r.Contains("Ouvert dans le navigateur", StringComparison.OrdinalIgnoreCase));
                if (hasSuccessfulOpenText)
                {
                    conversation.AddMessage(AIMessage.System(
                        "UN OUTIL A RÉUSSI ET A OUVERT/LANCÉ QUELQUE CHOSE. TA TÂCHE EST TERMINÉE. " +
                        "N'appelle AUCUN autre outil. Donne ta réponse finale MAINTENANT en décrivant ce qui a été fait."));
                    continue;
                }

                continue;
            }

            _logger.LogInformation("[AGENT] Final response ({Length} chars)", responseContent.Length);

            if (!verificationDone && toolCallsExecuted > 0 && _options.SelfVerificationEnabled)
            {
                verificationDone = true;
                var correction = await TryVerifyAsync(userMessage, responseContent, effectiveModel, cancellationToken);
                if (correction is not null)
                {
                    _logger.LogInformation("[AGENT] Auto-vérification : réponse incomplète, nouvelle tentative");
                    _taskHistory?.AddStep(TaskExecutionStep.Thought("Auto-vérification : correction demandée"));
                    conversation.AddAssistantMessage(responseContent);
                    conversation.AddMessage(AIMessage.System(correction));
                    yield return IAIService.StreamRestartMarker;
                    continue;
                }
            }

            if (_dedupGuard is not null && !dedupRetried && _dedupGuard.IsDuplicate(responseContent))
            {
                _logger.LogInformation("[AGENT] Anti-boucle : réponse quasi identique à une réponse récente; reformulation demandée");
                _taskHistory?.AddStep(TaskExecutionStep.Thought("Anti-boucle : reformulation de la réponse"));
                dedupRetried = true;
                conversation.AddAssistantMessage(responseContent);
                conversation.AddMessage(AIMessage.System(
                    "TA RÉPONSE PRÉCÉDENTE ÉTAIT QUASI IDENTIQUE À UNE RÉPONSE DÉJÀ DONNÉE. " +
                    "Reformule avec un contenu réellement nouveau : précise le résultat concret, " +
                    "ajoute les informations manquantes ou réponds plus brièvement. Ne répète pas le même texte."));
                yield return IAIService.StreamRestartMarker;
                continue;
            }

            conversation.AddAssistantMessage(responseContent);
            _dedupGuard?.Record(responseContent);
            if (toolCallsExecuted == 0)
                _responseCache?.Set(userMessage, effectiveModel, responseContent);
            _taskHistory?.AddStep(TaskExecutionStep.Final(responseContent, 0));
            _taskHistory?.Complete(responseContent, true);
            yield break;
        }

        _logger.LogWarning("[AGENT] Max rounds reached ({Max}) without final answer; forcing a final answer", maxRounds);
        _taskHistory?.AddStep(TaskExecutionStep.Thought("Limite d'itérations atteinte : réponse finale forcée"));

        conversation.AddMessage(AIMessage.System(
            "Tu as atteint la limite maximale d'itérations d'outils pour cette tâche. " +
            "Ne fais PLUS aucun appel d'outil. Donne maintenant ta réponse finale directement, " +
            "en répondant à la demande de l'utilisateur avec tout ce que tu as déjà obtenu."));

        var finalRequest = new AIRequest(
            systemPrompt: conversation.SystemPrompt,
            messages: conversation.ToRequestMessages(),
            tools: Array.Empty<AIToolDefinition>(),
            model: effectiveModel,
            temperature: 0.2f);

        var finalText = new StringBuilder();
        await foreach (var chunk in StreamProviderSafelyAsync(_provider, finalRequest, cancellationToken))
        {
            if (chunk.Error is not null) break;
            if (chunk.Token is not null)
            {
                finalText.Append(chunk.Token);
                yield return chunk.Token;
            }
        }

        var forced = finalText.ToString();
        if (!string.IsNullOrWhiteSpace(forced))
        {
            conversation.AddAssistantMessage(forced);
            _dedupGuard?.Record(forced);
            if (toolCallsExecuted == 0)
                _responseCache?.Set(userMessage, effectiveModel, forced);
            _taskHistory?.AddStep(TaskExecutionStep.Final(forced, 0));
            _taskHistory?.Complete(forced, true);
            yield break;
        }

        _logger.LogWarning("[AGENT] Max rounds reached ({Max}) with no usable final answer", maxRounds);
        _taskHistory?.AddStep(TaskExecutionStep.Error($"Max rounds reached ({maxRounds}) without final answer"));
        _taskHistory?.Complete(null, false, $"Max rounds reached ({maxRounds}) without final answer");
        yield return "[Désolé, j'ai atteint la limite d'itérations sans pouvoir finaliser. " +
                     "Peux-tu reformuler ta demande de manière plus simple ?]";
    }

    private static readonly HashSet<string> PlanTriggerKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "et", "puis", "ensuite", "plan", "étapes", "etapes", "projet", "programme",
        "compare", "comparez", "résumé", "resume", "analyse", "analyser", "recherche",
        "organise", "organisez", "prépare", "prepare", "installe", "installez", "configure",
        "un script", "un programme", "un plan"
    };

    private async Task<string?> TryGeneratePlanAsync(string userMessage, AIConversation conversation, CancellationToken cancellationToken)
    {
        if (!_options.HiddenPlanningEnabled) return null;
        if (string.IsNullOrWhiteSpace(userMessage) || userMessage.Length < _options.HiddenPlanningMinChars) return null;

        var userTurns = conversation.ToRequestMessages().Count(m => m.Role == AIMessageRole.User);
        if (userTurns > _options.HiddenPlanningMaxUserTurns) return null;

        var lower = userMessage.ToLowerInvariant();
        var keywordHit = PlanTriggerKeywords.Any(k => lower.Contains(k));
        if (!keywordHit && userMessage.Length < 400) return null;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            var route = _router.Resolve(userMessage, conversation, ModelSelectionMode.Fast);

            var prompt =
                "Tu es un planificateur discret. La demande de l'utilisateur est : \"" + userMessage + "\".\n" +
                "Si la demande est complexe et nécessite plusieurs actions, donne un plan d'exécution très concis (2 à 6 étapes), " +
                "en pensant aux outils disponibles (recherche web, météo, news, fichiers, terminal, navigateur, calculatrice...).\n" +
                "Si la demande est simple, renvoie un plan vide.\n" +
                "Réponds UNIQUEMENT avec du JSON valide au format : {\"plan\": [\"étape 1\", \"étape 2\"]}";

            var response = await _inner.ChatAsync(prompt, conversation: null, route.Model, cts.Token);
            if (!response.Success || string.IsNullOrWhiteSpace(response.Content)) return null;

            using var doc = System.Text.Json.JsonDocument.Parse(response.Content);
            if (!doc.RootElement.TryGetProperty("plan", out var planEl) || planEl.ValueKind != System.Text.Json.JsonValueKind.Array)
                return null;

            var steps = new List<string>();
            foreach (var step in planEl.EnumerateArray())
            {
                if (step.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var s = step.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(s)) steps.Add("- " + s);
                }
            }
            return steps.Count > 0 ? string.Join("\n", steps) : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[AGENT] Planification cachée indisponible, poursuite sans plan");
            return null;
        }
    }

    private async Task<string?> TryVerifyAsync(string userMessage, string responseContent, string model, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(responseContent)) return null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            var prompt =
                "Tu vérifies si une réponse d'assistant répond réellement à la demande de l'utilisateur.\n" +
                "DEMANDE : \"" + userMessage + "\"\n" +
                "RÉPONSE : \"" + TruncateText(responseContent, 2000) + "\"\n" +
                "Réponds UNIQUEMENT en JSON : {\"complete\": true ou false, \"correction\": \"ce qui manque ou à corriger (ou vide)\"}.\n" +
                "complete=false seulement si la demande n'est manifestement pas satisfaite (aucun résultat, hors sujet, étape importante omise).";
            var fastModel = _router.Resolve("", null, ModelSelectionMode.Fast).Model;
            var response = await _inner.ChatAsync(prompt, conversation: null, fastModel, cts.Token);
            if (!response.Success || string.IsNullOrWhiteSpace(response.Content)) return null;

            using var doc = System.Text.Json.JsonDocument.Parse(response.Content);
            if (!doc.RootElement.TryGetProperty("complete", out var completeEl)) return null;
            if (completeEl.ValueKind == System.Text.Json.JsonValueKind.True) return null;
            if (!doc.RootElement.TryGetProperty("correction", out var correctionEl)
                || correctionEl.ValueKind != System.Text.Json.JsonValueKind.String)
                return null;
            var correction = correctionEl.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(correction)) return null;

            return "TA RÉPONSE PRÉCÉDENTE NE SATISFAISAIT PAS LA DEMANDE. Correction à appliquer : " + correction +
                   " Rédige maintenant la réponse complète (appelle des outils si nécessaire, puis donne le résultat final).";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[AGENT] Auto-vérification indisponible");
            return null;
        }
    }

    private static readonly Regex[] RefusalPatterns =
    {
        new(@"\bI\s+(cannot|can'?t)\s+(?:help|assist|do|perform|access|control)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b(?:cannot|can'?t)\s+access\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bdon'?t\s+have\s+(?:access|the\s+ability|permission)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b(?:I'?m|I\s+am)\s+not\s+(?:able|allowed|programmed)\s+to\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bno\s+(?:direct\s+)?access\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\byou\s+(?:need|should|must)\s+to\s+(?:run|execute|type|use|try|open)\s+(?:the\s+)?(?:following|this|these|command|script)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bje\s+ne\s+peux\s+pas\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bje\s+n'?ai\s+pas\s+(?:les?\s+)?(?:capacité|droit|permission|accès)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bexecut(?:e|ez)\s+(?:the\s+)?(?:following|this|these)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    };

    internal static bool IsRefusalResponse(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        // Si la réponse contient un appel d'outil (JSON ou textuel), ce n'est pas un refus.
        if (content.Contains("\"name\"") && content.Contains("\"parameters\"")) return false;
        if (content.Contains("\"name\"") && content.Contains("\"arguments\"")) return false;
        return RefusalPatterns.Any(p => p.IsMatch(content));
    }

    public static List<(string Name, Dictionary<string, string> Args)> TryParseTextToolCalls(string content, IReadOnlyList<AIToolDefinition> toolDefinitions)
    {
        var result = new List<(string Name, Dictionary<string, string> Args)>();
        if (string.IsNullOrWhiteSpace(content) || toolDefinitions.Count == 0) return result;

        result.AddRange(TryParseJsonToolCalls(content, toolDefinitions));

        var normalized = NormalizeToolCallMarkup(content, toolDefinitions);
        var toolNames = string.Join("|", toolDefinitions.Select(t => Regex.Escape(t.Name)));
        var matches = Regex.Matches(normalized, @"(?<name>" + toolNames + @")\s*\(", RegexOptions.Singleline);
        if (matches.Count == 0) return result;

        var currentSpanEnd = -1;
        foreach (Match match in matches)
        {
            if (match.Index < currentSpanEnd) continue;

            var toolName = match.Groups["name"].Value;

            var argsStart = match.Index + match.Length;
            var depth = 1;
            var argsEnd = argsStart;
            while (argsEnd < normalized.Length && depth > 0)
            {
                if (normalized[argsEnd] == '(') depth++;
                else if (normalized[argsEnd] == ')') depth--;
                if (depth > 0) argsEnd++;
            }

            if (depth != 0) continue;

            currentSpanEnd = argsEnd + 1;

            var argsStr = normalized[argsStart..argsEnd];
            var args = ParseToolCallArguments(argsStr);
            if (args == null) continue;

            result.Add((toolName, args));
        }

        return result;
    }

    private static List<(string Name, Dictionary<string, string> Args)> TryParseJsonToolCalls(string content, IReadOnlyList<AIToolDefinition> toolDefinitions)
    {
        var result = new List<(string Name, Dictionary<string, string> Args)>();
        var i = 0;
        while (i < content.Length)
        {
            var start = content.IndexOf('{', i);
            if (start < 0) break;

            var depth = 1;
            var end = start + 1;
            while (end < content.Length && depth > 0)
            {
                if (content[end] == '{') depth++;
                else if (content[end] == '}') depth--;
                end++;
            }

            if (depth != 0) break;

            var json = content[start..end];
            i = end;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != System.Text.Json.JsonValueKind.String)
                    continue;
                var name = nameEl.GetString();
                if (name is null || toolDefinitions.All(t => t.Name != name))
                    continue;
                // Accepte "arguments" ou "parameters" (selon le format du LLM)
                if (!root.TryGetProperty("arguments", out var argsEl))
                {
                    if (!root.TryGetProperty("parameters", out argsEl))
                        continue;
                }

                var args = new Dictionary<string, string>();
                if (argsEl.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    foreach (var prop in argsEl.EnumerateObject())
                    {
                        args[prop.Name] = prop.Value.ValueKind == System.Text.Json.JsonValueKind.String
                            ? prop.Value.GetString()!
                            : prop.Value.ToString();
                    }
                }
                result.Add((name, args));
            }
            catch
            {
                // Not valid JSON; skip this block
            }
        }

        return result;
    }

    private static string NormalizeToolCallMarkup(string content, IReadOnlyList<AIToolDefinition> toolDefinitions)
    {
        // Convert <tool name="X">(...)</tool> to X(...)
        content = Regex.Replace(content, @"<tool\s+name=\s*[""']([^""']+)[""']\s*>", "$1", RegexOptions.IgnoreCase);
        // Convert <browser>(...)</browser> to browser(...) for any known tool name
        var toolTags = string.Join("|", toolDefinitions.Select(t => Regex.Escape(t.Name)));
        content = Regex.Replace(content, @"<(" + toolTags + @")\s*>", "$1", RegexOptions.IgnoreCase);
        content = Regex.Replace(content, @"</(" + toolTags + @")\s*>", "", RegexOptions.IgnoreCase);
        // Remove any <tool ...> opening / closing tags
        content = Regex.Replace(content, @"</?tool(?:\s+[^>]*)?>", "", RegexOptions.IgnoreCase);
        return content;
    }

    public static (string Name, Dictionary<string, string> Args)? TryParseTextToolCall(string content, IReadOnlyList<AIToolDefinition> toolDefinitions)
    {
        var calls = TryParseTextToolCalls(content, toolDefinitions);
        return calls.Count > 0 ? calls[0] : null;
    }

    public static Dictionary<string, string>? ParseToolCallArguments(string argsStr)
    {
        var result = new Dictionary<string, string>();
        var remaining = argsStr.AsSpan().Trim();

        while (remaining.Length > 0)
        {
            var eqIndex = remaining.IndexOf('=');
            if (eqIndex < 0) return null;

            var key = remaining[..eqIndex].Trim().ToString();
            if (string.IsNullOrEmpty(key)) return null;

            remaining = remaining[(eqIndex + 1)..].TrimStart();

            string? value;
            if (remaining.Length > 0 && remaining[0] == '"')
            {
                remaining = remaining[1..];
                var endQuote = remaining.IndexOf('"');
                if (endQuote < 0)
                    return null;
                value = remaining[..endQuote].ToString();
                remaining = remaining[(endQuote + 1)..];
            }
            else if (remaining.Length > 0 && remaining[0] == '\'')
            {
                remaining = remaining[1..];
                var endQuote = remaining.IndexOf('\'');
                if (endQuote < 0) return null;
                value = remaining[..endQuote].ToString();
                remaining = remaining[(endQuote + 1)..];
            }
            else
            {
                var commaIndex = remaining.IndexOf(',');
                if (commaIndex < 0)
                {
                    value = remaining.Trim().ToString();
                    remaining = ReadOnlySpan<char>.Empty;
                }
                else
                {
                    value = remaining[..commaIndex].Trim().ToString();
                    remaining = remaining[(commaIndex + 1)..];
                }
            }

            if (value != null)
                result[key] = value;

            if (remaining.Length > 0 && remaining[0] == ',')
                remaining = remaining[1..].TrimStart();
        }

        return result;
    }

    private static string TruncateText(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength) return text;
        return text[..maxLength] + "...";
    }

    private static bool IsToolLooping(Dictionary<string, int> toolCallCounts, IReadOnlyList<AIToolCall>? toolCalls)
    {
        if (toolCalls is null || toolCalls.Count == 0) return false;
        foreach (var call in toolCalls)
        {
            var args = call.Arguments.Count > 0
                ? string.Join("|", call.Arguments.Select(kv => $"{kv.Key}={kv.Value}"))
                : "(none)";
            var signature = $"{call.Name}({args})";
            toolCallCounts[signature] = toolCallCounts.GetValueOrDefault(signature) + 1;
            if (toolCallCounts[signature] >= 3)
                return true;

            // Normalized: same tool + same arg keys = loop even with different values
            if (call.Arguments.Count > 0)
            {
                var keyOnlySignature = $"{call.Name}({string.Join("|", call.Arguments.Keys.OrderBy(k => k))})";
                var normKey = $"norm:{keyOnlySignature}";
                toolCallCounts[normKey] = toolCallCounts.GetValueOrDefault(normKey) + 1;
                if (toolCallCounts[normKey] >= 5)
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Détecte la recréation d'un outil déjà créé dans cette même conversation
    /// (le modèle boucle parfois en re-créant le même outil au lieu de l'utiliser).
    /// </summary>
    private static bool IsRecreatingTool(IReadOnlyList<AIToolCall>? toolCalls, HashSet<string> createdTools)
    {
        if (toolCalls is null || toolCalls.Count == 0) return false;
        foreach (var call in toolCalls)
        {
            if (!string.Equals(call.Name, "create_tool", StringComparison.OrdinalIgnoreCase)) continue;
            if (!call.Arguments.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name)) continue;
            if (!createdTools.Add(name.Trim())) return true;
        }
        return false;
    }

    private static bool IsTransientToolError(string? error)
    {
        if (string.IsNullOrEmpty(error)) return false;
        return error.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || error.Contains("429", StringComparison.OrdinalIgnoreCase)
            || error.Contains("ECONNREFUSED", StringComparison.OrdinalIgnoreCase)
            || error.Contains("503", StringComparison.OrdinalIgnoreCase)
            || error.Contains("temporairement", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<ToolResult> ExecuteToolSafeAsync(IToolExecutor executor, string toolName, AgentContext context, CancellationToken ct)
    {
        // Un outil indisponible (vision sans modèle, hermes non configuré…) ne
        // s'exécute JAMAIS : le modèle hallucine parfois des appels vus dans
        // l'historique. On renvoie une erreur claire pour le remettre sur browser.
        var tool = _toolRegistry.GetByName(toolName);
        if (tool is not null && !tool.IsAvailable)
        {
            _logger.LogWarning("[AGENT] Outil {Name} indisponible : appel refusé", toolName);
            return ToolResult.Failed($"L'outil {toolName} n'est pas disponible actuellement. Pour tout ce qui est web/YouTube, utilise UNIQUEMENT l'outil browser.");
        }

        var timer = AgentMetrics.Instance.StartTimer();
        try
        {
            var result = await executor.ExecuteAsync(toolName, context, ct);
            var ms = AgentMetrics.Instance.StopTimer(timer);
            AgentMetrics.Instance.RecordLatency($"tool:{toolName}", ms);
            AgentMetrics.Instance.Increment($"tool:{toolName}:{(result.Success ? "ok" : "fail")}");
            _toolUsage?.Record(toolName, (long)ms, result.Success);
            return result;
        }
        catch (Exception ex)
        {
            var ms = AgentMetrics.Instance.StopTimer(timer);
            AgentMetrics.Instance.RecordLatency($"tool:{toolName}", ms);
            AgentMetrics.Instance.Increment($"tool:{toolName}:fail");
            _toolUsage?.Record(toolName, (long)ms, false);
            return ToolResult.Failed(ex.Message);
        }
    }

    private static async IAsyncEnumerable<AIStreamChunk> StreamProviderSafelyAsync(
        IAIProvider provider,
        AIRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var enumerator = provider.StreamChatAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            string? error = null;
            bool hasNext;
            try
            {
                hasNext = await enumerator.MoveNextAsync();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await enumerator.DisposeAsync();
                throw;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                hasNext = false;
            }

            if (error is not null)
            {
                await enumerator.DisposeAsync();
                yield return new AIStreamChunk(Error: error);
                yield break;
            }

            if (!hasNext)
            {
                await enumerator.DisposeAsync();
                yield break;
            }

            yield return enumerator.Current;
        }
    }

    // computer_use removed from AlwaysPrunedOut — it's replaced by computer_action which is always included
    private static readonly string[] AlwaysPrunedOut =
    {
        "computer", "ui_elements", "terminal", "windows", "web_search"
    };

    // French screen-interaction intent words — when present, computer_use must NOT be pruned
    private static readonly string[] ScreenIntentWords =
    {
        "clique", "clic", "bouton", "écran", "souris", "clavier", "tape", "sélectionne",
        "déplace", "ferme", "ouvre", "rolle", "scroll", "appuie", "press", "click",
        "sur l'écran", "dans la", "remplis", "remplir", "colorie", "peins", "dessine",
        "fond", "noir", "blanc", "rouge", "bleu", "vert", "jaune", "paint", "photoshop",
        "excel", "word", "blender", "interface", "menu", "barre", "onglet", "fenêtre"
    };

    private static readonly string[] GeneralAlwaysTools =
    {
        "system_info", "date_time", "memory", "news", "weather",
        "calculator", "read_document", "set_reminder", "timer", "computer_action"
    };

    // Requêtes explicitement liées à une génération d'image.
    private static readonly string[] ImageGenIntentWords =
    {
        "génère une image", "genere une image", "génère-moi", "genere-moi", "crée une image",
        "cree une image", "crée-moi", "cree-moi", "dessine", "create an image", "generate an image",
        "image de", "une image", "une photo de", "a picture of", "draw me"
    };

    // Mots décrivant une scène visuelle : déclenche le générateur d'images même sans mot « image ».
    private static readonly string[] VisualSceneWords =
    {
        "dans les montagnes", "au bord", "autour du lac", "sur la plage", "ciel bleu",
        "sunset", "coucher de soleil", "lever de soleil", "à l'aube", "paysage",
        "landscape", "portrait de", "un paysage de", "dans l'espace", "aéroport"
    };

    // Mots déclenchant la génération vidéo.
    private static readonly string[] VideoGenIntentWords =
    {
        "génère une vidéo", "genere une vidéo", "génère-moi une vidéo", "genere-moi une vidéo",
        "crée une vidéo", "cree une vidéo", "crée-moi une vidéo", "cree-moi une vidéo",
        "create a video", "generate a video", "une vidéo de", "a video of",
        "vidéo de", "video of", "film", "animation", "clip"
    };

    private IReadOnlyList<AIToolDefinition> BuildToolDefinitions(string userMessage)
    {
        // web_search est interdit : l'agent doit naviguer comme un humain via browser.
        var all = ToolDefinitionBuilder.Build(_toolRegistry)
            .Where(t => !string.Equals(t.Name, "web_search", StringComparison.Ordinal))
            .ToList();
        if (!_options.ToolPruningEnabled || _toolSelection is null) return all;

        try
        {
            var selected = _toolSelection.SelectWithScores(_toolRegistry.GetAll().Where(t => t.IsAvailable).ToList(), userMessage, null);
            var names = new HashSet<string>(selected.Tools.Select(t => t.Name), StringComparer.Ordinal);

            var lower = userMessage.ToLowerInvariant();
            var hasExecutionIntent = lower.Contains("exécute") || lower.Contains("execute") || lower.Contains("run")
                || lower.Contains("ouvre") || lower.Contains("ouvrir") || lower.Contains("lance")
                || lower.Contains("créé") || lower.Contains("cree") || lower.Contains("create")
                || lower.Contains("installe") || lower.Contains("supprime") || lower.Contains("fichier")
                || lower.Contains("dossier") || lower.Contains("terminal") || lower.Contains("commande");

            // Screen-interaction intent: user wants to click/type/interact with an app
            var hasScreenIntent = ScreenIntentWords.Any(w => lower.Contains(w, StringComparison.OrdinalIgnoreCase));

            if (!hasExecutionIntent)
            {
                // Requête d'information : on garde un socle d'outils généraux pour ne
                // pas priver le modèle des outils évidents (météo, news, calcul...).
                foreach (var t in all)
                {
                    if (GeneralAlwaysTools.Contains(t.Name)) names.Add(t.Name);
                }
            }

            // Les outils lourds / à risque ne sont proposés que s'ils sont réellement pertinents.
            foreach (var heavy in AlwaysPrunedOut)
            {
                if (!names.Contains(heavy))
                {
                    var def = all.FirstOrDefault(t => t.Name == heavy);
                    var relevant = def is not null &&
                                   (lower.Contains(def.Name) || lower.Contains(heavy.Replace('_', ' ')));
                    if (relevant) names.Add(heavy);
                }
            }

            // Always include computer_use when screen-interaction intent is detected
            if (hasScreenIntent && !names.Contains("computer_use"))
            {
                names.Add("computer_use");
                names.Add("computer");
                names.Add("computer_action");
                _logger.LogDebug("[AIServiceAdapter] Screen intent detected — including computer_use + computer_action tools");
            }

            // Si l'utilisateur décrit une scène / veut une image, on expose le générateur d'images
            // même si la requête ne contient pas littéralement le mot « image » (pruning par mots-clés trop strict).
            if (all.Any(t => t.Name == "image_generator")
                && (ImageGenIntentWords.Any(lower.Contains)
                    || (lower.Contains("image") || lower.Contains("dessine") || lower.Contains("peint")
                        || lower.Contains("illustre") || lower.Contains("photo")
                        || VisualSceneWords.Any(lower.Contains))))
            {
                names.Add("image_generator");
            }

            // Si l'utilisateur veut une vidéo, on expose le générateur vidéo.
            if (all.Any(t => t.Name == "video_generator")
                && (VideoGenIntentWords.Any(lower.Contains)
                    || lower.Contains("vidéo") || lower.Contains("video")))
            {
                names.Add("video_generator");
            }

            var pruned = all.Where(t => names.Contains(t.Name)).ToList();

            // When computer_action is available, remove computer_use to avoid confusion
            if (pruned.Any(t => t.Name == "computer_action"))
            {
                pruned.RemoveAll(t => t.Name == "computer_use");
                _logger.LogDebug("[AGENT] Removed computer_use — computer_action is the primary screen tool");
            }

            _logger.LogInformation("[AGENT] Tool pruning: {Count}/{Total} tools exposés", pruned.Count, all.Count);
            return pruned;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AGENT] Tool pruning failed, using all tools");
            return all;
        }
    }
}
