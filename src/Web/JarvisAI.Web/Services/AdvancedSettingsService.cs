using System.Text.Json;

namespace JarvisAI.Web.Services;

/// <summary>
/// 60+ AI-controllable settings. The AI can read/write any of these via the SettingsTool.
/// </summary>
public sealed class AdvancedSettings
{
    // ── AI Behavior ──
    public float Temperature { get; set; } = 0.7f;
    public int MaxTokens { get; set; } = 4096;
    public float TopP { get; set; } = 0.9f;
    public float FrequencyPenalty { get; set; } = 0.0f;
    public float PresencePenalty { get; set; } = 0.0f;
    public bool StreamingEnabled { get; set; } = true;
    public bool ToolPruningEnabled { get; set; } = true;
    public int MaxToolCallsPerTurn { get; set; } = 16;
    public bool AutoSummarizeLongContext { get; set; } = true;
    public int ContextWindowTokens { get; set; } = 8192;
    public bool AllowMultiStepPlanning { get; set; } = true;
    public int MaxPlanningSteps { get; set; } = 10;
    public bool EnableReflection { get; set; } = true;
    public bool EnableSelfCorrection { get; set; } = true;
    public string ResponseLanguage { get; set; } = "français";
    public string SystemPromptStyle { get; set; } = "concise";

    // ── Memory & Context ──
    public bool MemoryEnabled { get; set; } = true;
    public int MemoryMaxEntries { get; set; } = 500;
    public bool AutoRememberFacts { get; set; } = true;
    public bool ContextPersistence { get; set; } = true;
    public int ContextRetentionTurns { get; set; } = 20;
    public bool UseSemanticSearch { get; set; } = true;
    public float SemanticSimilarityThreshold { get; set; } = 0.75f;
    public bool CompressOldMessages { get; set; } = true;
    public int CompressionThresholdTokens { get; set; } = 6000;

    // ── Tools & Capabilities ──
    public bool FileReadWriteEnabled { get; set; } = true;
    public bool WebSearchEnabled { get; set; } = true;
    public bool CodeExecutionEnabled { get; set; } = true;
    public bool ImageGenerationEnabled { get; set; } = false;
    public bool VoiceEnabled { get; set; } = true;
    public bool BrowserAutomationEnabled { get; set; } = true;
    public bool GitIntegrationEnabled { get; set; } = true;
    public bool CalendarIntegrationEnabled { get; set; } = true;
    public bool NotificationEnabled { get; set; } = true;
    public bool AutoCommitEnabled { get; set; } = false;
    public bool ProcessManagementEnabled { get; set; } = true;
    public int MaxFileSizeKB { get; set; } = 10240;
    public int WebSearchMaxResults { get; set; } = 5;
    public int CodeExecutionTimeoutMs { get; set; } = 30000;

    // ── Security & Privacy ──
    public bool ConfirmDangerousActions { get; set; } = true;
    public bool SandboxMode { get; set; } = false;
    public bool AuditLogEnabled { get; set; } = true;
    public bool DataEncryptionEnabled { get; set; } = true;
    public int SessionTimeoutMinutes { get; set; } = 60;
    public bool AutoLockEnabled { get; set; } = false;
    public string AllowedFilePatterns { get; set; } = "*";
    public string BlockedCommands { get; set; } = "";
    public bool TelemetryEnabled { get; set; } = false;

    // ── Notifications ──
    public bool ToastNotifications { get; set; } = true;
    public bool SoundNotifications { get; set; } = false;
    public bool DesktopNotifications { get; set; } = true;
    public int NotificationDurationMs { get; set; } = 4000;
    public bool NotifyOnTaskComplete { get; set; } = true;
    public bool NotifyOnError { get; set; } = true;

    // ── Performance ──
    public bool CacheResponses { get; set; } = true;
    public int ResponseCacheMaxSize { get; set; } = 100;
    public bool ParallelToolExecution { get; set; } = true;
    public int MaxConcurrentTasks { get; set; } = 3;
    public bool LazyLoadHistory { get; set; } = true;
    public int HistoryPageSize { get; set; } = 50;

    // ── UI ──
    public bool ShowTokenCount { get; set; } = true;
    public bool ShowToolCalls { get; set; } = true;
    public bool CompactMode { get; set; } = false;
    public bool MarkdownRendering { get; set; } = true;
    public bool CodeHighlighting { get; set; } = true;
    public bool AnimationsEnabled { get; set; } = true;
    public int SidebarWidth { get; set; } = 60;
    public string DefaultModel { get; set; } = "";
    public string FastModel { get; set; } = "";
    public string ReasoningModel { get; set; } = "";

    // ── Scheduling & Automation ──
    public bool AutoUpdateEnabled { get; set; } = true;
    public bool StartupLaunch { get; set; } = false;
    public bool MinimizeToTray { get; set; } = true;
    public bool KeepAliveOnClose { get; set; } = false;
    public int AutoSaveIntervalSeconds { get; set; } = 30;
    public bool DailyReportEnabled { get; set; } = false;
    public string DailyReportTime { get; set; } = "09:00";
}

public sealed class AdvancedSettingsService
{
    private readonly string _filePath;
    private AdvancedSettings _settings = new();
    private readonly object _lock = new();

    public event Action? Changed;

    public AdvancedSettingsService()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JarvisAI");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "advanced-settings.json");
        Load();
    }

    public AdvancedSettings Get() { lock (_lock) return _settings; }

    public void Update(Action<AdvancedSettings> mutate)
    {
        lock (_lock)
        {
            mutate(_settings);
            Save();
        }
        Changed?.Invoke();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                _settings = JsonSerializer.Deserialize<AdvancedSettings>(json) ?? new();
            }
        }
        catch { _settings = new(); }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }
        catch { }
    }
}
