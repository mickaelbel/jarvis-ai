using JarvisAI.Application.Abstractions;
using JarvisAI.Application.AI;
using JarvisAI.Application.Agents;
using JarvisAI.Application.AutoImprovement;
using JarvisAI.Application.Budget;
using JarvisAI.Application.Commands;
using JarvisAI.Application.Memory;
using JarvisAI.Application.Planning;
using JarvisAI.Application.Plugins;
using JarvisAI.Application.Reminders;
using JarvisAI.Application.Security;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure;
using JarvisAI.Infrastructure.AI;
using JarvisAI.Infrastructure.Reminders;
using JarvisAI.Web.Hubs;
using JarvisAI.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Text.Json;

namespace JarvisAI.Web;

public static class WebAppFactory
{
    public static WebApplication Create(string[]? args = null, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args ?? Array.Empty<string>());

        builder.Logging.AddProvider(new JarvisAI.Web.Diagnostics.FileLogProvider());

        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents(o => o.DetailedErrors = true);

        builder.Services.AddSignalR(options =>
        {
            options.MaximumReceiveMessageSize = 1_048_576;
            options.EnableDetailedErrors = true; // app locale uniquement
        });

        builder.Services.AddApplication();
        builder.Services.AddInfrastructure();

        builder.Services.AddSingleton(sp =>
        {
            var options = new SecurityOptions();
            builder.Configuration.GetSection("JarvisAI:Security").Bind(options);
            return options;
        });

        builder.Services.AddSingleton(sp =>
        {
            var options = new BudgetOptions();
            builder.Configuration.GetSection("JarvisAI:Budget").Bind(options);
            return options;
        });

        builder.Services.AddSingleton(sp =>
        {
            var options = new JarvisAI.Hue.HueOptions();
            builder.Configuration.GetSection("JarvisAI:Hue").Bind(options);
            return options;
        });

        builder.Services.AddSingleton(sp =>
        {
            var options = new JarvisAI.Infrastructure.Obs.ObsOptions();
            builder.Configuration.GetSection("JarvisAI:Obs").Bind(options);
            return options;
        });

        builder.Services.AddSingleton(sp =>
        {
            var options = new JarvisAI.Infrastructure.Tools.WolOptions();
            builder.Configuration.GetSection("JarvisAI:Wol").Bind(options);
            return options;
        });

        builder.Services.AddSingleton(sp =>
        {
            var options = new JarvisAI.Application.Personality.PersonalityOptions();
            builder.Configuration.GetSection("JarvisAI:Personality").Bind(options);
            return options;
        });

        builder.Services.AddSingleton<IAIService, AIServiceAdapter>();
        builder.Services.AddSingleton<ITaskExecutionHistory, InMemoryTaskExecutionHistory>();
        builder.Services.AddSingleton<IConfirmationStore, MemoryConfirmationStore>();
        builder.Services.AddSingleton<WebConfirmationService>();
        builder.Services.AddSingleton<IUserConfirmationService>(sp => sp.GetRequiredService<WebConfirmationService>());
        builder.Services.AddSingleton<ActionHistoryService>();
        builder.Services.AddSingleton<EventBroadcaster>();
        builder.Services.AddSingleton<AgentRunBroadcaster>();
        builder.Services.AddSingleton<DebugModeStore>();
        builder.Services.AddSingleton<OllamaModelsStore>();
        builder.Services.AddSingleton<MarkdownRenderer>();
        builder.Services.AddScoped<GenerationSession>();
        builder.Services.AddScoped<ToastService>();
        builder.Services.AddSingleton<ConversationExportService>();
        builder.Services.AddSingleton<ModelDownloadProgressService>(sp =>
            new ModelDownloadProgressService(
                sp.GetRequiredService<OllamaModelService>(),
                sp.GetService<IHubContext<JarvisHub>>(),
                sp.GetService<ILogger<ModelDownloadProgressService>>()));

        builder.Services.AddSingleton<OllamaModelService>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<OllamaModelService>>();
            var httpClient = new HttpClient { BaseAddress = new Uri("http://localhost:11434"), Timeout = TimeSpan.FromMinutes(30) };
            return new OllamaModelService(httpClient, logger);
        });

        builder.Services.AddSingleton<OllamaKeepAliveService>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<OllamaKeepAliveService>>();
            var httpClient = new HttpClient { BaseAddress = new Uri("http://localhost:11434"), Timeout = TimeSpan.FromSeconds(30) };
            var idleMinutes = 15;
            if (int.TryParse(Environment.GetEnvironmentVariable("OLLAMA_IDLE_UNLOAD_MINUTES"), out var envMinutes) && envMinutes > 0)
                idleMinutes = envMinutes;
            return new OllamaKeepAliveService(
                httpClient,
                sp.GetRequiredService<IModelRouter>(),
                logger,
                Array.Empty<string>(), // llava/nomic absents de la machine : leur preload générait des erreurs 404 en boucle
                monitor: sp.GetRequiredService<OllamaRunMonitor>(),
                modelService: sp.GetRequiredService<OllamaModelService>(),
                idleUnloadTimeout: TimeSpan.FromMinutes(idleMinutes));
        });
        builder.Services.AddHostedService(sp => sp.GetRequiredService<OllamaKeepAliveService>());

        builder.Services.AddSingleton(sp =>
        {
            var eventBus = sp.GetRequiredService<IEventBus>();
            var logger = sp.GetRequiredService<ILogger<JarvisAI.Core.Engine.CoreEngine>>();
            var pluginManager = sp.GetRequiredService<IPluginManager>();
            return new JarvisAI.Core.Engine.CoreEngine(logger, eventBus, pluginManager);
        });

        // Voice services
        builder.Services.AddSingleton<JarvisAI.Application.Voice.IVoiceSettingsStore, JarvisAI.Infrastructure.Voice.VoiceSettingsStore>();
        builder.Services.AddSingleton<JarvisAI.Infrastructure.Voice.FasterWhisperSpeechToTextService>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<JarvisAI.Infrastructure.Voice.FasterWhisperSpeechToTextService>>();
            var httpClient = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:17001"), Timeout = TimeSpan.FromMinutes(10) };
            var autoStart = sp.GetRequiredService<JarvisAI.Application.Voice.IVoiceSettingsStore>().Get().AutoStart;
            return new JarvisAI.Infrastructure.Voice.FasterWhisperSpeechToTextService(httpClient, logger, autoStart);
        });
        builder.Services.AddSingleton<JarvisAI.Application.Voice.ISpeechToTextService>(sp =>
            new JarvisAI.Infrastructure.Voice.ResilientSpeechToTextService(
                sp.GetRequiredService<JarvisAI.Infrastructure.Voice.FasterWhisperSpeechToTextService>(),
                restartCallback: () => Task.FromResult(true),
                sp.GetRequiredService<ILogger<JarvisAI.Infrastructure.Voice.ResilientSpeechToTextService>>()));
        builder.Services.AddSingleton<JarvisAI.Application.Voice.IWakeWordDetector>(sp =>
            new JarvisAI.Infrastructure.Voice.OpenWakeWordService(
                new HttpClient { BaseAddress = new Uri("http://127.0.0.1:17002"), Timeout = TimeSpan.FromSeconds(15) },
                sp.GetRequiredService<ILogger<JarvisAI.Infrastructure.Voice.OpenWakeWordService>>()));
        builder.Services.AddSingleton<JarvisAI.Infrastructure.Voice.WindowsSpeechTextToSpeechService>();
        builder.Services.AddSingleton<JarvisAI.Infrastructure.Voice.PiperTextToSpeechService>();
        builder.Services.AddSingleton<JarvisAI.Infrastructure.Voice.XttsTextToSpeechService>(sp =>
            new JarvisAI.Infrastructure.Voice.XttsTextToSpeechService(
                new HttpClient { BaseAddress = new Uri("http://127.0.0.1:17003"), Timeout = TimeSpan.FromSeconds(60) },
                sp.GetRequiredService<ILogger<JarvisAI.Infrastructure.Voice.XttsTextToSpeechService>>()));
        builder.Services.AddSingleton<JarvisAI.Infrastructure.Voice.EdgeTtsTextToSpeechService>(sp =>
            new JarvisAI.Infrastructure.Voice.EdgeTtsTextToSpeechService(
                new HttpClient { BaseAddress = new Uri("http://127.0.0.1:17004"), Timeout = TimeSpan.FromSeconds(30) },
                sp.GetRequiredService<ILogger<JarvisAI.Infrastructure.Voice.EdgeTtsTextToSpeechService>>()));
        // Moteur TTS choisi dans VoiceSettings.TtsEngine : edge (défaut JARVIS), xtts (voix clonée), ou piper (local).
        builder.Services.AddSingleton<JarvisAI.Application.Voice.ITextToSpeechService>(sp =>
        {
            var windows = sp.GetRequiredService<JarvisAI.Infrastructure.Voice.WindowsSpeechTextToSpeechService>();
            var settingsStore = sp.GetRequiredService<JarvisAI.Application.Voice.IVoiceSettingsStore>();
            var ttsEngine = settingsStore.Get().TtsEngine;
            JarvisAI.Application.Voice.ITextToSpeechService primary;
            if (ttsEngine.Equals("xtts", StringComparison.OrdinalIgnoreCase))
                primary = sp.GetRequiredService<JarvisAI.Infrastructure.Voice.XttsTextToSpeechService>();
            else if (ttsEngine.Equals("piper", StringComparison.OrdinalIgnoreCase))
                primary = sp.GetRequiredService<JarvisAI.Infrastructure.Voice.PiperTextToSpeechService>();
            else
                primary = sp.GetRequiredService<JarvisAI.Infrastructure.Voice.EdgeTtsTextToSpeechService>();
            return new JarvisAI.Infrastructure.Voice.ResilientTextToSpeechService(
                primary,
                fallback: windows,
                sp.GetRequiredService<ILogger<JarvisAI.Infrastructure.Voice.ResilientTextToSpeechService>>());
        });
        builder.Services.AddSingleton<JarvisAI.Application.Voice.AmbientContextService>();
        builder.Services.AddSingleton<JarvisAI.Application.Voice.VoiceConversationService>(sp =>
        {
            var stt = sp.GetRequiredService<JarvisAI.Application.Voice.ISpeechToTextService>();
            var tts = sp.GetRequiredService<JarvisAI.Application.Voice.ITextToSpeechService>();
            var fallback = sp.GetRequiredService<JarvisAI.Infrastructure.Voice.WindowsSpeechTextToSpeechService>();
            var ai = sp.GetRequiredService<IAIService>();
            var registry = sp.GetRequiredService<IToolRegistry>();
            var settings = sp.GetRequiredService<JarvisAI.Application.Voice.IVoiceSettingsStore>();
            var logger = sp.GetRequiredService<ILogger<JarvisAI.Application.Voice.VoiceConversationService>>();
            var ambient = sp.GetRequiredService<JarvisAI.Application.Voice.AmbientContextService>();
            var picker = sp.GetRequiredService<JarvisAI.Application.Voice.IVoiceModelPicker>();
            var condenser = sp.GetRequiredService<JarvisAI.Application.AI.ConversationCondenser>();
            var episodic = sp.GetRequiredService<JarvisAI.Application.Memory.IEpisodicMemoryService>();
            var dictation = sp.GetService<JarvisAI.Application.Voice.IDictationService>();
            var ducking = sp.GetService<JarvisAI.Application.Services.IAudioDuckingService>();
            return new JarvisAI.Application.Voice.VoiceConversationService(stt, tts, ai, registry, settings, logger, fallback, ambient, picker, condenser, episodic, dictation, ducking);
        });
        builder.Services.AddSingleton<JarvisAI.Application.Voice.IVoiceConfirmationChannel>(
            sp => sp.GetRequiredService<JarvisAI.Application.Voice.VoiceConversationService>());
        builder.Services.AddSingleton<VoiceConnectionRegistry>();
        builder.Services.AddSingleton<VoiceSession>();
        builder.Services.AddSingleton<VoiceNotifier>();
        builder.Services.AddSingleton<VoiceUiState>();
        builder.Services.AddSingleton<AppVoiceStatus>();
        builder.Services.AddSingleton<ConversationSessionStore>();
        builder.Services.AddSingleton<ConversationSessionService>();
        builder.Services.AddSingleton<Lazy<IAIService>>(sp => new Lazy<IAIService>(sp.GetRequiredService<IAIService>));
        builder.Services.AddSingleton<Lazy<IEmbeddingService>>(sp => new Lazy<IEmbeddingService>(sp.GetRequiredService<IEmbeddingService>));
        builder.Services.AddSingleton<IModelSelector>(sp => new ModelSelector(
            sp.GetRequiredService<Lazy<IAIService>>(),
            sp.GetRequiredService<ModelRouterOptions>(),
            sp.GetRequiredService<ILogger<ModelSelector>>()));
        builder.Services.AddSingleton<VoiceSessionTracker>();
        builder.Services.AddSingleton<IAppLifecycleService, NoopAppLifecycle>();
        builder.Services.AddHostedService<EngineHostedService>();

        // Routage intelligent des modèles : préférences persistées, recommandation
        // (heuristique + classification IA + modèles installés + suggestion de
        // téléchargement) et sélection du modèle vocal.
        builder.Services.AddSingleton<SmartRoutingStore>();
        builder.Services.AddSingleton<ModelRecommendationService>();
        builder.Services.AddSingleton<JarvisAI.Application.Voice.IVoiceModelPicker, SmartVoiceModelPicker>();

        // Annonces proactives (rappels) : file vocale + service d'échéance.
        builder.Services.AddSingleton<ProactiveAnnouncer>();
        builder.Services.AddSingleton<JarvisAI.Web.Services.IApiGateway, JarvisAI.Web.Services.ApiGateway>();
        builder.Services.AddSingleton<JarvisAI.Web.Services.IBrowserSequenceRecorder, JarvisAI.Web.Services.BrowserSequenceRecorder>();
        builder.Services.AddSingleton<JarvisAI.Web.Services.IDiffViewerService, JarvisAI.Web.Services.DiffViewerService>();
        builder.Services.AddSingleton<JarvisAI.Web.Services.IScheduledSummaryService, JarvisAI.Web.Services.ScheduledSummaryService>();
        builder.Services.AddSingleton<JarvisAI.Web.Services.ISessionRecorder, JarvisAI.Web.Services.SessionRecorder>();
        builder.Services.AddSingleton<JarvisAI.Web.Services.IHotkeyManager, JarvisAI.Web.Services.HotkeyManager>();
        builder.Services.AddSingleton<JarvisAI.Web.Services.IWebhookService>(sp =>
            new JarvisAI.Web.Services.WebhookService(
                sp.GetRequiredService<ILogger<JarvisAI.Web.Services.WebhookService>>(),
                new HttpClient()));
        builder.Services.AddSingleton<JarvisAI.Web.Services.IScreenshotAnnotator, JarvisAI.Web.Services.ScreenshotAnnotator>();
        builder.Services.AddSingleton<JarvisAI.Web.Services.INoteService, JarvisAI.Web.Services.NoteService>();
        builder.Services.AddSingleton<JarvisAI.Web.Services.ICollaborationService, JarvisAI.Web.Services.CollaborationService>();
        builder.Services.AddSingleton<JarvisAI.Web.Services.IBackupService, JarvisAI.Web.Services.BackupService>();
        builder.Services.AddSingleton<JarvisAI.Web.Services.IAnimationService, JarvisAI.Web.Services.AnimationService>();
        builder.Services.AddSingleton<JarvisAI.Web.Services.INotificationService, JarvisAI.Web.Services.NotificationService>();
        builder.Services.AddSingleton<JarvisAI.Web.Services.IEmailService, JarvisAI.Web.Services.EmailService>();
        builder.Services.AddSingleton<JarvisAI.Web.Services.ICalendarService, JarvisAI.Web.Services.CalendarService>();
        builder.Services.AddSingleton<JarvisAI.Web.Services.IOfflineModeService>(sp =>
            new JarvisAI.Web.Services.OfflineModeService(
                sp.GetRequiredService<ILogger<JarvisAI.Web.Services.OfflineModeService>>(),
                new HttpClient()));
        builder.Services.AddSingleton<JarvisAI.Web.Services.ITutorialService, JarvisAI.Web.Services.TutorialService>();
        builder.Services.AddSingleton<JarvisAI.Web.Services.IPluginMarketplace, JarvisAI.Web.Services.PluginMarketplace>();
        builder.Services.AddSingleton<JarvisAI.Web.Services.IEmbeddableWebViewService, JarvisAI.Web.Services.EmbeddableWebViewService>();
        builder.Services.AddSingleton<JarvisAI.Web.Services.IAccessibilityService, JarvisAI.Web.Services.AccessibilityService>();
        builder.Services.AddSingleton<JarvisAI.Web.Services.IPromptTemplateService, JarvisAI.Web.Services.PromptTemplateService>();
        builder.Services.AddSingleton<JarvisAI.Web.Services.IWorkflowTemplateService, JarvisAI.Web.Services.WorkflowTemplateService>();
        builder.Services.AddSingleton<JarvisAI.Web.Services.ISessionAnalyticsService, JarvisAI.Web.Services.SessionAnalyticsService>();
        builder.Services.AddSingleton<JarvisAI.Web.Services.ICostTrackingService, JarvisAI.Web.Services.CostTrackingService>();
        builder.Services.AddSingleton<JarvisAI.Web.Services.IAutomatedReportingService>(sp =>
            new JarvisAI.Web.Services.AutomatedReportingService(
                sp.GetRequiredService<ILogger<JarvisAI.Web.Services.AutomatedReportingService>>(),
                sp.GetRequiredService<JarvisAI.Web.Services.ISessionAnalyticsService>(),
                sp.GetRequiredService<JarvisAI.Web.Services.ICostTrackingService>()));
        builder.Services.AddSingleton<JarvisAI.Web.Services.ICodeSnippetManager, JarvisAI.Web.Services.CodeSnippetManager>();
        builder.Services.AddSingleton<JarvisAI.Web.Services.IApiDocumentationGenerator, JarvisAI.Web.Services.ApiDocumentationGenerator>();
        builder.Services.AddHostedService<ReminderHostedService>();
        // Auto-création de routines par observation des habitudes vocales
        builder.Services.AddHostedService<HabitsObserverService>();
        // Mode proactif : rappels imminents + relance d'objectifs négligés
        builder.Services.AddHostedService<ProactiveService>();
        // Veille proactive : nouveaux mails non lus + mentions Discord → annonce vocale.
        builder.Services.AddHostedService<ProactiveNotifierService>();

        // Maintenance périodique de la mémoire et du cache de réponses.
        builder.Services.AddHostedService<MemoryMaintenanceService>();

        // Permet à l'hôte (JarvisAI.Desktop) d'enregistrer ses propres services hébergés,
        // p. ex. VoiceHostedService (moteur vocal Desktop always-on).
        configure?.Invoke(builder);

        var app = builder.Build();

        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error");
            app.UseHsts();
        }

        app.UseStaticFiles();
        app.UseAntiforgery();

        // Autorise les événements beforeunload/unload utilisés par Blazor Server
        // (blazor.web.js). Sans ce header, Chrome/WinUI consigne une violation
        // "Permissions policy violation: unload is not allowed in this document".
        app.Use(async (context, next) =>
        {
            context.Response.Headers["Permissions-Policy"] = "unload=self, microphone=(self), camera=(self)";
            await next();
        });

        var broadcaster = app.Services.GetRequiredService<EventBroadcaster>();
        broadcaster.Start();

        var agentBroadcaster = app.Services.GetRequiredService<AgentRunBroadcaster>();
        agentBroadcaster.Start();

        // Charge les outils auto-créés persistés (respecte le safe-mode).
        try
        {
            app.Services.GetRequiredService<ISelfImprovementManager>().LoadPersisted();
        }
        catch (Exception ex)
        {
            app.Logger.LogError(ex, "Failed to load auto-tools");
        }

        _ = app.Services.GetRequiredService<VoiceNotifier>();
        _ = app.Services.GetRequiredService<VoiceSessionTracker>();

        var webConfirmation = app.Services.GetRequiredService<WebConfirmationService>();
        webConfirmation.OnConfirmationRequested += async (request) =>
        {
            var hubContext = app.Services.GetRequiredService<IHubContext<JarvisHub>>();
            await hubContext.Clients.All.SendAsync("RequestConfirmation", new
            {
                request.ToolName,
                request.Description,
                request.RiskLevel,
                request.CorrelationId
            });
        };

        app.MapHub<JarvisHub>("/hubs/jarvis");
        app.MapHub<VoiceHub>("/hubs/voice");
        app.MapHub<AgentHub>("/hubs/agent");
        app.MapHub<OverlayHub>("/hubs/overlay");

        app.MapGet("/api/voice/devices", (JarvisAI.Application.Voice.IAudioDeviceLister lister) =>
        {
            try
            {
                return Results.Ok(new
                {
                    mics = lister.ListInputs(),
                    speakers = lister.ListOutputs()
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        app.MapGet("/api/voice/settings", (JarvisAI.Application.Voice.VoiceConversationService voice) =>
        {
            return Results.Ok(voice.GetSettings());
        });

        app.MapPost("/api/voice/settings", async (HttpContext ctx, JarvisAI.Application.Voice.VoiceConversationService voice) =>
        {
            try
            {
                var current = voice.GetSettings();
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    switch (prop.Name.ToLowerInvariant())
                    {
                        case "voiceenabled": current.VoiceEnabled = prop.Value.GetBoolean(); break;
                        case "wakewordenabled": current.WakeWordEnabled = prop.Value.GetBoolean(); break;
                        case "passivemode": current.PassiveMode = prop.Value.GetBoolean(); break;
                        case "wakewords": current.WakeWords = prop.Value.GetString() ?? current.WakeWords; break;
                        case "bargeinenabled": current.BargeInEnabled = prop.Value.GetBoolean(); break;
                        case "micdeviceid": current.MicDeviceId = prop.Value.GetString() ?? current.MicDeviceId; break;
                        case "speakerdeviceid": current.SpeakerDeviceId = prop.Value.GetString() ?? current.SpeakerDeviceId; break;
                        case "ttsvoice": current.TtsVoice = prop.Value.GetString() ?? current.TtsVoice; break;
                        case "ttslanguage": current.TtsLanguage = prop.Value.GetString() ?? current.TtsLanguage; break;
                        case "sttlanguage": current.SttLanguage = prop.Value.GetString() ?? current.SttLanguage; break;
                        case "volume": current.Volume = prop.Value.GetSingle(); break;
                        case "ttsspeed": current.TtsSpeed = Math.Clamp(prop.Value.GetSingle(), 0.5f, 2.0f); break;
                        case "autostart": current.AutoStart = prop.Value.GetBoolean(); break;
                        case "silencetimeoutms": current.SilenceTimeoutMs = prop.Value.GetInt32(); break;
                        case "vadthreshold": current.VadThreshold = prop.Value.GetSingle(); break;
                        case "maxutteranceseconds": current.MaxUtteranceSeconds = prop.Value.GetInt32(); break;
                        case "model": current.Model = prop.Value.GetString() ?? current.Model; break;
                        case "audioduckingenabled": current.AudioDuckingEnabled = prop.Value.GetBoolean(); break;
                        case "audioduckingsystemvolume": current.AudioDuckingSystemVolume = Math.Clamp(prop.Value.GetSingle(), 0f, 1f); break;
                        case "audioduckingmusicvolume": current.AudioDuckingMusicVolume = Math.Clamp(prop.Value.GetSingle(), 0f, 1f); break;
                        case "audioduckingfadem": current.AudioDuckingFadeMs = Math.Clamp(prop.Value.GetInt32(), 200, 5000); break;
                        case "audioduckingexcludedapps": current.AudioDuckingExcludedApps = prop.Value.GetString() ?? current.AudioDuckingExcludedApps; break;
                        case "audioduckingshortcut": current.AudioDuckingShortcut = prop.Value.GetString() ?? current.AudioDuckingShortcut; break;
                    }
                }
                voice.UpdateSettings(current);
                return Results.Ok(new { Status = "updated" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.ToString() }, statusCode: 500);
            }
        });

        app.MapPost("/api/voice/ducking/toggle", async (
            JarvisAI.Application.Services.IAudioDuckingService ducking,
            JarvisAI.Application.Voice.VoiceConversationService voice) =>
        {
            var settings = voice.GetSettings();
            await ducking.ToggleAsync(settings);
            return Results.Ok(new { ducking.IsDucking });
        });

        app.MapGet("/api/voice/voices", (
            JarvisAI.Application.Voice.ITextToSpeechService tts,
            JarvisAI.Infrastructure.Voice.WindowsSpeechTextToSpeechService windowsTts) =>
        {
            var piperVoices = tts.AvailableVoices
                .Select(v => new { Id = v, Name = v, Engine = "piper" });
            var windowsVoices = windowsTts.AvailableVoices
                .Select(v => new { Id = v, Name = v, Engine = "windows" });
            return Results.Ok(piperVoices.Concat(windowsVoices));
        });

        app.MapPost("/api/voice/test", async (
            JarvisAI.Application.Voice.VoiceTestRequest request,
            JarvisAI.Application.Voice.ITextToSpeechService tts,
            JarvisAI.Infrastructure.Voice.WindowsSpeechTextToSpeechService windowsTts) =>
        {
            try
            {
                byte[] wav;
                if (request.Engine == "windows")
                {
                    wav = await windowsTts.SynthesizeWavAsync(request.Text, request.Voice, request.Volume, request.Speed);
                }
                else
                {
                    wav = await tts.SynthesizeWavAsync(request.Text, request.Voice, request.Volume, request.Speed);
                }
                return Results.Bytes(wav, "audio/wav");
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { Error = ex.Message });
            }
        });

        // Synthèse d'un texte arbitraire (lecture des réponses du chat à voix
        // haute dans le navigateur), en respectant les réglages voix enregistrés.
        app.MapPost("/api/voice/speak", async (
            JarvisAI.Application.Voice.SpeakRequest request,
            JarvisAI.Application.Voice.ITextToSpeechService tts,
            JarvisAI.Application.Voice.VoiceConversationService voice) =>
        {
            try
            {
                var settings = voice.GetSettings();
                var text = (request.Text ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(text))
                    return Results.BadRequest(new { Error = "Text is required" });
                var wav = await tts.SynthesizeWavAsync(
                    text,
                    string.IsNullOrWhiteSpace(request.Voice) ? settings.TtsVoice : request.Voice,
                    settings.Volume,
                    request.Speed is > 0 ? request.Speed.Value : settings.TtsSpeed);
                return Results.Bytes(wav, "audio/wav");
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { Error = ex.Message });
            }
        });

        app.MapGet("/api/tools", (IToolRegistry registry) =>
        {
            var tools = registry.GetAll();
            return Results.Ok(tools.Select(t => new { t.Name, t.Description, t.Category, RiskLevel = t.RiskLevel.ToString(), ParameterCount = t.Parameters.Count }));
        });

        app.MapGet("/api/tools/health", (IToolRegistry registry) =>
        {
            var tools = registry.GetAll();
            var health = tools.Select(t => new
            {
                t.Name,
                t.Category,
                IsAvailable = t.IsAvailable,
                McpExposed = t.McpExpose,
                ParameterCount = t.Parameters.Count
            });
            return Results.Ok(new
            {
                TotalTools = tools.Count,
                Available = tools.Count(t => t.IsAvailable),
                Unavailable = tools.Count(t => !t.IsAvailable),
                Tools = health
            });
        });

        // Pont iPhone (portage de core/pont_iphone.py) : un Raccourci iOS envoie
        // une commande texte, Jarvis l'exécute avec l'agent complet et renvoie
        // la réponse. Protégé par un jeton configuré dans JarvisAI:IPhoneToken.
        app.MapPost("/jarvis/{token}", async (
            string token,
            HttpRequest request,
            IConfiguration config,
            JarvisAI.Application.Security.IAIService ai,
            CancellationToken ct) =>
        {
            var expected = config["JarvisAI:IPhoneToken"];
            if (string.IsNullOrWhiteSpace(expected))
                return Results.Problem("Pont iPhone désactivé : renseigne JarvisAI:IPhoneToken.", statusCode: 503);
            if (!string.Equals(token, expected, StringComparison.Ordinal))
                return Results.Unauthorized();

            using var reader = new StreamReader(request.Body);
            var command = (await reader.ReadToEndAsync(ct)).Trim();
            if (command.Length == 0)
                return Results.BadRequest("Envoie le texte de la commande dans le corps de la requête.");
            if (command.Length > 2000)
                command = command[..2000];

            try
            {
                var response = new System.Text.StringBuilder();
                await foreach (var chunk in ai.StreamChatAsync(command, null, cancellationToken: ct))
                    response.Append(chunk);
                return Results.Text(response.ToString(), "text/plain", System.Text.Encoding.UTF8);
            }
            catch (Exception ex)
            {
                return Results.Problem($"Erreur : {ex.Message}", statusCode: 500);
            }
        });

        app.MapGet("/api/tools/{name}", (string name, IToolRegistry registry) =>
        {
            var tool = registry.GetByName(name);
            return tool is not null ? Results.Ok(new { tool.Name, tool.Description, tool.Category, RiskLevel = tool.RiskLevel.ToString() }) : Results.NotFound();
        });

        app.MapGet("/api/plugins", (IPluginManager manager) =>
        {
            return Results.Ok(manager.GetAllMetadata());
        });

        app.MapPost("/api/plugins/{id}/start", async (string id, IPluginManager manager) =>
        {
            try { await manager.StartAsync(id); return Results.Ok(new { Status = "started" }); }
            catch (Exception ex) { return Results.BadRequest(new { Error = ex.Message }); }
        });

        app.MapPost("/api/plugins/{id}/stop", async (string id, IPluginManager manager) =>
        {
            try { await manager.StopAsync(id); return Results.Ok(new { Status = "stopped" }); }
            catch (Exception ex) { return Results.BadRequest(new { Error = ex.Message }); }
        });

        app.MapGet("/api/memory", async (IMemoryService memory, string? search, string? category, int? limit, int? tier) =>
        {
            var query = new MemoryQuery
            {
                TextSearch = search,
                Category = category,
                Limit = limit ?? 50,
                OrderByNewest = true,
                Tier = tier.HasValue ? (MemoryTier)tier.Value : null
            };
            var results = await memory.SearchAsync(query);
            return Results.Ok(results);
        });

        app.MapGet("/api/memory/summary", async (IMemoryService memory) =>
        {
            var all = await memory.SearchAsync(new MemoryQuery { Limit = 1000, OrderByNewest = true });
            var grouped = all
                .Where(m => m.Category != "episodic")
                .GroupBy(m => m.Category)
                .Select(g => new
                {
                    Category = g.Key,
                    Count = g.Count(),
                    Entries = g.OrderByDescending(m => m.CreatedAt).Select(m => new
                    {
                        m.Key,
                        m.Content,
                        m.Type,
                        m.Tier,
                        m.Importance,
                        m.CreatedAt,
                        m.AccessCount
                    })
                })
                .OrderByDescending(g => g.Count)
                .ToList();
            return Results.Ok(new { total = all.Count(m => m.Category != "episodic"), categories = grouped });
        });

        app.MapPost("/api/memory", async (MemoryEntry entry, IMemoryService memory) =>
        {
            var saved = await memory.SaveAsync(entry.Key, entry.Content, entry.Type, entry.Category, entry.Importance);
            return Results.Ok(saved);
        });

        app.MapPut("/api/memory/{key}", async (string key, MemoryEntry body, IMemoryService memory) =>
        {
            var existing = await memory.GetAsync(key);
            if (existing is null) return Results.NotFound();
            existing.Content = body.Content;
            existing.Category = body.Category;
            existing.Importance = body.Importance;
            await memory.SaveAsync(existing.Key, existing.Content, existing.Type, existing.Category, existing.Importance, existing.Tier == MemoryTier.Session ? TimeSpan.FromHours(1) : existing.Tier == MemoryTier.ShortTerm ? TimeSpan.FromDays(30) : null);
            return Results.Ok(existing);
        });

        app.MapDelete("/api/memory/{key}", async (string key, IMemoryService memory) =>
        {
            var deleted = await memory.DeleteAsync(key);
            return deleted ? Results.Ok() : Results.NotFound();
        });

        // Change le niveau de rétention d'un souvenir (pérenniser / rendre éphémère).
        app.MapPut("/api/memory/{key}/tier", async (string key, TierUpdateRequest body, IMemoryService memory, JarvisAI.Application.Agents.IAutomaticMemoryService autoMemory) =>
        {
            var existing = await memory.GetAsync(key);
            if (existing is null) return Results.NotFound();

            var metadata = new Dictionary<string, string>(existing.Metadata);
            metadata[body.Pinned ? JarvisAI.Application.Agents.AutomaticMemoryService.PinnedMetadataKey : "pinned"] = body.Pinned ? "true" : "false";

            MemoryTier newTier = body.Tier.Equals("LongTerm", StringComparison.OrdinalIgnoreCase) ? MemoryTier.LongTerm : MemoryTier.ShortTerm;
            TimeSpan? ttl = null;
            if (newTier == MemoryTier.ShortTerm)
            {
                var importance = Math.Max(4, autoMemory.ComputeImportance(existing.Content));
                ttl = autoMemory.DecideExpiration(importance, MemoryTier.ShortTerm);
            }

            var updated = await memory.SaveMemoryAsync(
                existing.Key, existing.Content, existing.Type, existing.Category,
                importance: existing.Importance,
                tier: newTier,
                project: existing.ProjectName,
                ttl: ttl,
                metadata: metadata);

            return Results.Ok(updated);
        });

        // Efface toutes les mémoires.
        app.MapDelete("/api/memory", async (JarvisAI.Application.Memory.IMemoryStore store) =>
        {
            var all = await store.GetAllAsync();
            foreach (var e in all)
                await store.DeleteAsync(e.Key);
            return Results.Ok(new { Deleted = all.Count });
        });

        app.MapGet("/api/memory/settings", (IMemorySettingsStore store) =>
        {
            return Results.Ok(store.Get());
        });

        app.MapPost("/api/memory/settings", (MemorySettings settings, IMemorySettingsStore store) =>
        {
            store.Save(settings);
            return Results.Ok(settings);
        });

        app.MapGet("/api/security/options", (ISecurityManager security) =>
        {
            var options = security.GetOptions();
            return Results.Ok(new
            {
                Mode = options.Mode.ToString(),
                options.RequireConfirmationForHighRisk,
                options.RequireConfirmationForMediumRisk,
                options.VoiceConfirmationEnabled,
                options.AllowDisableConfirmation,
                options.MaxActionsPerMinute,
                BlacklistedCommands = options.BlacklistedCommands.ToList(),
                WhitelistedTools = options.WhitelistedTools.ToList()
            });
        });

        app.MapPost("/api/security/options", async (HttpContext ctx, ISecurityManager security) =>
        {
            // Fusion partielle : ne modifie que les champs présents dans le corps,
            // sans réinitialiser la liste des chemins autorisés ni la liste noire.
            var current = security.GetOptions();
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
            if (doc.RootElement.TryGetProperty("mode", out var mode) && mode.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var raw = mode.GetString();
                current.Mode = raw switch
                {
                    "Autonomous" or "autonomous" or "Developer" or "developer" or "dev" => OperationMode.Autonomous,
                    _ => OperationMode.Safe
                };
            }
            if (doc.RootElement.TryGetProperty("requireConfirmationForHighRisk", out var hi) && hi.ValueKind == System.Text.Json.JsonValueKind.True)
                current.RequireConfirmationForHighRisk = true;
            if (doc.RootElement.TryGetProperty("requireConfirmationForMediumRisk", out var med) && med.ValueKind == System.Text.Json.JsonValueKind.True)
                current.RequireConfirmationForMediumRisk = true;
            if (doc.RootElement.TryGetProperty("voiceConfirmationEnabled", out var vc) && vc.ValueKind == System.Text.Json.JsonValueKind.True)
                current.VoiceConfirmationEnabled = true;
            if (doc.RootElement.TryGetProperty("allowDisableConfirmation", out var adc) && adc.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                current.AllowDisableConfirmation = adc.GetBoolean();
            security.Configure(current);
            return Results.Ok(new { Status = "updated" });
        });

        app.MapGet("/api/security/pending", async (IConfirmationStore store) =>
        {
            var pending = await store.GetPendingAsync();
            return Results.Ok(pending);
        });

        app.MapPost("/api/security/confirm", async (Guid requestId, bool confirmed, IConfirmationStore store) =>
        {
            var method = ConfirmationMethod.Text;
            var result = confirmed
                ? ConfirmationResult.Accepted(method, TimeSpan.Zero, "api_confirmed")
                : ConfirmationResult.Denied(method, TimeSpan.Zero, "api_denied");
            await store.ResolveRequestAsync(requestId, result);
            return Results.Ok(new { Status = confirmed ? "confirmed" : "denied" });
        });

        app.MapGet("/api/tasks", (ITaskExecutionHistory history) =>
        {
            return Results.Ok(history.GetRecentHistory(50));
        });

        app.MapGet("/api/tasks/{correlationId}", (Guid correlationId, ITaskExecutionHistory history) =>
        {
            var record = history.GetHistory(correlationId);
            return record is null ? Results.NotFound() : Results.Ok(record);
        });

        app.MapGet("/api/routing/analytics", (RoutingFeedbackStore store) =>
        {
            return Results.Ok(store.GetAnalytics());
        });

        app.MapPost("/api/routing/feedback", (RoutingFeedbackRequest req, RoutingFeedbackStore store) =>
        {
            switch (req.Type)
            {
                case "thumbs_up": store.RecordThumbsUp(req.Model, req.Category); break;
                case "thumbs_down": store.RecordThumbsDown(req.Model, req.Category); break;
                case "regeneration": store.RecordRegeneration(req.Model, req.Category); break;
            }
            return Results.Ok(new { ok = true });
        });

        app.MapGet("/api/tasks/{correlationId}/export", (Guid correlationId, ITaskExecutionHistory history) =>
        {
            var record = history.GetHistory(correlationId);
            if (record is null) return Results.NotFound();
            var json = JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true });
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            return Results.Stream(new MemoryStream(bytes), "application/json", $"jarvis-task-{correlationId:N}.json");
        });

        app.MapGet("/api/tools/usage", (JarvisAI.Application.Tools.IToolUsageTracker tracker) =>
        {
            return Results.Ok(tracker.GetAll());
        });

        app.MapGet("/api/tools/usage/{toolName}", (string toolName, JarvisAI.Application.Tools.IToolUsageTracker tracker) =>
        {
            var stat = tracker.Get(toolName);
            return stat is null ? Results.NotFound() : Results.Ok(stat);
        });

        app.MapGet("/api/models/ollama/status", async (OllamaModelService ollama) =>
        {
            var available = await ollama.IsAvailableAsync();
            return Results.Ok(new { Available = available });
        });

        app.MapGet("/api/models/ollama/local", async (OllamaModelService ollama) =>
        {
            var models = await ollama.GetLocalModelsAsync();
            return Results.Ok(models);
        });

        app.MapGet("/api/models/catalog", () =>
        {
            return Results.Ok(OllamaModelService.GetCatalog());
        });

        app.MapGet("/api/models/available", (IEnumerable<IAIProvider> providers) =>
        {
            var items = new List<AvailableModelDto>();

            foreach (var provider in providers)
            {
                if (provider is OllamaProvider || provider is RoutingProvider) continue;
                if (!provider.IsAvailable) continue;
                foreach (var m in provider.KnownModels.Distinct(StringComparer.OrdinalIgnoreCase))
                    items.Add(new AvailableModelDto(m, "cloud", provider.Name, true, ""));
            }

            var catalog = OllamaModelService.GetCatalog();
            foreach (var c in catalog)
                items.Add(new AvailableModelDto(c.Name, "catalog", "Ollama (gratuit)", false, c.DownloadSize));

            return Results.Ok(items);
        });

        app.MapGet("/api/ai-providers", (AiProviderSettingsStore store) =>
        {
            var settings = store.Get();
            var result = AiProviderCatalog.All.Select(entry =>
            {
                settings.TryGetValue(entry.Key, out var s);
                var added = s is not null;
                return new AiProviderDto(
                    entry.Key,
                    added && !string.IsNullOrWhiteSpace(s!.DisplayName) ? s.DisplayName : entry.DisplayName,
                    entry.Description,
                    entry.RequiresKey,
                    s?.Enabled ?? true,
                    s?.ApiKey ?? "",
                    string.IsNullOrWhiteSpace(s?.BaseUrl) ? entry.DefaultBaseUrl : s.BaseUrl,
                    string.IsNullOrWhiteSpace(s?.DefaultModel) ? entry.DefaultModel : s.DefaultModel,
                    entry.ChatPath,
                    entry.Models,
                    entry.ModelPrefixes,
                    s?.Models ?? new List<string>(),
                    added);
            }).ToArray();
            return Results.Ok(result);
        });

        app.MapPost("/api/ai-providers", (SaveAiProvidersRequest request, AiProviderSettingsStore store) =>
        {
            if (request?.Providers is null)
                return Results.BadRequest(new { Error = "No providers supplied" });

            var settings = store.Get();
            var addedKeys = new HashSet<string>(
                request.Providers.Select(p => p.Key),
                StringComparer.OrdinalIgnoreCase);

            foreach (var p in request.Providers)
            {
                var entry = AiProviderCatalog.Find(p.Key);
                if (entry is null) continue;
                settings[p.Key] = new AiProviderSettings
                {
                    Enabled = p.Enabled,
                    DisplayName = string.IsNullOrWhiteSpace(p.DisplayName) ? entry.DisplayName : p.DisplayName,
                    ApiKey = p.ApiKey ?? "",
                    BaseUrl = p.BaseUrl ?? "",
                    DefaultModel = p.DefaultModel ?? "",
                    Models = p.CustomModels?.ToList() ?? new List<string>()
                };
            }

            // Les fournisseurs du catalogue absents de la requête ont été
            // « enlevés » par l'utilisateur : on supprime leur configuration.
            foreach (var key in settings.Keys.ToList())
            {
                if (AiProviderCatalog.Find(key) is not null && !addedKeys.Contains(key))
                    settings.Remove(key);
            }

            store.Save(settings);
            return Results.Ok(new { Status = "saved" });
        });

        app.MapPost("/api/models/ollama/pull", async (string name, OllamaModelService ollama) =>
        {
            var results = await ollama.PullModelAsync(name);
            return Results.Ok(new { Status = results.Count > 0 ? "completed" : "failed" });
        });

        app.MapPost("/api/models/ollama/delete", async (string name, OllamaModelService ollama) =>
        {
            var success = await ollama.DeleteModelAsync(name);
            return success ? Results.Ok(new { Status = "deleted" }) : Results.BadRequest(new { Error = "Failed to delete" });
        });

        app.MapGet("/api/diagnostics/ollama", async (OllamaModelService ollama) =>
        {
            var diag = await ollama.GetDiagnosticsAsync();
            return Results.Ok(diag);
        });

        app.MapGet("/api/ollama/metrics", (OllamaRunMonitor monitor) =>
        {
            return Results.Ok(new
            {
                Last = monitor.Last is null ? null : new
                {
                    monitor.Last.Model,
                    monitor.Last.PromptTokens,
                    monitor.Last.CompletionTokens,
                    monitor.Last.EvalDurationMs,
                    monitor.Last.OccurredAt,
                    TokensPerSecond = Math.Round(monitor.Last.TokensPerSecond, 1)
                },
                Recent = monitor.Recent.Select(r => new
                {
                    r.Model,
                    r.PromptTokens,
                    r.CompletionTokens,
                    r.EvalDurationMs,
                    r.OccurredAt,
                    TokensPerSecond = Math.Round(r.TokensPerSecond, 1)
                }).ToArray()
            });
        });

        app.MapGet("/api/ollama/models-path", (OllamaModelsStore store) =>
        {
            var effective = store.EffectivePath();
            return Results.Ok(new
            {
                Path = effective,
                Configured = store.ModelsPath,
                EnvPath = OllamaModelsStore.UserEnvModelsPath(),
                DefaultPath = OllamaModelsStore.DefaultModelsPath(),
                Exists = Directory.Exists(effective)
            });
        });

        app.MapPost("/api/ollama/models-path", (OllamaModelsPathRequest request, OllamaModelsStore store) =>
        {
            var path = request.Path?.Trim();
            if (string.IsNullOrWhiteSpace(path))
            {
                store.ModelsPath = null;
                Environment.SetEnvironmentVariable("OLLAMA_MODELS", null, EnvironmentVariableTarget.User);
                return Results.Ok(new
                {
                    Ok = true,
                    Path = store.EffectivePath(),
                    Message = "Dossier par défaut restauré. Redémarrez Ollama pour appliquer le changement."
                });
            }

            try
            {
                var full = Path.GetFullPath(path);
                store.ModelsPath = full;
                Environment.SetEnvironmentVariable("OLLAMA_MODELS", full, EnvironmentVariableTarget.User);
                Directory.CreateDirectory(full);
                return Results.Ok(new
                {
                    Ok = true,
                    Path = full,
                    Message = "Dossier enregistré. Redémarrez Ollama (et l'application) pour appliquer le changement."
                });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { Ok = false, Error = ex.Message });
            }
        });

        app.MapGet("/api/ollama/processes", async (OllamaModelService ollama) =>
        {
            var processes = await ollama.GetRunningModelsAsync();
            return Results.Ok(processes);
        });

        app.MapPost("/api/ollama/preload", async (string model, OllamaKeepAliveService keepAlive) =>
        {
            if (string.IsNullOrWhiteSpace(model))
                return Results.BadRequest(new { Error = "Model name is required" });
            var success = await keepAlive.PreloadAsync(model.Trim());
            return success
                ? Results.Ok(new { Status = "preloaded" })
                : Results.BadRequest(new { Error = "Failed to preload model" });
        });

        app.MapGet("/api/ollama/keepalive", (OllamaKeepAliveService keepAlive) =>
        {
            var state = keepAlive.GetState();
            return Results.Ok(new
            {
                state.WarmModels,
                state.PendingModels,
                state.IdleUnloadEnabled,
                IdleUnloadTimeoutMinutes = state.IdleUnloadTimeout.TotalMinutes
            });
        });

        app.MapGet("/api/models/router/status", (IModelRouter router) =>
        {
            var options = router.Options;
            return Results.Ok(new
            {
                FastModel = options.FastModel,
                ReasoningModel = options.ReasoningModel,
                KeepAlive = options.KeepAlive,
                LongConversationThreshold = options.LongConversationThreshold,
                LastRoute = router.LastRoute is null ? null : new
                {
                    router.LastRoute.Model,
                    router.LastRoute.Profile,
                    router.LastRoute.Reason,
                    router.LastRoute.Timestamp
                },
                RecentRoutes = router.RecentRoutes.Select(r => new
                {
                    r.Model,
                    r.Profile,
                    r.Reason,
                    r.Timestamp
                }).ToArray()
            });
        });

        app.MapGet("/api/models/router/last", (IModelRouter router) =>
        {
            if (router.LastRoute is null) return Results.NotFound();
            var r = router.LastRoute;
            return Results.Ok(new { r.Model, r.Profile, r.Reason, r.Timestamp });
        });

        // Routage intelligent des modèles
        app.MapGet("/api/smart-routing/settings", (SmartRoutingStore store) =>
        {
            return Results.Ok(store.Get());
        });

        app.MapPost("/api/smart-routing/settings", (SmartRoutingSettings settings, SmartRoutingStore store) =>
        {
            store.Save(settings);
            return Results.Ok(new { Status = "updated" });
        });

        app.MapGet("/api/smart-routing/recommend", async (string text, ModelRecommendationService service, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(text))
                return Results.BadRequest(new { Error = "Text is required" });

            var route = await service.ResolveAsync(text, null, allowMultiStep: true, ct);
            return Results.Ok(new
            {
                route.Model,
                Profile = route.Profile.ToString(),
                route.Reason,
                route.MultiStep,
                DownloadSuggestion = route.DownloadSuggestion is null ? null : new
                {
                    route.DownloadSuggestion.Name,
                    route.DownloadSuggestion.Description,
                    route.DownloadSuggestion.Parameters,
                    route.DownloadSuggestion.DownloadSize
                }
            });
        });

        app.MapPost("/api/smart-routing/test", async (SmartRoutingTestRequest request, ModelRecommendationService service, IAIProvider ai, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Text))
                return Results.BadRequest(new { Error = "Text is required" });

            var route = await service.ResolveAsync(request.Text, null, allowMultiStep: true, ct);
            var model = string.IsNullOrWhiteSpace(request.Model) ? route.Model : request.Model;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            AIResponse response;
            try
            {
                response = await ai.ChatAsync(new AIRequest(
                    systemPrompt: "Tu es Jarvis, un assistant IA. Réponds de façon concise et utile.",
                    messages: new[] { AIMessage.User(request.Text) },
                    model: model,
                    temperature: 0.7f,
                    maxTokens: 1024), ct);
            }
            catch (Exception ex)
            {
                sw.Stop();
                return Results.Ok(new
                {
                    Model = model,
                    Profile = route.Profile.ToString(),
                    route.Reason,
                    route.MultiStep,
                    Success = false,
                    Error = ex.Message,
                    Response = "",
                    ElapsedMs = sw.ElapsedMilliseconds,
                    TokensPerSecond = 0
                });
            }
            sw.Stop();

            return Results.Ok(new
            {
                Model = model,
                Profile = route.Profile.ToString(),
                route.Reason,
                route.MultiStep,
                Success = response.Success,
                Error = response.ErrorMessage,
                Response = response.Content,
                ElapsedMs = sw.ElapsedMilliseconds,
                TokensPerSecond = response.TokensPerSecond,
                DownloadSuggestion = route.DownloadSuggestion is null ? null : new
                {
                    route.DownloadSuggestion.Name,
                    route.DownloadSuggestion.Description,
                    route.DownloadSuggestion.Parameters,
                    route.DownloadSuggestion.DownloadSize
                }
            });
        });

        // Rappels (annonce vocale proactive à l'échéance)
        app.MapGet("/api/reminders", (IReminderService reminders) =>
        {
            return Results.Ok(reminders.GetAll());
        });

        app.MapPost("/api/reminders", (ReminderRequest request, IReminderService reminders) =>
        {
            var dueAt = RemindersTool.ParseWhen(request.When);
            if (dueAt is null)
                return Results.BadRequest(new { Error = "Format de délai non reconnu. Ex : \"10 minutes\", \"2 heures\", \"18:30\", \"demain 9h\", \"2026-08-08T18:00\"." });
            if (dueAt <= DateTime.Now)
                return Results.BadRequest(new { Error = "La date demandée est déjà passée." });

            var reminder = reminders.Add(request.Text, dueAt.Value);
            return Results.Ok(reminder);
        });

        app.MapDelete("/api/reminders/{id}", (string id, IReminderService reminders) =>
        {
            return reminders.Cancel(id) ? Results.Ok(new { Status = "cancelled" }) : Results.NotFound();
        });

        app.MapGet("/api/app-voice/status", (AppVoiceStatus status) =>
        {
            return Results.Ok(status.Snapshot());
        });

        // Mode debug : déverrouillage par mot de passe (une seule fois par PC)
        // puis activation/désactivation. L'état est persisté dans
        // %LOCALAPPDATA%\JarvisAI\debug-mode.json.
        app.MapGet("/api/debug/status", (DebugModeStore store) =>
        {
            return Results.Ok(store.GetState());
        });

        app.MapPost("/api/debug/unlock", (DebugUnlockRequest request, DebugModeStore store) =>
        {
            if (store.TryUnlock(request?.Password))
                return Results.Ok(new { Status = "unlocked", store.Enabled, store.Unlocked });
            return Results.Json(new { Error = "Mot de passe incorrect" }, statusCode: 401);
        });

        app.MapPost("/api/debug/enabled", (DebugEnabledRequest request, DebugModeStore store) =>
        {
            if (request?.Enabled == true && !store.Unlocked)
                return Results.Json(new { Error = "Mode debug verrouillé : mot de passe requis" }, statusCode: 403);
            store.Enabled = request?.Enabled ?? false;
            return Results.Ok(new { Status = "updated", store.Enabled, store.Unlocked });
        });

        // Flux de debug : derniers appels d'outils exécutés par l'IA (commande,
        // fichier, résultat complet) pour le panneau de debug vocal.
        app.MapGet("/api/debug/feed", (JarvisAI.Application.Debug.IDebugToolFeed feed) =>
        {
            return Results.Ok(feed.Recent(100));
        });

        // Cycle de vie de l'application : l'hôte Desktop (JarvisAI.Desktop)
        // enregistre une implémentation réelle de IAppLifecycleService qui
        // prend le relais du NoopAppLifecycle enregistré par défaut ici.
        app.MapGet("/api/app/status", () =>
        {
            return Results.Ok(new { Status = "ok", Time = DateTimeOffset.UtcNow });
        });

        // ── Intégrations externes (identifiants éditables dans Paramètres) ─────
        app.MapGet("/api/integrations", (JarvisAI.Infrastructure.Integrations.IntegrationsStore store) =>
            Results.Ok(store.Get()));
        app.MapPost("/api/integrations", (JarvisAI.Infrastructure.Integrations.IntegrationsSettings settings,
            JarvisAI.Infrastructure.Integrations.IntegrationsStore store) =>
        {
            store.Save(settings);
            return Results.Ok(new { Status = "updated" });
        });

        // Test d'une intégration : appel réel mais sans effet de bord (lecture seule).
        // Clés : google, gmail, discord, instagram, twilio, alexa, hermes.
        app.MapPost("/api/integrations/test/{key}", async (HttpContext http, string key, IToolRegistry registry) =>
        {
            if (!LocalGuard.IsLocal(http)) return Results.StatusCode(403);
            var context = new AgentContext($"[test] {key}", "tests");

            var (toolName, args, note) = key.ToLowerInvariant() switch
            {
                "google" => ("agenda", new Dictionary<string, string> { ["action"] = "list",
                    ["start"] = DateTime.Now.AddDays(-1).ToString("yyyy-MM-ddT00:00:00"),
                    ["end"] = DateTime.Now.AddDays(1).ToString("yyyy-MM-ddT23:59:59") }, "lecture agenda 48 h"),
                "gmail" => ("mail", new Dictionary<string, string> { ["action"] = "list", ["max"] = "1" }, "lecture du dernier mail"),
                "discord" => ("discord", new Dictionary<string, string> { ["action"] = "mentions" }, "lecture des mentions"),
                "instagram" => ("instagram", new Dictionary<string, string> { ["action"] = "list" }, "comptes configurés"),
                "twilio" => ("twilio", new Dictionary<string, string> { ["action"] = "status" }, "vérification API"),
                "alexa" => ("alexa", new Dictionary<string, string> { ["action"] = "status" }, "vérification appareils"),
                "homeassistant" => ("homeassistant", new Dictionary<string, string> { ["action"] = "status" }, "vérification API"),
                _ => ("", new Dictionary<string, string>(), "")
            };

            if (toolName == "")
                return Results.BadRequest(new { error = $"Pas de test disponible pour « {key} »." });

            var tool = registry.GetByName(toolName);
            if (tool is null)
                return Results.NotFound(new { error = $"Outil « {toolName} » introuvable." });

            try
            {
                var result = await tool.ExecuteAsync(context, args, http.RequestAborted);
                return Results.Ok(new
                {
                    success = result.Success,
                    output = result.Success ? Truncate(result.Output ?? "", 400) : result.ErrorMessage,
                    note
                });
            }
            catch (Exception ex)
            {
                return Results.Ok(new { success = false, output = ex.Message, note });
            }
        });

        static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 3)] + "…";

        // ── Gestes main (tracker MediaPipe local → étiquettes uniquement) ──────
        app.MapPost("/api/gestes", async (HttpContext http, GestureRequest request, IToolRegistry registry) =>
        {
            if (!LocalGuard.IsLocal(http)) return Results.StatusCode(403);
            var cfgGestes = http.RequestServices.GetRequiredService<JarvisAI.Infrastructure.Integrations.IntegrationsStore>().Get().Gestes;
            var token = cfgGestes.Token;
            if (!string.IsNullOrWhiteSpace(token))
            {
                var provided = http.Request.Headers["X-Gestes-Token"].FirstOrDefault();
                if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                        System.Text.Encoding.UTF8.GetBytes(provided ?? ""),
                        System.Text.Encoding.UTF8.GetBytes(token)))
                    return Results.StatusCode(401);
            }

            var geste = request.Geste?.ToLowerInvariant() ?? "";

            // Mapping personnalisé (Paramètres → Intégrations → Gestes.Actions) sinon défauts :
            // main_ouverte=play/pause · pincement_haut/bas=luminosité salon ±10 · poing=mute
            if (cfgGestes.Actions.TryGetValue(geste, out var spec) && !string.IsNullOrWhiteSpace(spec))
            {
                try
                {
                    var parts = spec.Split(':', 2);
                    var tool = registry.GetByName(parts[0].Trim());
                    if (tool is null) return Results.Ok(new { ok = false, error = $"outil inconnu : {parts[0]}" });
                    var callArgs = new Dictionary<string, string>();
                    if (parts.Length == 2)
                    {
                        var segs = parts[1].Split(';');
                        if (!string.IsNullOrWhiteSpace(segs[0]))
                            callArgs["action"] = segs[0].Trim();
                        foreach (var kv in segs.Skip(1))
                        {
                            var idx = kv.IndexOf('=');
                            if (idx > 0) callArgs[kv[..idx].Trim()] = kv[(idx + 1)..].Trim();
                        }
                    }
                    await tool.ExecuteAsync(new AgentContext($"[geste] {geste}", "gestes"), callArgs);
                    return Results.Ok(new { ok = true, geste, spec });
                }
                catch (Exception ex)
                {
                    return Results.Ok(new { ok = false, error = ex.Message });
                }
            }

            var args = new Dictionary<string, string>();
            try
            {
                switch (geste)
                {
                    case "pincement_haut":
                    case "pincement_bas":
                        // Luminosité salon ±10 via Hue direct
                        var hue = registry.GetByName("hue");
                        var hueArgs = new Dictionary<string, string>
                        {
                            ["action"] = "group",
                            ["id"] = cfgGestes.HueGroupId,
                            ["brightness"] = ""
                        };
                        if (hue is not null) await hue.ExecuteAsync(new AgentContext($"[geste] {geste}", "gestes"), hueArgs);
                        return Results.Ok(new { ok = true });
                    case "main_ouverte":
                        args["action"] = "play_pause";
                        break;
                    case "poing":
                        args["action"] = "mute";
                        break;
                    default:
                        return Results.Ok(new { ok = false, unknown = geste });
                }
                var media = registry.GetByName("media");
                if (media is not null)
                    await media.ExecuteAsync(new AgentContext($"[geste] {geste}", "gestes"), args);
                return Results.Ok(new { ok = true });
            }
            catch (Exception ex)
            {
                return Results.Ok(new { ok = false, error = ex.Message });
            }
        });

        // ── Catalogue VRAM (quels modèles locaux rentrent dans le GPU ?) ──
        app.MapGet("/api/vram", (HttpContext http) =>
        {
            if (!LocalGuard.IsLocal(http)) return Results.StatusCode(403);
            var vram = http.RequestServices.GetRequiredService<JarvisAI.Infrastructure.Models.VramCatalogService>().Sond();
            return Results.Ok(new
            {
                gpu = vram.GpuNom,
                totalGo = Math.Round(vram.VramTotalGo, 1),
                utiliseeGo = Math.Round(vram.VramUtiliseeGo, 1),
                catalogue = vram.Catalogue.Select(c => new
                {
                    nom = c.Modele.Nom,
                    famille = c.Modele.Famille,
                    vramGo = c.Modele.VramGo,
                    usage = c.Modele.Usage,
                    conseille = c.Modele.Recommande,
                    rentre = c.Rentre
                })
            });
        });

        // ── Serveur MCP (pont HTTP local ; le binaire stdio JarvisAI.Mcp s'y connecte) ──
        app.MapGet("/api/mcp/tools", (HttpContext http, IToolRegistry registry) =>
        {
            if (!LocalGuard.IsLocal(http)) return Results.StatusCode(403);
            var tools = registry.GetAll()
                .Where(t => t.McpExpose)
                .Select(t => new
                {
                    name = t.Name,
                    description = t.Description,
                    risk = t.RiskLevel.ToString(),
                    parameters = t.Parameters.Select(p => new { name = p.Name, type = p.Type.Name, required = p.Required, description = p.Description })
                });
            return Results.Ok(tools);
        });
        app.MapPost("/api/mcp/call", async (HttpContext http, McpCallRequest request, IToolRegistry registry) =>
        {
            if (!LocalGuard.IsLocal(http)) return Results.StatusCode(403);
            var tool = registry.GetByName(request.Name);
            if (tool is null || !tool.McpExpose)
                return Results.NotFound(new { error = $"Outil '{request.Name}' introuvable ou non exposé au MCP." });

            // Confirmation N2/N3 via MCP : exige args.confirm = true explicite.
            if (tool.RiskLevel >= SecurityRiskLevel.Medium &&
                !string.Equals(request.Args.GetValueOrDefault("confirmed"), "true", StringComparison.OrdinalIgnoreCase))
                return Results.Ok(new
                {
                    needsConfirmation = true,
                    message = $"« {tool.Name} » est une action sensible ({tool.RiskLevel}). Demande confirmation à l'utilisateur puis relance avec confirmed=true."
                });

            var context = new AgentContext($"[MCP] {request.Name}", "mcp");
            var result = await tool.ExecuteAsync(context, request.Args, http.RequestAborted);
            return Results.Ok(new { success = result.Success, output = result.Success ? result.Output : result.ErrorMessage });
        });

        app.MapPost("/api/app/reload", (IAppLifecycleService lifecycle) =>
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(400);
                lifecycle.Reload();
            });
            return Results.Ok(new { Status = "reloading" });
        });

        app.MapPost("/api/app/restart", (IAppLifecycleService lifecycle) =>
        {
            _ = Task.Run(async () =>
            {
                // Laisse le temps à la réponse HTTP d'être envoyée avant de fermer l'app.
                await Task.Delay(400);
                lifecycle.Restart();
            });
            return Results.Ok(new { Status = "restarting" });
        });

        app.MapPost("/api/app/stop", (IAppLifecycleService lifecycle) =>
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(400);
                lifecycle.Stop();
            });
            return Results.Ok(new { Status = "stopping" });
        });

        // Auto-amélioration
        app.MapGet("/api/auto/status", (ISelfImprovementManager sim) =>
        {
            return Results.Ok(new { SafeMode = sim.SafeMode, Tools = sim.ListTools().Count, Lessons = sim.GetLessons().Count });
        });

        app.MapGet("/api/auto/tools", (ISelfImprovementManager sim) => Results.Ok(sim.ListTools()));

        app.MapGet("/api/auto/tools/{name}", (string name, ISelfImprovementManager sim) =>
        {
            var spec = sim.GetTool(name);
            return spec is null ? Results.NotFound() : Results.Ok(spec);
        });

        app.MapPost("/api/auto/tools", (AutoToolSpec spec, ISelfImprovementManager sim) =>
        {
            var result = sim.CreateTool(spec, "user");
            return result.Success
                ? Results.Ok(new { Status = "created", Name = result.Name })
                : Results.BadRequest(new { Error = result.Error });
        });

        app.MapDelete("/api/auto/tools/{name}", (string name, ISelfImprovementManager sim) =>
        {
            return sim.RemoveTool(name) ? Results.Ok(new { Status = "removed" }) : Results.NotFound();
        });

        app.MapPost("/api/auto/tools/{name}/enable", (string name, ISelfImprovementManager sim) =>
        {
            return sim.EnableTool(name) ? Results.Ok(new { Status = "enabled" }) : Results.NotFound();
        });

        app.MapPost("/api/auto/tools/{name}/disable", (string name, ISelfImprovementManager sim) =>
        {
            return sim.DisableTool(name) ? Results.Ok(new { Status = "disabled" }) : Results.NotFound();
        });

        app.MapGet("/api/auto/lessons", (ISelfImprovementManager sim) => Results.Ok(sim.GetLessons()));

        app.MapPost("/api/auto/lessons", (AddLessonRequest request, ISelfImprovementManager sim) =>
        {
            sim.AddLesson(request.Lesson);
            return Results.Ok(new { Status = "added" });
        });

        app.MapPost("/api/auto/safemode", (SafeModeRequest request, ISelfImprovementManager sim) =>
        {
            sim.SafeMode = request.Enabled;
            return Results.Ok(new { Status = sim.SafeMode ? "safe mode enabled" : "safe mode disabled" });
        });

        app.MapRazorComponents<JarvisAI.Web.Components.App>()
            .AddInteractiveServerRenderMode();

        return app;
    }
}

public sealed record AddLessonRequest(string Lesson);
public sealed record OllamaModelsPathRequest(string? Path);
public sealed record SafeModeRequest(bool Enabled);
public sealed record DebugUnlockRequest(string? Password);
public sealed record DebugEnabledRequest(bool Enabled);
public sealed record SmartRoutingTestRequest(string Text, string? Model);
public sealed record AvailableModelDto(string Name, string Source, string Label, bool Installed, string Size);
public sealed record AiProviderDto(
    string Key,
    string DisplayName,
    string Description,
    bool RequiresKey,
    bool Enabled,
    string ApiKey,
    string BaseUrl,
    string DefaultModel,
    string ChatPath,
    IReadOnlyList<string> Models,
    IReadOnlyList<string> Prefixes,
    IReadOnlyList<string> CustomModels,
    bool Added);
public sealed record SaveAiProvidersRequest(IReadOnlyList<AiProviderDto> Providers);
public sealed record ReminderRequest(string Text, string When);
public sealed record McpCallRequest(string Name, Dictionary<string, string> Args);
public sealed record GestureRequest(string Geste);
public sealed record TierUpdateRequest(string Tier, bool Pinned);

// Requêtes MCP/panneau : uniquement depuis localhost (garde anti-ngrok, façon core/panneau.py)
public static class LocalGuard
{
    public static bool IsLocal(HttpContext http)
    {
        if (http.Request.Headers.ContainsKey("X-Forwarded-For")) return false;
        var host = http.Request.Host.Host.ToLowerInvariant();
        return host is "localhost" or "127.0.0.1" or "::1" or "[::1]";
    }
}

