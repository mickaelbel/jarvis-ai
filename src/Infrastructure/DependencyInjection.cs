using JarvisAI.Application.Agents;
using JarvisAI.Application.Abstractions;
using JarvisAI.Application.AI;
using JarvisAI.Application.Budget;
using JarvisAI.Application.ComputerUse;
using JarvisAI.Application.Personality;
using JarvisAI.Application.Planning;
using JarvisAI.Application.Plugins;
using JarvisAI.Application.Presence;
using JarvisAI.Application.Reasoning;
using JarvisAI.Application.Security;
using JarvisAI.Application.Tools;
using JarvisAI.Application.WebAutomation;
using JarvisAI.Hue;
using JarvisAI.Infrastructure.AI;
using JarvisAI.Infrastructure.Budget;
using JarvisAI.Infrastructure.ComputerUse;
using JarvisAI.Infrastructure.Dev;
using JarvisAI.Infrastructure.Events;
using JarvisAI.Infrastructure.Memory;
using JarvisAI.Infrastructure.Obs;
using JarvisAI.Infrastructure.Planning;
using JarvisAI.Infrastructure.Plugins;
using JarvisAI.Infrastructure.Presence;
using JarvisAI.Infrastructure.Routines;
using JarvisAI.Infrastructure.Security;
using JarvisAI.Infrastructure.Tools;
using JarvisAI.Infrastructure.Vision;
using JarvisAI.Infrastructure.WebAutomation;
using JarvisAI.Application.Vision;
using JarvisAI.Application.Reminders;
using JarvisAI.Infrastructure.Reminders;

using JarvisAI.Application.Memory;
using JarvisAI.Application.Search;
using JarvisAI.Application.AutoImprovement;
using JarvisAI.Infrastructure.AutoImprovement;
using JarvisAI.Infrastructure.Search;
using JarvisAI.Infrastructure.Voice;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Enregistre uniquement le socle d'auto-développement (tests, outils externes).</summary>
    public static IServiceCollection AddInfrastructureDevOnly(this IServiceCollection services)
    {
        services.AddSingleton(sp => new Lazy<JarvisAI.Application.AI.AIService>(() =>
            throw new InvalidOperationException("AIService indisponible dans le conteneur autodev minimal.")));
        services.AddSingleton<JarvisAI.Infrastructure.Dev.SelfDevStore>();
        services.AddSingleton<JarvisAI.Infrastructure.Dev.SelfDevEngine>();
        services.AddSingleton<ITool, JarvisAI.Infrastructure.Tools.SelfDevTool>();
        services.AddSingleton<IToolRegistry>(sp =>
        {
            var registry = new ToolRegistry(sp.GetRequiredService<ILogger<ToolRegistry>>());
            foreach (var tool in sp.GetServices<ITool>())
                registry.Register(tool);
            return registry;
        });
        return services;
    }

    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IEventBus, InMemoryEventBus>();
        services.AddSingleton<ITool, SystemInfoTool>();
        services.AddSingleton<ITool, DateTimeTool>();
        services.AddSingleton<ITool, CalculatorTool>();
        services.AddSingleton<ITool, MemoryTool>();
        services.AddSingleton<ITool, FileSystemTool>();
        services.AddSingleton<ITool, ReadDocumentTool>();
        services.AddSingleton<ITool, EqualizerCurveTool>();
        services.AddSingleton<ITool, TerminalTool>();
        services.AddSingleton<ITool, ProcessTool>();
        services.AddSingleton<BrowserManager>();
        services.AddSingleton<ITool, BrowserTool>();
        services.AddSingleton<ITool, WebPageTool>();
        services.AddSingleton<ITool, ClipboardTool>();
        services.AddSingleton<ITool, WindowsTool>();
        services.AddSingleton<ITool, ComputerTool>();
        services.AddSingleton<ITool, VisionTool>();
        services.AddSingleton<ITool, UiElementTool>();
        services.AddSingleton<ITool, ComputerUseTool>();
        services.AddSingleton<ITool, SetVoiceTool>();
        services.AddSingleton<ITool, PowerTool>();
        services.AddSingleton<ITool, PermissionsTool>();
        services.AddSingleton<ITool, PresenceTool>();
        services.AddSingleton<ITool, BudgetTool>();
        services.AddSingleton<ITool, MediaControlTool>();
        services.AddSingleton<ITool, HueTool>();
        services.AddSingleton<ITool, ObsTool>();
        services.AddSingleton<ITool, WolTool>();
        services.AddSingleton<ITool, PersonalityTool>();
        services.AddSingleton<ITool, ContentHubTool>();

        // Intégrations externes (portage complet du repo Python) — identifiants
        // vivent dans %LOCALAPPDATA%\JarvisAI\integrations.json (éditables dans Paramètres)
        services.AddSingleton<JarvisAI.Infrastructure.Integrations.IntegrationsStore>();
        services.AddSingleton<JarvisAI.Infrastructure.Integrations.GoogleAuthHelper>();
        services.AddSingleton<ITool, GoogleCalendarTool>();
        services.AddSingleton<ITool, GmailTool>();
        services.AddSingleton<ITool, DiscordTool>();
        services.AddSingleton<ITool, InstagramTool>();
        services.AddSingleton<ITool, TwilioTool>();
        services.AddSingleton<ITool, AlexaTool>();
        services.AddSingleton<ITool, NestTool>();
        services.AddSingleton<ITool, HermesDelegationTool>();
        services.AddSingleton<ITool, SuiviContenuTool>();
        services.AddSingleton<ITool, LoopstrTool>();
        services.AddSingleton<ITool, BriefTool>();

        // Gestes / musique / hub de contenu (sous-process Python isolés)
        services.AddSingleton<ITool, GestureTool>();
        services.AddSingleton<ITool, MusicTool>();
        services.AddSingleton<ITool, HubInspirationTool>();

        // Catalogue VRAM (panneau : quels modèles rentrent dans la carte ?)
        services.AddSingleton<JarvisAI.Infrastructure.Models.VramCatalogService>();

        // Domotique / streaming / personnalité (portage du repo Python)
        services.AddSingleton<HueOptions>();
        services.AddSingleton<HueBridgeClient>();
        services.AddSingleton<ObsOptions>();
        services.AddSingleton<ObsWebSocketClient>();
        services.AddSingleton<WolOptions>();
        services.AddSingleton<PersonalityStore>();
        services.AddSingleton<IPermissionStore, JsonPermissionStore>();
        services.AddSingleton<BudgetOptions>();
        services.AddSingleton<IBudgetTracker, JsonBudgetTracker>();
        services.AddSingleton<PresenceOptions>();
        services.AddSingleton<PingPresenceMonitor>();
        services.AddHostedService(sp => sp.GetRequiredService<PingPresenceMonitor>());
        // Routines (déclencheurs présence + horaire)
        services.AddSingleton<RoutinesStore>();
        services.AddSingleton<RoutineEngine>();
        services.AddHostedService(sp => sp.GetRequiredService<RoutineEngine>());
        services.AddSingleton<ITool, RoutinesTool>();

        // Auto-développement : build/tests/checkpoints/publication/auto-fix (docs/selfdev.md)
        services.AddSingleton<SelfDevStore>();
        services.AddSingleton<SelfDevEngine>();
        services.AddHostedService<SelfDevLoop>();
        services.AddSingleton<ITool, SelfDevTool>();

        // Retour arrière conversationnel : chaque tour (chat/voix) est committé,
        // l'utilisateur peut annuler les derniers changements de code par la voix
        // (« Jarvis, annule tes derniers changements ») ou depuis le chat.
        services.AddSingleton<JarvisAI.Infrastructure.Dev.IGitTurnOps>(sp =>
            sp.GetRequiredService<JarvisAI.Infrastructure.Dev.SelfDevEngine>());
        services.AddSingleton<JarvisAI.Infrastructure.Dev.ITurnHistory, JarvisAI.Infrastructure.Dev.TurnHistoryService>();
        services.AddHostedService<JarvisAI.Infrastructure.Dev.VoiceTurnRecorder>();
        services.AddSingleton<ITool, RollbackTool>();

        // Objectifs long terme : avancés périodiquement par le GoalRunner,
        // une action à la fois, via les outils existants (sécurité conservée).
        services.AddSingleton<JarvisAI.Infrastructure.Goals.ObjectifsStore>();
        services.AddSingleton<JarvisAI.Infrastructure.Goals.GoalRunner>();
        services.AddHostedService(sp => sp.GetRequiredService<JarvisAI.Infrastructure.Goals.GoalRunner>());
        services.AddSingleton<ITool, JarvisAI.Infrastructure.Tools.ObjectifsTool>();
        services.AddSingleton<ITool, JarvisAI.Infrastructure.Tools.VoixPacksTool>();
        services.AddSingleton<ITool, JarvisAI.Infrastructure.Tools.RetiensTool>();
        services.AddSingleton<ITool, JarvisAI.Infrastructure.Tools.CaptureEcranTool>();
        services.AddSingleton<ITool, JarvisAI.Infrastructure.Tools.Vision.DecritEcranTool>();
        services.AddSingleton<ITool, HomeAssistantTool>();
        services.AddSingleton<IComputerController, WindowsComputerController>();
        services.AddSingleton<IUiElementDetector, OcrUiElementDetector>();
        services.AddSingleton<IComputerUseService, ComputerUseService>();
        services.AddSingleton<IWebBrowser, PlaywrightWebBrowser>();
        services.AddSingleton<IObservationProvider, ScreenAndPageObservationProvider>();
        services.AddSingleton(new AutonomousLoopOptions());
        services.AddSingleton<IAutonomousAgentLoop, AutonomousAgentLoop>();

        services.AddSingleton<IToolRegistry>(sp =>
        {
            var registry = new ToolRegistry(sp.GetRequiredService<ILogger<ToolRegistry>>());
            foreach (var tool in sp.GetServices<ITool>())
                registry.Register(tool);
            return registry;
        });

        // Lazy différé : la Value n'est accédée qu'après coup (jamais pendant la
        // construction des outils), ce qui évite toute dépendance circulaire
        // ToolRegistry -> ITool -> PermissionsTool/SecurityManager -> ToolRegistry.
        services.AddSingleton(sp => new Lazy<IToolRegistry>(sp.GetRequiredService<IToolRegistry>));
        services.AddSingleton(sp => new Lazy<JarvisAI.Application.Voice.VoiceConversationService>(sp.GetRequiredService<JarvisAI.Application.Voice.VoiceConversationService>));
        // AIService dépend d'IToolRegistry : les outils ne doivent le référencer
        // qu'au travers de ce Lazy, jamais en direct (sinon cycle au démarrage).
        services.AddSingleton(sp => new Lazy<JarvisAI.Application.AI.AIService>(sp.GetRequiredService<JarvisAI.Application.AI.AIService>));

        services.AddSingleton<SecurityOptions>();
        services.AddSingleton<ISecurityManager>(sp =>
        {
            var toolRegistry = new Lazy<IToolRegistry>(sp.GetRequiredService<IToolRegistry>);
            return new SecurityManager(
                toolRegistry,
                sp.GetRequiredService<IUserConfirmationService>(),
                sp.GetRequiredService<IEventBus>(),
                sp.GetRequiredService<ILogger<SecurityManager>>(),
                sp.GetRequiredService<SecurityOptions>(),
                sp.GetRequiredService<IPermissionStore>());
        });
        services.AddSingleton<IUserConfirmationService, ConsoleConfirmationService>();
        services.AddSingleton<SecurityPolicyStore>();
        services.AddSingleton(new ToolTimeoutOptions());
        services.AddSingleton<IToolExecutor, ToolExecutor>();

        // Auto-amélioration : outils auto-créés (recettes + C# sandbox), leçons, safe-mode.
        services.AddSingleton<IAutoToolStore, AutoToolStore>();
        services.AddSingleton<IAutoToolCompiler, CSharpToolCompiler>();
        services.AddSingleton<IToolHostFactory, ToolHostFactory>();
        services.AddSingleton<ISelfImprovementManager>(sp => new SelfImprovementManager(
            sp.GetRequiredService<IAutoToolStore>(),
            new Lazy<IToolRegistry>(sp.GetRequiredService<IToolRegistry>),
            new Lazy<IToolExecutor>(sp.GetRequiredService<IToolExecutor>),
            sp.GetRequiredService<IAutoToolCompiler>(),
            sp.GetRequiredService<IToolHostFactory>(),
            sp.GetRequiredService<ILogger<SelfImprovementManager>>()));
        services.AddSingleton<ITool>(sp => new CreateToolTool(sp.GetRequiredService<ISelfImprovementManager>()));
        services.AddSingleton<ITool>(sp => new AddLessonTool(sp.GetRequiredService<ISelfImprovementManager>()));

        // Rappels : service + outil IA (annonce vocale proactive à l'échéance).
        services.AddSingleton<IReminderService>(sp =>
            new ReminderService(sp.GetRequiredService<ILogger<ReminderService>>()));
        services.AddSingleton<ITool>(sp => new RemindersTool(sp.GetRequiredService<IReminderService>()));
        services.AddSingleton<ITool>(sp => new TimerTool(sp.GetRequiredService<IReminderService>()));

        services.AddSingleton<IMemoryStore>(sp =>
            new LiteDBMemoryStore(
                sp.GetRequiredService<ILogger<LiteDBMemoryStore>>(),
                GetPersistentMemoryDatabasePath()));
        services.AddSingleton<IEmbeddingService>(sp =>
            new OllamaEmbeddingService(
                new HttpClient { BaseAddress = new Uri("http://localhost:11434"), Timeout = TimeSpan.FromMinutes(30) },
                sp.GetRequiredService<ILogger<OllamaEmbeddingService>>()));
        services.AddSingleton<IMemoryService>(sp =>
            new MemoryService(
                sp.GetRequiredService<IMemoryStore>(),
                sp.GetRequiredService<IEventBus>(),
                sp.GetRequiredService<ILogger<MemoryService>>(),
                sp.GetRequiredService<IEmbeddingService>()));
        services.AddSingleton<Application.Memory.IEpisodicMemoryService>(sp =>
            new Application.Memory.EpisodicMemoryService(
                sp.GetRequiredService<IMemoryService>(),
                sp.GetService<Microsoft.Extensions.Logging.ILogger<Application.Memory.EpisodicMemoryService>>(),
                // Contexte visuel : application au premier plan + texte OCR à l'écran.
                contextProbe: () =>
                {
                    var app = Windows.ForegroundAppProbe.GetName();
                    string? ocr = null;
                    try { ocr = Windows.ScreenContextProbe.GetRecentOcr(280); } catch { }
                    if (string.IsNullOrWhiteSpace(app) && string.IsNullOrWhiteSpace(ocr)) return null;
                    if (string.IsNullOrWhiteSpace(ocr)) return app;
                    return $"{app ?? "bureau"} | à l'écran : {ocr}";
                }));

        services.AddSingleton<OllamaRunMonitor>();
        services.AddSingleton<OllamaLauncher>();
        services.AddSingleton<OllamaProvider>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<OllamaProvider>>();
            var httpClient = new HttpClient
            {
                BaseAddress = new Uri("http://localhost:11434"),
                // Les générations (surtout chargement à froid d'un modèle long à
                // charger) peuvent largement dépasser le timeout par défaut de 100 s.
                Timeout = TimeSpan.FromMinutes(30)
            };
            return new OllamaProvider(httpClient, logger, monitor: sp.GetRequiredService<OllamaRunMonitor>(), launcher: sp.GetRequiredService<OllamaLauncher>());
        });

        // Fournisseurs OpenAI-compatibles (OpenAI, Groq, Gemini, OpenRouter, HF, local).
        // La clé API / l'endpoint sont lus dynamiquement dans ai-providers.json,
        // donc les modifications faites dans les paramètres sont prises en compte
        // immédiatement, sans redémarrage.
        services.AddSingleton<AiProviderSettingsStore>();
        foreach (var catalogEntry in AiProviderCatalog.All)
        {
            var entry = catalogEntry;
            services.AddSingleton<IAIProvider>(sp => new OpenAiCompatibleProvider(
                entry.Key,
                entry.DisplayName,
                sp.GetRequiredService<AiProviderSettingsStore>(),
                entry,
                sp.GetRequiredService<ILogger<OpenAiCompatibleProvider>>()));
        }
        services.AddSingleton<IAIProvider>(sp => sp.GetRequiredService<OllamaProvider>());
        services.AddSingleton<RoutingProvider>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<RoutingProvider>>();
            var providers = new List<IAIProvider> { sp.GetRequiredService<OllamaProvider>() };
            foreach (var entry in AiProviderCatalog.All)
            {
                providers.Add(new OpenAiCompatibleProvider(
                    entry.Key,
                    entry.DisplayName,
                    sp.GetRequiredService<AiProviderSettingsStore>(),
                    entry,
                    sp.GetRequiredService<ILogger<OpenAiCompatibleProvider>>()));
            }
            return new RoutingProvider(providers, logger);
        });
        services.AddSingleton<IAIProvider>(sp => sp.GetRequiredService<RoutingProvider>());
        services.AddSingleton<AIService>();

        services.AddSingleton<PluginLoader>();
        services.AddSingleton<PluginRegistry>();
        services.AddSingleton<PluginPermissionManager>();
        services.AddSingleton<IPluginManager>(sp =>
            new PluginManager(
                sp.GetRequiredService<PluginLoader>(),
                sp.GetRequiredService<PluginRegistry>(),
                sp.GetRequiredService<IEventBus>(),
                sp.GetRequiredService<IToolRegistry>(),
                sp,
                sp.GetRequiredService<ILogger<PluginManager>>(),
                Path.Combine(AppContext.BaseDirectory, "Plugins"),
                sp.GetRequiredService<PluginPermissionManager>()));

        services.AddSingleton<IPlanner, Planner>();
        services.AddSingleton<IReasoningEngine, ReasoningEngine>();
        services.AddSingleton<IPlanRepository, PlanRepository>();
        services.AddSingleton<PlanExecutor>();

        services.AddSingleton<IOcrService, WindowsTesseractOcrService>();
        services.AddSingleton<IVisionService>(sp =>
            new OllamaVisionService(
                new HttpClient { BaseAddress = new Uri("http://localhost:11434"), Timeout = TimeSpan.FromMinutes(30) },
                sp.GetRequiredService<ILogger<OllamaVisionService>>()));

        services.AddWebSearch();

        return services;
    }

    public static IServiceCollection AddWebSearch(this IServiceCollection services)
    {
        services.AddSingleton<SearchCacheOptions>();
        services.AddSingleton<ISearchCache, SearchCache>();
        services.AddSingleton<OfficialSiteDetector>();
        services.AddSingleton<FakeSiteDetector>();
        services.AddSingleton<ILinkVerifier>(sp =>
            new LinkVerifier(SearchHttp.CreateClient(TimeSpan.FromSeconds(10))));
        services.AddSingleton<IWebPageContentService>(sp =>
            new WebPageContentService(SearchHttp.CreateClient(TimeSpan.FromSeconds(15)), sp.GetRequiredService<ILogger<WebPageContentService>>()));

        services.AddSingleton(sp =>
        {
            var http = SearchHttp.CreateClient();
            return new DuckDuckGoSearchProvider(http, sp.GetRequiredService<ILogger<DuckDuckGoSearchProvider>>());
        });
        services.AddSingleton(sp =>
        {
            var http = SearchHttp.CreateClient();
            return new BingSearchProvider(http, sp.GetRequiredService<ILogger<BingSearchProvider>>());
        });
        services.AddSingleton(sp =>
        {
            var http = SearchHttp.CreateClient();
            return new WikipediaSearchProvider(http, sp.GetRequiredService<ILogger<WikipediaSearchProvider>>());
        });
        services.AddSingleton(sp =>
        {
            var http = SearchHttp.CreateClient();
            http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
            return new GitHubSearchProvider(http, sp.GetRequiredService<ILogger<GitHubSearchProvider>>());
        });
        services.AddSingleton(sp =>
        {
            var http = SearchHttp.CreateClient();
            return new RedditSearchProvider(http, sp.GetRequiredService<ILogger<RedditSearchProvider>>());
        });
        services.AddSingleton(sp =>
        {
            var http = SearchHttp.CreateClient();
            return new NewsRssProvider(http, sp.GetRequiredService<ILogger<NewsRssProvider>>());
        });
        services.AddSingleton(sp =>
        {
            var http = SearchHttp.CreateClient();
            return new ArxivSearchProvider(http, sp.GetRequiredService<ILogger<ArxivSearchProvider>>());
        });
        services.AddSingleton(sp =>
        {
            var http = SearchHttp.CreateClient();
            return new SemanticScholarSearchProvider(http, sp.GetRequiredService<ILogger<SemanticScholarSearchProvider>>());
        });
        services.AddSingleton(sp =>
        {
            var http = SearchHttp.CreateClient();
            return new HalSearchProvider(http, sp.GetRequiredService<ILogger<HalSearchProvider>>());
        });
        services.AddSingleton(sp =>
        {
            var http = SearchHttp.CreateClient();
            return new PubMedSearchProvider(http, sp.GetRequiredService<ILogger<PubMedSearchProvider>>());
        });
        services.AddSingleton(sp =>
        {
            var http = SearchHttp.CreateClient();
            return new CrossRefSearchProvider(http, sp.GetRequiredService<ILogger<CrossRefSearchProvider>>());
        });
        services.AddSingleton(sp =>
        {
            var http = SearchHttp.CreateClient();
            return new StackOverflowSearchProvider(http, sp.GetRequiredService<ILogger<StackOverflowSearchProvider>>());
        });
        services.AddSingleton(sp =>
        {
            var http = SearchHttp.CreateClient();
            return new NominatimLocalProvider(http, sp.GetRequiredService<ILogger<NominatimLocalProvider>>());
        });

        services.AddSingleton<IWebSearchProvider>(sp => sp.GetRequiredService<DuckDuckGoSearchProvider>());
        services.AddSingleton<IWebSearchProvider>(sp => sp.GetRequiredService<BingSearchProvider>());
        services.AddSingleton<IWebSearchProvider>(sp => sp.GetRequiredService<WikipediaSearchProvider>());
        services.AddSingleton<IWebSearchProvider>(sp => sp.GetRequiredService<GitHubSearchProvider>());
        services.AddSingleton<IWebSearchProvider>(sp => sp.GetRequiredService<RedditSearchProvider>());
        services.AddSingleton<IWebSearchProvider>(sp => sp.GetRequiredService<NewsRssProvider>());
        services.AddSingleton<IWebSearchProvider>(sp => sp.GetRequiredService<ArxivSearchProvider>());
        services.AddSingleton<IWebSearchProvider>(sp => sp.GetRequiredService<SemanticScholarSearchProvider>());
        services.AddSingleton<IWebSearchProvider>(sp => sp.GetRequiredService<HalSearchProvider>());
        services.AddSingleton<IWebSearchProvider>(sp => sp.GetRequiredService<PubMedSearchProvider>());
        services.AddSingleton<IWebSearchProvider>(sp => sp.GetRequiredService<CrossRefSearchProvider>());
        services.AddSingleton<IWebSearchProvider>(sp => sp.GetRequiredService<StackOverflowSearchProvider>());
        services.AddSingleton<IWebSearchProvider>(sp => sp.GetRequiredService<NominatimLocalProvider>());

        services.AddSingleton<IWebSearchService>(sp =>
            new WebSearchService(
                sp.GetServices<IWebSearchProvider>(),
                sp.GetRequiredService<ILinkVerifier>(),
                sp.GetRequiredService<ISearchCache>(),
                sp.GetRequiredService<OfficialSiteDetector>(),
                sp.GetRequiredService<FakeSiteDetector>(),
                sp.GetRequiredService<ILogger<WebSearchService>>(),
                sp.GetRequiredService<SearchCacheOptions>()));

        services.AddSingleton<ITool>(sp => new WebSearchTool(
            sp.GetRequiredService<IWebSearchService>(),
            sp.GetRequiredService<ILogger<WebSearchTool>>(),
            sp.GetService<BrowserManager>() ?? new BrowserManager(sp.GetRequiredService<ILogger<BrowserManager>>()),
            sp.GetService<IWebPageContentService>()));

        services.AddSingleton<ITool>(sp =>
            new NewsTool(sp.GetRequiredService<IWebSearchService>(), sp.GetRequiredService<ILogger<NewsTool>>()));
        services.AddSingleton<ITool>(sp =>
            new WeatherTool(SearchHttp.CreateClient()));

        return services;
    }

    private static string GetPersistentMemoryDatabasePath()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var jarvisDir = Path.Combine(baseDir, "JarvisAI");
        Directory.CreateDirectory(jarvisDir);
        return Path.Combine(jarvisDir, "memory.db");
    }
}
