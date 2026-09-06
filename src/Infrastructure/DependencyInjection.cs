using JarvisAI.Application.Agents;
using JarvisAI.Application.Abstractions;
using JarvisAI.Application.AI;
using System.Diagnostics;
using JarvisAI.Application.Budget;
using JarvisAI.Application.ComputerUse;
using JarvisAI.Application.Personality;
using JarvisAI.Application.Planning;
using JarvisAI.Application.Presence;
using JarvisAI.Application.Reasoning;
using JarvisAI.Application.Security;
using JarvisAI.Application.Tools;
using JarvisAI.Application.WebAutomation;
using JarvisAI.Infrastructure.AI;
using JarvisAI.Infrastructure.Budget;
using JarvisAI.Infrastructure.Integrations;
using JarvisAI.Infrastructure.ComputerUse;
using JarvisAI.Infrastructure.Dev;
using JarvisAI.Infrastructure.Events;
using JarvisAI.Infrastructure.Memory;
using JarvisAI.Infrastructure.Planning;
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
using JarvisAI.Infrastructure.Audio;
using JarvisAI.Infrastructure.Windows;
using JarvisAI.Infrastructure.Automation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure;

public static class DependencyInjection
{
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
            var tools = sp.GetServices<ITool>();
            foreach (var tool in tools)
                registry.Register(tool);
            return registry;
        });
        return services;
    }

    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IEventBus, InMemoryEventBus>();
        services.AddSingleton<IntegrationsStore>();
        services.AddSingleton<ITool, SystemInfoTool>();
        services.AddSingleton<ITool, DateTimeTool>();
        services.AddSingleton<ITool>(sp => new ModelChangeTool(
            sp.GetRequiredService<Application.AI.ModelOverrideStore>(),
            sp.GetRequiredService<Application.AI.ModelRouterOptions>()));
        services.AddSingleton<ITool, CalculatorTool>();
        services.AddSingleton<ITool, MemoryTool>();
        services.AddSingleton<ITool, FileSystemTool>();
        services.AddSingleton<ITool, ReadDocumentTool>();
        services.AddSingleton<ITool, TerminalTool>();
        services.AddSingleton<ITool, ProcessTool>();
        services.AddSingleton<ITool, RegistryTool>();
        services.AddSingleton<ITool, ServicesTool>();
        services.AddSingleton<ITool, SchedulerTool>();
        services.AddSingleton<BrowserManager>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<BrowserManager>>();
            return new BrowserManager(logger, url =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                }
                catch { }
            });
        });
        services.AddSingleton<ITool, BrowserTool>();
        services.AddSingleton<ITool, WebPageTool>();
        services.AddSingleton<ITool, DictationTool>();
        services.AddSingleton<ITool>(sp => new ReminderTool(sp.GetRequiredService<Application.Services.IReminderService>()));
        services.AddSingleton<ITool, ClipboardTool>();
        services.AddSingleton<ITool, WindowsTool>();
        services.AddSingleton<ITool, VisionTool>();
        services.AddSingleton<ITool, ComputerUseTool>();
        services.AddSingleton<ITool, ComputerActionTool>();
        services.AddSingleton<ITool, PaintTool>();
        services.AddSingleton<ITool, ImageGenTool>();
        services.AddSingleton<ITool, SetVoiceTool>();
        services.AddSingleton<ITool, PowerTool>();
        services.AddSingleton<ITool, PermissionsTool>();
        services.AddSingleton<ITool, BudgetTool>();
        services.AddSingleton<ITool, PersonalityTool>();
        services.AddSingleton<ITool, ContentHubTool>();

        services.AddSingleton<PersonalityStore>();
        services.AddSingleton<JarvisAI.Infrastructure.Security.AuditLogService>();
        services.AddSingleton<JarvisAI.Application.Security.IAuditLogService>(sp =>
            sp.GetRequiredService<JarvisAI.Infrastructure.Security.AuditLogService>());

        services.AddSingleton<JarvisAI.Infrastructure.Security.ErrorLearningService>();
        services.AddSingleton<JarvisAI.Application.Security.IErrorLearningService>(sp =>
            sp.GetRequiredService<JarvisAI.Infrastructure.Security.ErrorLearningService>());

        services.AddSingleton<JarvisAI.Infrastructure.Security.SecuritySandbox>();
        services.AddSingleton<JarvisAI.Application.Security.ISecuritySandbox>(sp =>
            sp.GetRequiredService<JarvisAI.Infrastructure.Security.SecuritySandbox>());

        services.AddSingleton<JarvisAI.Application.Analytics.IDashboardService,
            JarvisAI.Infrastructure.Analytics.DashboardService>();

        services.AddSingleton<JarvisAI.Application.Configuration.IConfigExportService,
            JarvisAI.Infrastructure.Configuration.ConfigExportService>();
        services.AddSingleton<BudgetOptions>();
        services.AddSingleton<IBudgetTracker, JsonBudgetTracker>();
        services.AddSingleton<RoutinesStore>();
        services.AddSingleton<RoutineEngine>();
        services.AddHostedService(sp => sp.GetRequiredService<RoutineEngine>());
        services.AddSingleton<ITool, RoutinesTool>();

        services.AddSingleton<SelfDevStore>();
        services.AddSingleton<SelfDevEngine>();
        services.AddHostedService<SelfDevLoop>();
        services.AddSingleton<ITool, SelfDevTool>();

        services.AddSingleton<JarvisAI.Infrastructure.Dev.IGitTurnOps>(sp =>
            sp.GetRequiredService<JarvisAI.Infrastructure.Dev.SelfDevEngine>());
        services.AddSingleton<JarvisAI.Infrastructure.Dev.ITurnHistory, JarvisAI.Infrastructure.Dev.TurnHistoryService>();
        services.AddHostedService<JarvisAI.Infrastructure.Dev.VoiceTurnRecorder>();
        services.AddSingleton<ITool, RollbackTool>();

        services.AddSingleton<JarvisAI.Infrastructure.Goals.ObjectifsStore>();
        services.AddSingleton<JarvisAI.Infrastructure.Goals.GoalRunner>();
        services.AddHostedService(sp => sp.GetRequiredService<JarvisAI.Infrastructure.Goals.GoalRunner>());
        services.AddSingleton<ITool, JarvisAI.Infrastructure.Tools.ObjectifsTool>();
        services.AddSingleton<ITool, JarvisAI.Infrastructure.Tools.VoixPacksTool>();
        services.AddSingleton<ITool, JarvisAI.Infrastructure.Tools.RetiensTool>();
        services.AddSingleton<ITool, JarvisAI.Infrastructure.Tools.CaptureEcranTool>();
        services.AddSingleton<ITool, JarvisAI.Infrastructure.Tools.IndexerFichiersTool>();
        services.AddSingleton<ITool>(sp => new JarvisAI.Infrastructure.Tools.GestionModelesTool(
            new Lazy<Application.Voice.IVoiceConfirmationChannel>(sp.GetRequiredService<Application.Voice.IVoiceConfirmationChannel>),
            sp.GetRequiredService<Application.Voice.IVoiceSettingsStore>()));
        services.AddSingleton<ITool, JarvisAI.Infrastructure.Tools.Vision.DecritEcranTool>();
        services.AddSingleton<ITool>(sp => new JarvisAI.Infrastructure.Tools.CloneVoixTool(
            sp.GetRequiredService<JarvisAI.Infrastructure.Voice.XttsTextToSpeechService>(),
            sp.GetRequiredService<Application.Voice.IVoiceSettingsStore>()));
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
            registry.SetToolResolver(() => sp.GetServices<ITool>());
            return registry;
        });

        services.AddSingleton(sp => new Lazy<IToolRegistry>(sp.GetRequiredService<IToolRegistry>));
        services.AddSingleton(sp => new Lazy<JarvisAI.Application.Voice.VoiceConversationService>(sp.GetRequiredService<JarvisAI.Application.Voice.VoiceConversationService>));
        services.AddSingleton(sp => new Lazy<JarvisAI.Application.AI.AIService>(sp.GetRequiredService<JarvisAI.Application.AI.AIService>));

        services.AddSingleton<SecurityOptions>();
        services.AddSingleton<IPermissionStore, JsonPermissionStore>();
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

        services.AddSingleton<IReminderService>(sp =>
            new ReminderService(sp.GetRequiredService<ILogger<ReminderService>>()));
        services.AddSingleton<ITool>(sp => new RemindersTool(sp.GetRequiredService<IReminderService>()));
        services.AddSingleton<ITool>(sp => new TimerTool(sp.GetRequiredService<IReminderService>()));

        services.AddSingleton<IMemoryStore>(sp =>
            new LiteDBMemoryStore(
                sp.GetRequiredService<ILogger<LiteDBMemoryStore>>(),
                GetPersistentMemoryDatabasePath()));
        services.AddSingleton<IMemorySettingsStore>(sp =>
            new MemorySettingsStore(
                sp.GetRequiredService<ILogger<MemorySettingsStore>>()));
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
                contextProbe: () =>
                {
                    var app = Windows.ForegroundAppProbe.GetName();
                    string? ocr = null;
                    try { ocr = Windows.ScreenContextProbe.GetRecentOcr(280); } catch { }
                    if (string.IsNullOrWhiteSpace(app) && string.IsNullOrWhiteSpace(ocr)) return null;
                    if (string.IsNullOrWhiteSpace(ocr)) return app;
                    return $"{app ?? "bureau"} | à l'écran : {ocr}";
                },
                settingsStore: sp.GetService<IMemorySettingsStore>()));

        services.AddSingleton<MemoryConsolidationService>();

        services.AddSingleton<OllamaRunMonitor>();
        services.AddSingleton<OllamaLauncher>();
        services.AddSingleton<OllamaProvider>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<OllamaProvider>>();
            var httpClient = new HttpClient
            {
                BaseAddress = new Uri("http://localhost:11434"),
                Timeout = TimeSpan.FromMinutes(30)
            };
            var opts = sp.GetRequiredService<Application.AI.ModelRouterOptions>();
            return new OllamaProvider(httpClient, logger, monitor: sp.GetRequiredService<OllamaRunMonitor>(), launcher: sp.GetRequiredService<OllamaLauncher>(), numCtx: opts.NumCtx);
        });

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

        services.AddSingleton<IPlanner, Planner>();
        services.AddSingleton<IReasoningEngine, ReasoningEngine>();
        services.AddSingleton<IPlanRepository, PlanRepository>();
        services.AddSingleton<PlanExecutor>();

        services.AddSingleton<IOcrService, WindowsTesseractOcrService>();
        services.AddSingleton<IVisionService>(sp =>
            new OllamaVisionService(
                new HttpClient { BaseAddress = new Uri("http://localhost:11434"), Timeout = TimeSpan.FromMinutes(30) },
                sp.GetRequiredService<ILogger<OllamaVisionService>>()));

        services.AddSingleton<ComfyUIProcessManager>(sp =>
            new ComfyUIProcessManager(
                sp.GetRequiredService<ILogger<ComfyUIProcessManager>>()));
        services.AddSingleton<ComfyUISetupService>(sp =>
            new ComfyUISetupService(
                sp.GetRequiredService<ILogger<ComfyUISetupService>>()));
        services.AddSingleton<JarvisAI.Application.Abstractions.IComfyUISetup>(sp =>
            sp.GetRequiredService<ComfyUISetupService>());
        services.AddSingleton<ComfyUIImageGenerationService>(sp =>
            new ComfyUIImageGenerationService(
                new HttpClient(),
                sp.GetRequiredService<ILogger<ComfyUIImageGenerationService>>()));
        services.AddSingleton<PollinationsImageGenerationService>(sp =>
            new PollinationsImageGenerationService(
                new HttpClient { Timeout = TimeSpan.FromMinutes(5) },
                sp.GetRequiredService<ILogger<PollinationsImageGenerationService>>()));
        services.AddSingleton<QwenImageService>(sp =>
            new QwenImageService(
                new HttpClient { Timeout = TimeSpan.FromMinutes(5) },
                sp.GetRequiredService<ILogger<QwenImageService>>()));
        services.AddSingleton<IImageGenerationService>(sp =>
            sp.GetRequiredService<QwenImageService>());
        services.AddSingleton<IVideoGenerationService>(sp =>
            new LocalVideoService(
                new HttpClient { Timeout = TimeSpan.FromMinutes(5) },
                sp.GetRequiredService<ILogger<LocalVideoService>>()));
        services.AddHostedService<PythonServerHostedService>();
        services.AddSingleton<ITool, VideoGenTool>();

        services.AddWebSearch();

        services.AddSingleton<IVoiceSetupService, VoiceSetupService>();
        services.AddSingleton<IVoiceWizardService, VoiceWizardService>();
        services.AddSingleton<IWindowsIntegrationService, WindowsIntegrationService>();
        services.AddSingleton<ISystemTrayService, SystemTrayService>();
        services.AddSingleton<IEnhancedSttService, EnhancedSttService>();
        services.AddSingleton<IEnhancedTtsService, EnhancedTtsService>();
        services.AddSingleton<IEnhancedWakeWordService, EnhancedWakeWordService>();
        services.AddSingleton<IVoiceConversationOrchestrator, VoiceConversationOrchestrator>();
        services.AddSingleton<IVoiceEffectsService, VoiceEffectsService>();
        services.AddSingleton<IVoiceCommandProcessor, VoiceCommandProcessor>();
        services.AddSingleton<IVoiceProfilesService, VoiceProfilesService>();
        services.AddSingleton<IVoiceContextService, VoiceContextService>();
        services.AddSingleton<IVoiceUiService, VoiceUiService>();
        services.AddSingleton<IVoiceSystemCommandsService, VoiceSystemCommandsService>();
        services.AddSingleton<IVoiceDiagnosticsService, VoiceDiagnosticsService>();
        services.AddSingleton<IVoicePerformanceService, VoicePerformanceService>();

        services.AddSingleton<IDailyWorkflowService, DailyWorkflowService>();
        services.AddSingleton<IBackupService, BackupService>();
        services.AddSingleton<ISystemCleanupService, SystemCleanupService>();
        services.AddSingleton<IFileOrganizationService, FileOrganizationService>();
        services.AddSingleton<IAutomationReminderService, AutomationReminderService>();
        services.AddSingleton<IBatchRenameService, BatchRenameService>();
        services.AddSingleton<IFileConversionService, FileConversionService>();
        services.AddSingleton<IProcessMonitorService, ProcessMonitorService>();

        services.AddSingleton<IWebScraperService, WebScraperService>();
        services.AddSingleton<IPriceComparatorService, PriceComparatorService>();
        services.AddSingleton<IVeilleTechnoService, VeilleTechnoService>();
        services.AddSingleton<IDownloadManagerService, DownloadManagerService>();
        services.AddSingleton<ITextSummarizerService, TextSummarizerService>();
        services.AddSingleton<IDocumentTranslatorService, DocumentTranslatorService>();
        services.AddSingleton<ISiteMonitorService, SiteMonitorService>();

        services.AddSingleton<IFileDeduplicationService, FileDeduplicationService>();
        services.AddSingleton<ILocalSearchService, LocalSearchService>();
        services.AddSingleton<IFolderSyncService, FolderSyncService>();
        services.AddSingleton<IDiskAnalyzerService, DiskAnalyzerService>();
        services.AddSingleton<IFileEncryptorService, FileEncryptorService>();
        services.AddSingleton<IFileWatcherService, FileWatcherService>();
        services.AddSingleton<IFolderCompareService, FolderCompareService>();
        services.AddSingleton<ISymlinkManagerService, SymlinkManagerService>();

        services.AddSingleton<IMediaAutomationService, MediaAutomationService>();

        services.AddSingleton<ISubAgentService, SubAgentService>();

        services.AddSingleton<IApiHealthCheckerService, ApiHealthCheckerService>();

        return services;
    }

    public static IServiceCollection AddWebSearch(this IServiceCollection services)
    {
        services.AddSingleton<SearchCacheOptions>();
        services.AddSingleton<ISearchCache, SearchCache>();
        services.AddSingleton<OfficialSiteDetector>();
        services.AddSingleton<FakeSiteDetector>();
        services.AddSingleton<ILinkVerifier>(sp =>
            new LinkVerifier(SearchHttp.GetSharedClient(TimeSpan.FromSeconds(10))));
        services.AddSingleton<IWebPageContentService>(sp =>
            new WebPageContentService(SearchHttp.GetSharedClient(TimeSpan.FromSeconds(15)), sp.GetRequiredService<ILogger<WebPageContentService>>()));

        services.AddSingleton(sp =>
            new DuckDuckGoSearchProvider(SearchHttp.GetSharedClient(), sp.GetRequiredService<ILogger<DuckDuckGoSearchProvider>>()));
        services.AddSingleton(sp =>
            new BingSearchProvider(SearchHttp.GetSharedClient(), sp.GetRequiredService<ILogger<BingSearchProvider>>()));
        services.AddSingleton(sp =>
            new WikipediaSearchProvider(SearchHttp.GetSharedClient(), sp.GetRequiredService<ILogger<WikipediaSearchProvider>>()));
        services.AddSingleton(sp =>
        {
            var http = SearchHttp.GetSharedClient();
            http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
            return new GitHubSearchProvider(http, sp.GetRequiredService<ILogger<GitHubSearchProvider>>());
        });
        services.AddSingleton(sp =>
            new RedditSearchProvider(SearchHttp.GetSharedClient(), sp.GetRequiredService<ILogger<RedditSearchProvider>>()));
        services.AddSingleton(sp =>
            new NewsRssProvider(SearchHttp.GetSharedClient(), sp.GetRequiredService<ILogger<NewsRssProvider>>()));
        services.AddSingleton(sp =>
            new ArxivSearchProvider(SearchHttp.GetSharedClient(), sp.GetRequiredService<ILogger<ArxivSearchProvider>>()));
        services.AddSingleton(sp =>
            new SemanticScholarSearchProvider(SearchHttp.GetSharedClient(), sp.GetRequiredService<ILogger<SemanticScholarSearchProvider>>()));
        services.AddSingleton(sp =>
            new HalSearchProvider(SearchHttp.GetSharedClient(), sp.GetRequiredService<ILogger<HalSearchProvider>>()));
        services.AddSingleton(sp =>
            new PubMedSearchProvider(SearchHttp.GetSharedClient(), sp.GetRequiredService<ILogger<PubMedSearchProvider>>()));
        services.AddSingleton(sp =>
            new CrossRefSearchProvider(SearchHttp.GetSharedClient(), sp.GetRequiredService<ILogger<CrossRefSearchProvider>>()));
        services.AddSingleton(sp =>
            new StackOverflowSearchProvider(SearchHttp.GetSharedClient(), sp.GetRequiredService<ILogger<StackOverflowSearchProvider>>()));
        services.AddSingleton(sp =>
            new NominatimLocalProvider(SearchHttp.GetSharedClient(), sp.GetRequiredService<ILogger<NominatimLocalProvider>>()));

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
            sp.GetService<BrowserManager>() ?? new BrowserManager(sp.GetRequiredService<ILogger<BrowserManager>>(), _ =>
                throw new InvalidOperationException("BrowserManager sans opener : refus d'ouvrir le navigateur en dehors du hôte applicatif.")),
            sp.GetService<IWebPageContentService>()));

        services.AddSingleton<ITool>(sp =>
            new NewsTool(sp.GetRequiredService<IWebSearchService>(), sp.GetRequiredService<ILogger<NewsTool>>()));
        services.AddSingleton<ITool>(sp =>
            new WeatherTool(SearchHttp.CreateClient()));

        services.AddSingleton<JarvisAI.Infrastructure.AI.IRoutingFeedbackService, JarvisAI.Infrastructure.AI.RoutingFeedbackService>();

        services.AddSingleton<JarvisAI.Infrastructure.Hardware.IMonitorService, JarvisAI.Infrastructure.Hardware.MonitorService>();
        services.AddSingleton<JarvisAI.Infrastructure.Hardware.IResourceThrottler, JarvisAI.Infrastructure.Hardware.ResourceThrottler>();
        services.AddSingleton<JarvisAI.Infrastructure.Hardware.INetworkMonitor, JarvisAI.Infrastructure.Hardware.NetworkMonitor>();
        services.AddSingleton<JarvisAI.Infrastructure.AI.ICommandPredictor, JarvisAI.Infrastructure.AI.CommandPredictor>();
        services.AddSingleton<JarvisAI.Infrastructure.AI.IActionChainManager, JarvisAI.Infrastructure.AI.ActionChainManager>();
        services.AddSingleton<JarvisAI.Infrastructure.Security.IUndoRedoManager, JarvisAI.Infrastructure.Security.UndoRedoManager>();
        services.AddSingleton<JarvisAI.Infrastructure.Security.IToolLockManager, JarvisAI.Infrastructure.Security.ToolLockManager>();
        services.AddSingleton<JarvisAI.Application.Tools.ITool, JarvisAI.Infrastructure.Tools.NaturalLanguageFileTool>();

        services.AddSingleton<JarvisAI.Infrastructure.Security.IGitVersioningService, JarvisAI.Infrastructure.Security.GitVersioningService>();
        services.AddSingleton<JarvisAI.Infrastructure.Security.ISecureStorage, JarvisAI.Infrastructure.Security.SecureStorage>();
        services.AddSingleton<JarvisAI.Infrastructure.Security.IPersonaManager, JarvisAI.Infrastructure.Security.PersonaManager>();
        services.AddSingleton<JarvisAI.Infrastructure.Security.IClipboardHistoryManager, JarvisAI.Infrastructure.Security.ClipboardHistoryManager>();

        services.AddSingleton<JarvisAI.Infrastructure.Configuration.IThemeManager, JarvisAI.Infrastructure.Configuration.ThemeManager>();

        services.AddSingleton<JarvisAI.Infrastructure.AI.IFederatedLearningService, JarvisAI.Infrastructure.AI.FederatedLearningService>();
        services.AddSingleton<JarvisAI.Infrastructure.AI.IIntentRecognizer, JarvisAI.Infrastructure.AI.IntentRecognizer>();
        services.AddSingleton<JarvisAI.Infrastructure.AI.IMultiModalProcessor, JarvisAI.Infrastructure.AI.MultiModalProcessor>();
        services.AddSingleton<JarvisAI.Infrastructure.Hardware.IHealthMonitor, JarvisAI.Infrastructure.Hardware.HealthMonitor>();
        services.AddSingleton<JarvisAI.Infrastructure.Security.ITelemetryService, JarvisAI.Infrastructure.Security.TelemetryService>();
        services.AddSingleton<JarvisAI.Application.Tools.ITool, JarvisAI.Infrastructure.Tools.InlineCodeSandbox>();
        services.AddSingleton<JarvisAI.Infrastructure.Hardware.IWindowManager, JarvisAI.Infrastructure.Hardware.WindowManager>();
        services.AddSingleton<JarvisAI.Application.Tools.ITool, JarvisAI.Infrastructure.Tools.SmartFileWatcherTool>();
        services.AddSingleton<JarvisAI.Application.Tools.ITool, JarvisAI.Infrastructure.Tools.StressTestTool>();
        services.AddSingleton<JarvisAI.Infrastructure.AI.ITaskDecomposer, JarvisAI.Infrastructure.AI.TaskDecomposer>();
        services.AddSingleton<JarvisAI.Infrastructure.AI.ICodeReviewService, JarvisAI.Infrastructure.AI.CodeReviewService>();
        services.AddSingleton<JarvisAI.Infrastructure.AI.ITestGeneratorService, JarvisAI.Infrastructure.AI.TestGeneratorService>();
        services.AddSingleton<JarvisAI.Application.Tools.ITool, JarvisAI.Infrastructure.Tools.ProjectScaffoldingTool>();
        services.AddSingleton<JarvisAI.Infrastructure.AI.IDependencyScanner, JarvisAI.Infrastructure.AI.DependencyScanner>();
        services.AddSingleton<JarvisAI.Infrastructure.AI.IPerformanceProfiler, JarvisAI.Infrastructure.AI.PerformanceProfiler>();
        services.AddSingleton<JarvisAI.Infrastructure.AI.IKnowledgeGraphBuilder, JarvisAI.Infrastructure.AI.KnowledgeGraphBuilder>();
        services.AddSingleton<JarvisAI.Application.Tools.ITool, JarvisAI.Infrastructure.Tools.NaturalLanguageSQLTool>();
        services.AddSingleton<JarvisAI.Infrastructure.AI.ICodeFormatter, JarvisAI.Infrastructure.AI.CodeFormatter>();
        services.AddSingleton<JarvisAI.Application.Tools.ITool, JarvisAI.Infrastructure.Tools.TestDataGeneratorTool>();
        services.AddSingleton<JarvisAI.Infrastructure.AI.ISecurityScanner, JarvisAI.Infrastructure.AI.SecurityScanner>();
        services.AddSingleton<JarvisAI.Infrastructure.AI.IQueryOptimizer, JarvisAI.Infrastructure.AI.QueryOptimizer>();
        services.AddSingleton<JarvisAI.Application.Tools.ITool, JarvisAI.Infrastructure.Tools.DatabaseMigrationTool>();

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
