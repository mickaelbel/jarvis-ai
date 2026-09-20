using System.Text.Json;

namespace JarvisAI.Web.Services;

public enum ExecutionMode
{
    Show,
    Speed
}

/// <summary>
/// 80+ AI-controllable settings. The AI can read/write any of these via the SettingsTool.
/// </summary>
public sealed class AdvancedSettings
{
    // ── AI Behavior ──
    public ExecutionMode Mode { get; set; } = ExecutionMode.Speed;
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

    // ── Iron Man Features ──

    // Face Recognition (#1)
    public bool FaceRecognitionEnabled { get; set; } = false;
    public int WebcamDeviceIndex { get; set; } = 0;
    public bool FaceRecognitionAutoGreet { get; set; } = true;
    public bool FaceRecognitionBackgroundDetect { get; set; } = true;

    // Voice Cloning (#4)
    public bool VoiceCloningEnabled { get; set; } = false;
    public string ClonedVoiceProfile { get; set; } = "";
    public string VoiceCloneLanguage { get; set; } = "french";
    public string PresetVoice { get; set; } = "ryan";

    // AR Overlay (#5)
    public bool OverlayEnabled { get; set; } = false;
    public string OverlayHotkey { get; set; } = "Ctrl+Shift+J";
    public float OverlayOpacity { get; set; } = 0.85f;
    public string OverlayPosition { get; set; } = "top-right";
    public bool OverlayShowWeather { get; set; } = true;
    public bool OverlayShowSystemStats { get; set; } = true;

    // Hologram (#3)
    public bool HologramEnabled { get; set; } = false;

    // Multi-Monitor (#6)
    public bool MultiMonitorEnabled { get; set; } = false;

    // Emotion Detection (#7)
    public bool EmotionDetectionEnabled { get; set; } = false;

    // Network Security (#8)
    public bool NetworkSecurityEnabled { get; set; } = false;

    // Eye Tracking (#10)
    public bool GazeTrackingEnabled { get; set; } = false;
    public bool GazeCalibrated { get; set; } = false;

    // Auto-Improvement (#9)
    public bool SelfImprovementEnabled { get; set; } = true;
    public bool AutoApproveImprovements { get; set; } = false;
    public int MaxImprovementHistory { get; set; } = 50;
}

/// <summary>
/// Snapshot of settings for rollback capability.
/// </summary>
public sealed class SettingsSnapshot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Description { get; set; } = "";
    public string SettingsJson { get; set; } = "";
}

public sealed class AdvancedSettingsService
{
    private readonly string _filePath;
    private readonly string _historyPath;
    private AdvancedSettings _settings = new();
    private readonly object _lock = new();

    public event Action? Changed;

    public AdvancedSettingsService()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JarvisAI");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "advanced-settings.json");
        _historyPath = Path.Combine(dir, "settings-history.json");
        Load();
    }

    public AdvancedSettings Get() { lock (_lock) return Clone(_settings); }

    public void Update(Action<AdvancedSettings> mutate)
    {
        lock (_lock)
        {
            SaveSnapshot("before-update");
            mutate(_settings);
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Reset all settings to their default values. Saves a snapshot before resetting.
    /// </summary>
    public void ResetToDefaults()
    {
        lock (_lock)
        {
            SaveSnapshot("before-reset");
            _settings = new AdvancedSettings();
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Restore settings from a specific snapshot.
    /// </summary>
    public bool RestoreSnapshot(string snapshotId)
    {
        lock (_lock)
        {
            var snapshots = LoadHistory();
            var snapshot = snapshots.FirstOrDefault(s => s.Id == snapshotId);
            if (snapshot is null) return false;

            var restored = JsonSerializer.Deserialize<AdvancedSettings>(snapshot.SettingsJson);
            if (restored is null) return false;

            SaveSnapshot("before-restore");
            _settings = restored;
            Save();
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Get the last N settings snapshots for rollback UI.
    /// </summary>
    public IReadOnlyList<SettingsSnapshot> GetHistory(int limit = 20)
    {
        lock (_lock)
        {
            return LoadHistory().OrderByDescending(s => s.Timestamp).Take(limit).ToList();
        }
    }

    private void SaveSnapshot(string description)
    {
        try
        {
            var snapshots = LoadHistory();
            snapshots.Add(new SettingsSnapshot
            {
                Description = description,
                SettingsJson = JsonSerializer.Serialize(_settings)
            });

            // Keep only last 50 snapshots
            if (snapshots.Count > 50)
                snapshots = snapshots.Skip(snapshots.Count - 50).ToList();

            var json = JsonSerializer.Serialize(snapshots, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_historyPath, json);
        }
        catch { }
    }

    private List<SettingsSnapshot> LoadHistory()
    {
        try
        {
            if (File.Exists(_historyPath))
            {
                var json = File.ReadAllText(_historyPath);
                return JsonSerializer.Deserialize<List<SettingsSnapshot>>(json) ?? new();
            }
        }
        catch { }
        return new();
    }

    private static AdvancedSettings Clone(AdvancedSettings s) => new()
    {
        Mode = s.Mode, Temperature = s.Temperature, MaxTokens = s.MaxTokens,
        TopP = s.TopP, FrequencyPenalty = s.FrequencyPenalty, PresencePenalty = s.PresencePenalty,
        StreamingEnabled = s.StreamingEnabled, ToolPruningEnabled = s.ToolPruningEnabled,
        MaxToolCallsPerTurn = s.MaxToolCallsPerTurn, AutoSummarizeLongContext = s.AutoSummarizeLongContext,
        ContextWindowTokens = s.ContextWindowTokens, AllowMultiStepPlanning = s.AllowMultiStepPlanning,
        MaxPlanningSteps = s.MaxPlanningSteps, EnableReflection = s.EnableReflection,
        EnableSelfCorrection = s.EnableSelfCorrection, ResponseLanguage = s.ResponseLanguage,
        SystemPromptStyle = s.SystemPromptStyle, MemoryEnabled = s.MemoryEnabled,
        MemoryMaxEntries = s.MemoryMaxEntries, AutoRememberFacts = s.AutoRememberFacts,
        ContextPersistence = s.ContextPersistence, ContextRetentionTurns = s.ContextRetentionTurns,
        UseSemanticSearch = s.UseSemanticSearch, SemanticSimilarityThreshold = s.SemanticSimilarityThreshold,
        CompressOldMessages = s.CompressOldMessages, CompressionThresholdTokens = s.CompressionThresholdTokens,
        FileReadWriteEnabled = s.FileReadWriteEnabled, WebSearchEnabled = s.WebSearchEnabled,
        CodeExecutionEnabled = s.CodeExecutionEnabled, ImageGenerationEnabled = s.ImageGenerationEnabled,
        VoiceEnabled = s.VoiceEnabled, BrowserAutomationEnabled = s.BrowserAutomationEnabled,
        GitIntegrationEnabled = s.GitIntegrationEnabled, CalendarIntegrationEnabled = s.CalendarIntegrationEnabled,
        NotificationEnabled = s.NotificationEnabled, AutoCommitEnabled = s.AutoCommitEnabled,
        ProcessManagementEnabled = s.ProcessManagementEnabled, MaxFileSizeKB = s.MaxFileSizeKB,
        WebSearchMaxResults = s.WebSearchMaxResults, CodeExecutionTimeoutMs = s.CodeExecutionTimeoutMs,
        ConfirmDangerousActions = s.ConfirmDangerousActions, SandboxMode = s.SandboxMode,
        AuditLogEnabled = s.AuditLogEnabled, DataEncryptionEnabled = s.DataEncryptionEnabled,
        SessionTimeoutMinutes = s.SessionTimeoutMinutes, AutoLockEnabled = s.AutoLockEnabled,
        AllowedFilePatterns = s.AllowedFilePatterns, BlockedCommands = s.BlockedCommands,
        TelemetryEnabled = s.TelemetryEnabled, ToastNotifications = s.ToastNotifications,
        SoundNotifications = s.SoundNotifications, DesktopNotifications = s.DesktopNotifications,
        NotificationDurationMs = s.NotificationDurationMs, NotifyOnTaskComplete = s.NotifyOnTaskComplete,
        NotifyOnError = s.NotifyOnError, CacheResponses = s.CacheResponses,
        ResponseCacheMaxSize = s.ResponseCacheMaxSize, ParallelToolExecution = s.ParallelToolExecution,
        MaxConcurrentTasks = s.MaxConcurrentTasks, LazyLoadHistory = s.LazyLoadHistory,
        HistoryPageSize = s.HistoryPageSize, ShowTokenCount = s.ShowTokenCount,
        ShowToolCalls = s.ShowToolCalls, CompactMode = s.CompactMode,
        MarkdownRendering = s.MarkdownRendering, CodeHighlighting = s.CodeHighlighting,
        AnimationsEnabled = s.AnimationsEnabled, SidebarWidth = s.SidebarWidth,
        DefaultModel = s.DefaultModel, FastModel = s.FastModel, ReasoningModel = s.ReasoningModel,
        AutoUpdateEnabled = s.AutoUpdateEnabled, StartupLaunch = s.StartupLaunch,
        MinimizeToTray = s.MinimizeToTray, KeepAliveOnClose = s.KeepAliveOnClose,
        AutoSaveIntervalSeconds = s.AutoSaveIntervalSeconds, DailyReportEnabled = s.DailyReportEnabled,
        DailyReportTime = s.DailyReportTime,
        // Iron Man Features
        FaceRecognitionEnabled = s.FaceRecognitionEnabled, WebcamDeviceIndex = s.WebcamDeviceIndex,
        FaceRecognitionAutoGreet = s.FaceRecognitionAutoGreet, FaceRecognitionBackgroundDetect = s.FaceRecognitionBackgroundDetect,
        VoiceCloningEnabled = s.VoiceCloningEnabled, ClonedVoiceProfile = s.ClonedVoiceProfile,
        VoiceCloneLanguage = s.VoiceCloneLanguage, PresetVoice = s.PresetVoice,
        OverlayEnabled = s.OverlayEnabled, OverlayHotkey = s.OverlayHotkey,
        OverlayOpacity = s.OverlayOpacity, OverlayPosition = s.OverlayPosition,
        OverlayShowWeather = s.OverlayShowWeather, OverlayShowSystemStats = s.OverlayShowSystemStats,
        HologramEnabled = s.HologramEnabled, MultiMonitorEnabled = s.MultiMonitorEnabled,
        EmotionDetectionEnabled = s.EmotionDetectionEnabled, NetworkSecurityEnabled = s.NetworkSecurityEnabled,
        GazeTrackingEnabled = s.GazeTrackingEnabled, GazeCalibrated = s.GazeCalibrated,
        SelfImprovementEnabled = s.SelfImprovementEnabled, AutoApproveImprovements = s.AutoApproveImprovements,
        MaxImprovementHistory = s.MaxImprovementHistory,
    };

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
